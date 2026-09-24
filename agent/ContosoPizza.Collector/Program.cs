using System.Runtime.Versioning;
using ContosoPizza.Collector.Cloud;
using ContosoPizza.Collector.Fleet;
using ContosoPizza.Collector.Receivers;
using ContosoPizza.Collector.Spool;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Collector;

/// <summary>
/// One of these runs per site. Every probe and every instrumented application
/// on the network reports to it, and it is the only thing that holds a
/// credential for loveheartbeat.com or knows how to reach it.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--diagnose"))
        {
            return await Diagnostics.RunAsync(args).ConfigureAwait(false);
        }

        var builder = WebApplication.CreateBuilder(args);

        if (OperatingSystem.IsWindows())
        {
            ConfigureWindowsServiceHosting(builder);
        }

        builder.Services.Configure<CloudOptions>(
            builder.Configuration.GetSection(CloudOptions.SectionName));
        builder.Services.Configure<IngressOptions>(
            builder.Configuration.GetSection(IngressOptions.SectionName));

        var cloud = builder.Configuration
            .GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();
        var ingress = builder.Configuration
            .GetSection(IngressOptions.SectionName).Get<IngressOptions>() ?? new IngressOptions();

        builder.Services.AddSingleton<CollectorState>();
        builder.Services.AddSingleton<FleetRegistry>();

        builder.Services.AddSingleton(sp => new CredentialStore(
            cloud.StateDirectory, sp.GetRequiredService<ILogger<CredentialStore>>()));

        builder.Services.AddSingleton(sp => new EventSpool(
            cloud.StateDirectory,
            cloud.MaxSpoolBytes,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<EventSpool>()));

        builder.Services.AddSingleton<SpoolWriter>();

        // One HttpClient for the life of the process, shared by all three cloud
        // callers. This is the "persistent connection" half of staying connected: the
        // TCP and TLS handshake is paid once rather than every five seconds, and the
        // session on top of it is renewed before it can expire. Together they mean a
        // collector running for a month is still using the socket and the credential
        // it started with.
        //
        // Built here rather than with AddHttpClient<T>, and that is not a style
        // choice. These three must be singletons -- two CloudSessions would race to
        // renew each other's token -- but AddHttpClient<T> registers T as transient,
        // so the AddSingleton<T>() that followed it silently *replaced* the typed
        // registration. Each one then got the container's default HttpClient: no
        // pooling settings, and no env selector. It still reached a server, which is
        // why it looked fine until a deployment that needs the selector refused
        // everything.
        //
        // IHttpClientFactory's handler rotation exists to stop a captured handler
        // going stale on DNS; PooledConnectionLifetime below does that job directly,
        // which is what makes one long-lived client the right shape here.
        string envToken = cloud.ResolveEnvToken();

        var cloudHttp = new HttpClient(
            new EnvSelectorHandler(envToken) { InnerHandler = CloudHandler() });
        ConfigureCloudClient(cloudHttp);

        builder.Services.AddSingleton(sp => new CloudSession(
            cloudHttp,
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<IOptions<CloudOptions>>(),
            sp.GetRequiredService<CollectorState>(),
            sp.GetRequiredService<ILogger<CloudSession>>()));

        builder.Services.AddSingleton(sp => new SpoolPump(
            sp.GetRequiredService<EventSpool>(),
            sp.GetRequiredService<CollectorState>(),
            sp.GetRequiredService<CloudSession>(),
            sp.GetRequiredService<IOptions<CloudOptions>>(),
            cloudHttp,
            sp.GetRequiredService<ILogger<SpoolPump>>()));

        builder.Services.AddSingleton(sp => new HeartbeatService(
            sp.GetRequiredService<CloudSession>(),
            sp.GetRequiredService<CollectorState>(),
            sp.GetRequiredService<FleetRegistry>(),
            sp.GetRequiredService<IOptions<CloudOptions>>(),
            cloudHttp,
            sp.GetRequiredService<ILogger<HeartbeatService>>()));

        builder.Services.AddHostedService(sp => sp.GetRequiredService<SpoolPump>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatService>());

        var app = builder.Build();

        app.MapIngress();

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
        log.LogInformation("contoso-collector {Version}", CloudSession.CollectorVersion);
        log.LogInformation("Cloud          : {Endpoint}", cloud.Endpoint);
        log.LogInformation("Env selector   : {State}",
            envToken.Length > 0 ? "set" : "not set (talking straight to a backend)");
        log.LogInformation("State directory: {Dir}", cloud.StateDirectory);

        if (ingress.ResolveProbeKey().Length == 0)
        {
            log.LogWarning(
                "No probe key set: this collector accepts flows and OTLP from anything that can "
              + "reach it. Set CONTOSO_PROBE_KEY (or Ingress:ProbeKey) on the collector and on "
              + "every probe.");
        }

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ConfigureCloudClient(HttpClient client)
    {
        // Short enough that a stalled upload cannot wedge the pump behind it,
        // long enough for a large batch over a slow link.
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"contoso-collector/{CloudSession.CollectorVersion}");
    }

    private static SocketsHttpHandler CloudHandler() => new()
    {
        // Recycled every 15 minutes so the connection follows DNS when the
        // cloud endpoint moves -- a pool that never recycles keeps talking to
        // an address that has been withdrawn.
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
        // Keeps the idle connection alive through NAT and load-balancer idle
        // timeouts, which is what otherwise silently drops a collector's
        // socket overnight and costs a handshake on the next batch.
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
    };

    [SupportedOSPlatform("windows")]
    private static void ConfigureWindowsServiceHosting(WebApplicationBuilder builder)
    {
        builder.Services.AddWindowsService(options => options.ServiceName = "ContosoCollector");

        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Logging.AddEventLog(settings => settings.SourceName = "ContosoCollector");
        }
    }
}
