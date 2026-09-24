using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ContosoPizza.Probe.Shipping;
using ContosoPizza.Probe.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Probe;

/// <summary>
/// One of these runs on every monitored server. It reads that machine's eBPF
/// maps and ships what it finds to the collector on the LAN -- nothing else.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The probe reads eBPF for Windows maps and only runs on Windows.");
            return 1;
        }

        // --exports: verify EbpfApi.dll loads and every entry point we P/Invoke
        // resolves. Needs no elevation; it only reads the export table.
        if (args.Contains("--exports") || args.Contains("--probe"))
        {
            return ProbeExports(args);
        }

        // --check: can this server reach its collector? Unprivileged, so a
        // network admin can answer the most common install failure without
        // being a local administrator.
        if (args.Contains("--check"))
        {
            return await CheckAsync().ConfigureAwait(false);
        }

        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "Not elevated. Reading eBPF maps requires administrator rights, the same as bpftool.\n" +
                "Re-open PowerShell as Administrator and run this again.");
            return 1;
        }

        if (args.Contains("--dump"))
        {
            return DumpOnce(args);
        }

        var builder = Host.CreateApplicationBuilder(args);

        ConfigureWindowsServiceHosting(builder);

        builder.Services.Configure<ProbeOptions>(
            builder.Configuration.GetSection(ProbeOptions.SectionName));

        var options = builder.Configuration
            .GetSection(ProbeOptions.SectionName).Get<ProbeOptions>() ?? new ProbeOptions();

        var identity = ProbeIdentity.LoadOrCreate(ProbeIdentity.DefaultStateDirectory);
        builder.Services.AddSingleton(identity);

        // One handler, reused for the life of the process: the LAN hop runs at
        // the scrape interval forever, and a fresh TCP connection per scrape is
        // a socket churned every two seconds on every server in the estate.
        builder.Services.AddHttpClient("collector", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            });

        builder.Services.AddSingleton<CollectorClient>();
        builder.Services.AddSingleton<IFlowSource>(sp => new EbpfFlowSource(
            sp.GetRequiredService<ILogger<EbpfFlowSource>>(),
            sp.GetRequiredService<IOptions<ProbeOptions>>().Value.EbpfInstallPath));

        builder.Services.AddHostedService<ProbeService>();

        var host = builder.Build();

        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
        log.LogInformation(
            "contoso-probe {Version} on {Server} (probe {ProbeId})",
            CollectorClient.ProbeVersion, options.ResolveServerName(), identity.ProbeId);
        log.LogInformation("Collector: {Url}/v1/flows", options.CollectorEndpoint.TrimEnd('/'));

        if (string.IsNullOrEmpty(options.ResolveProbeKey()))
        {
            log.LogWarning(
                "No probe key set. If the collector has one configured it will answer 401. "
              + "Set CONTOSO_PROBE_KEY (or Probe:ProbeKey).");
        }

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static ProbeOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.GetSection(ProbeOptions.SectionName).Get<ProbeOptions>()
               ?? new ProbeOptions();
    }

    /// <summary>
    /// Reachability test for the collector. Deliberately does not need the
    /// kernel or elevation: "can this server talk to the collector" is the
    /// question that fails first and most often on a new install.
    /// </summary>
    private static async Task<int> CheckAsync()
    {
        var options = LoadOptions();
        string url = $"{options.CollectorEndpoint.TrimEnd('/')}/v1/flows";

        Console.WriteLine("Probe connectivity check");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"Server    : {options.ResolveServerName()}");
        Console.WriteLine($"Probe id  : {ProbeIdentity.LoadOrCreate(ProbeIdentity.DefaultStateDirectory).ProbeId}");
        Console.WriteLine($"Probe key : {(string.IsNullOrEmpty(options.ResolveProbeKey()) ? "not set" : "set")}");
        Console.WriteLine();
        Console.Write($"collector  {url} ... ");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        try
        {
            // An empty batch is a valid POST, so this exercises the real route
            // and the real authentication rather than a health endpoint that
            // would answer even when the probe key is wrong.
            string body = $$"""
                {"server":"{{options.ResolveServerName()}}","probeId":"check",
                 "probeVersion":"{{CollectorClient.ProbeVersion}}",
                 "sentAt":"{{DateTimeOffset.UtcNow:O}}","flows":[]}
                """;

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            if (options.ResolveProbeKey() is { Length: > 0 } key)
            {
                request.Headers.TryAddWithoutValidation("X-Probe-Key", key);
            }

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            Console.WriteLine($"HTTP {(int)response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine();
                Console.WriteLine("PASS: the collector accepted this probe.");
                return 0;
            }

            if ((int)response.StatusCode is 401 or 403)
            {
                Console.WriteLine();
                Console.Error.WriteLine("FAIL: reachable but rejected. CONTOSO_PROBE_KEY does not match");
                Console.Error.WriteLine("      the collector's Probe:Key.");
                return 1;
            }

            Console.Error.WriteLine(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.WriteLine();
            Console.Error.WriteLine("The collector is not reachable from this server. Check that it is");
            Console.Error.WriteLine("running, and that nothing between here and it blocks the port.");
            return 1;
        }
    }

    /// <summary>
    /// Lets the same binary run as a Windows service without a separate host
    /// path. Harmless when launched from a console.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ConfigureWindowsServiceHosting(HostApplicationBuilder builder)
    {
        builder.Services.AddWindowsService(options => options.ServiceName = "ContosoProbe");

        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Logging.AddEventLog(settings => settings.SourceName = "ContosoProbe");
        }
    }

    /// <summary>
    /// One-shot map read to the console. Proves the interop layer works without
    /// involving the collector at all.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int DumpOnce(string[] args)
    {
        string installPath = args
            .SkipWhile(a => a != "--install-path")
            .Skip(1)
            .FirstOrDefault() ?? Interop.EbpfApi.DefaultInstallPath;

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Debug)
            .AddSimpleConsole(o => o.SingleLine = true));

        using var source = new EbpfFlowSource(
            loggerFactory.CreateLogger<EbpfFlowSource>(), installPath);

        if (!source.TryConnect(out string? error))
        {
            Console.Error.WriteLine($"FAILED: {error}");
            return 1;
        }

        var snapshot = source.Scrape();

        Console.WriteLine();
        Console.WriteLine($"{"LOCAL",-22} {"PEER",-22} {"CLIENTPORT",-11} {"DIR",-4} {"PID",-6} {"PROCESS",-14} {"STATE",-7} DURATION_MS");
        Console.WriteLine(new string('-', 110));

        foreach (var flow in snapshot.ActiveFlows.Concat(snapshot.ClosedFlows))
        {
            Console.WriteLine(
                $"{flow.LocalAddress + ":" + flow.LocalPort,-22} " +
                $"{flow.RemoteAddress + ":" + flow.RemotePort,-22} " +
                $"{flow.ClientPort?.ToString() ?? "",-11} " +
                $"{(flow.IsOutbound ? "out" : "in"),-4} " +
                $"{flow.ProcessId,-6} " +
                $"{flow.ProcessName ?? "(exited)",-14} " +
                $"{(flow.IsOpen ? "OPEN" : "CLOSED"),-7} " +
                $"{(flow.Duration > TimeSpan.Zero ? flow.Duration.TotalMilliseconds.ToString("F1") : "-")}");
        }

        Console.WriteLine();
        Console.WriteLine($"established    : {snapshot.Counters.Established}");
        Console.WriteLine($"closed         : {snapshot.Counters.Deleted}");
        Console.WriteLine($"currently open : {snapshot.Counters.CurrentOpen}");
        Console.WriteLine($"flow_map       : {snapshot.MapEntries}/{snapshot.MapCapacity} entries");
        Console.WriteLine();
        Console.WriteLine(
            $"NOTE: {snapshot.ClosedFlows.Count} closed flow(s) were evicted from flow_map by this read.");

        return 0;
    }

    /// <summary>
    /// Load the native library and resolve every export the probe depends on.
    /// Runs unprivileged: it reads the export table, it does not call anything.
    /// </summary>
    private static int ProbeExports(string[] args)
    {
        string installPath = args
            .SkipWhile(a => a != "--install-path")
            .Skip(1)
            .FirstOrDefault() ?? Interop.EbpfApi.DefaultInstallPath;

        string dll = Path.Combine(installPath, "EbpfApi.dll");
        Console.WriteLine($"Probing {dll}");

        if (!File.Exists(dll))
        {
            Console.Error.WriteLine("FAILED: file not found.");
            return 1;
        }

        IntPtr handle;
        try
        {
            handle = NativeLibrary.Load(dll);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED to load: {ex.Message}");
            return 1;
        }

        string[] required =
        [
            "bpf_map_get_next_id",
            "bpf_map_get_fd_by_id",
            "bpf_obj_get_info_by_fd",
            "bpf_map_get_next_key",
            "bpf_map_lookup_elem",
            "bpf_map_delete_elem",
            "ebpf_close_fd",
        ];

        int missing = 0;
        Console.WriteLine();

        foreach (string export in required)
        {
            bool found = NativeLibrary.TryGetExport(handle, export, out IntPtr address);
            Console.WriteLine($"  {(found ? "OK  " : "MISS")}  {export}{(found ? $"  @ 0x{address:x}" : "")}");
            if (!found)
            {
                missing++;
            }
        }

        NativeLibrary.Free(handle);

        Console.WriteLine();
        if (missing > 0)
        {
            Console.Error.WriteLine($"FAILED: {missing} export(s) missing.");
            return 1;
        }

        Console.WriteLine("All required exports resolved. Interop surface is sound.");
        Console.WriteLine("Next: run elevated with --dump to read the maps.");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
