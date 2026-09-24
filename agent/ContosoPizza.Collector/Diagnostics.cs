using ContosoPizza.Collector.Cloud;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Collector;

/// <summary>
/// The counterpart to the Power BI gateway's network ports test: can this
/// machine reach the cloud, and will it be let in?
///
/// "Can this machine reach the service" is the single most common deployment
/// failure, and it is worth answering without starting the collector, without
/// touching the spool, and without needing administrator rights.
/// </summary>
internal static class Diagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var cloud = configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>()
                    ?? new CloudOptions();

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => o.SingleLine = true));

        var store = new CredentialStore(
            cloud.StateDirectory, loggerFactory.CreateLogger<CredentialStore>());
        var stored = store.Load();

        Console.WriteLine("Collector connectivity diagnostics");
        Console.WriteLine(new string('-', 64));
        Console.WriteLine($"Cloud endpoint  : {cloud.Endpoint}");
        Console.WriteLine($"State directory : {cloud.StateDirectory}");
        Console.WriteLine($"Enrolled        : {(stored is null ? "no" : $"yes, as {stored.CollectorId}")}");
        Console.WriteLine($"Enrollment token: {(cloud.ResolveEnrollmentToken().Length > 0 ? "set" : "not set")}");
        Console.WriteLine($"Env selector    : {(cloud.ResolveEnvToken().Length > 0 ? "set" : "not set")}");
        Console.WriteLine();

        // Same selector the running collector sends. A diagnostic that talks to a
        // different backend than the agent does is worse than no diagnostic.
        using var http = new HttpClient(
            new EnvSelectorHandler(cloud.ResolveEnvToken()) { InnerHandler = new HttpClientHandler() })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var state = new CollectorState();

        var session = new CloudSession(
            http, store, Options.Create(cloud), state,
            loggerFactory.CreateLogger<CloudSession>());

        Console.Write("authenticating ... ");

        string? token = await session.GetSessionAsync(CancellationToken.None).ConfigureAwait(false);

        if (token is null)
        {
            Console.WriteLine("FAILED");
            Console.WriteLine();
            Console.Error.WriteLine("The collector could not obtain a session. The log above says why.");
            Console.Error.WriteLine("The usual causes, in order:");
            Console.Error.WriteLine("  * not enrolled yet   -> set CONTOSO_COLLECTOR_ENROLLMENT_TOKEN");
            Console.Error.WriteLine("  * token already used -> issue a new one from the dashboard");
            Console.Error.WriteLine("  * endpoint wrong     -> check Cloud:Endpoint");
            Console.Error.WriteLine("  * credential revoked -> register a new collector");
            return 1;
        }

        Console.WriteLine("OK");
        Console.WriteLine($"collector id    : {session.CollectorId}");
        Console.WriteLine();

        Console.Write("heartbeat      ... ");

        using var request = new HttpRequestMessage(HttpMethod.Post, cloud.HeartbeatUrl)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { health = state.Health() }),
        };

        CloudSession.Authorize(request, token);

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        Console.WriteLine($"HTTP {(int)response.StatusCode}");

        // Local spool health, the other half of "is this collector fine".
        string spoolDirectory = Path.Combine(cloud.StateDirectory, "spool");

        if (Directory.Exists(spoolDirectory))
        {
            var segments = Directory.GetFiles(spoolDirectory, "*.seg");
            long bytes = segments.Sum(f => new FileInfo(f).Length);
            Console.WriteLine($"spool           : {segments.Length} segment(s) pending, {bytes:N0} bytes");
        }
        else
        {
            Console.WriteLine("spool           : empty");
        }

        Console.WriteLine();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine("Authenticated, but the heartbeat was refused. See the status above.");
            return 1;
        }

        Console.WriteLine("All checks passed. This collector can reach loveheartbeat and is authenticated.");
        return 0;
    }
}
