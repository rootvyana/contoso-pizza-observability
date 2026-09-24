using System.Diagnostics;
using ContosoPizza.Probe.Shipping;
using ContosoPizza.Probe.Sources;
using ContosoPizza.Shared.Model;
using ContosoPizza.Shared.Wire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Probe;

/// <summary>
/// The whole probe: read the kernel maps, hand what closed to the collector,
/// repeat at whatever interval the collector last asked for.
///
/// Everything this used to also do -- spooling to disk, holding a cloud
/// credential, exporting its own OpenTelemetry, running a control-channel
/// WebSocket -- now happens once, in the collector, instead of once per server.
/// </summary>
public sealed class ProbeService : BackgroundService
{
    private readonly IFlowSource _source;
    private readonly CollectorClient _collector;
    private readonly ProbeOptions _options;
    private readonly ILogger<ProbeService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public ProbeService(
        IFlowSource source,
        CollectorClient collector,
        IOptions<ProbeOptions> options,
        ILogger<ProbeService> logger,
        IHostApplicationLifetime lifetime)
    {
        _source = source;
        _collector = collector;
        _options = options.Value;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_source.TryConnect(out string? error))
        {
            _logger.LogError("Cannot attach to flow source '{Source}': {Error}", _source.Name, error);
            _lifetime.StopApplication();
            return;
        }

        var interval = _options.ScrapeInterval;

        _logger.LogInformation(
            "Probing '{Source}' every {Interval}, shipping to {Url} as server '{Server}'",
            _source.Name, interval, _collector.FlowsUrl, _options.ResolveServerName());

        using var timer = new PeriodicTimer(interval);

        await ScrapeAndShipAsync(stoppingToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // The collector, not this file, decides the cadence once the two are
            // talking -- so a dashboard change reaches every server without
            // anybody logging into one.
            if (_collector.ScrapeInterval > TimeSpan.Zero && _collector.ScrapeInterval != interval)
            {
                interval = _collector.ScrapeInterval;
                timer.Period = interval;
            }

            await ScrapeAndShipAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ScrapeAndShipAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            FlowSnapshot snapshot = _collector.CollectionEnabled
                ? _source.Scrape()
                : EmptySnapshot;

            var observedAt = DateTimeOffset.UtcNow;
            var flows = new List<ProbeFlow>(snapshot.ClosedFlows.Count);

            foreach (var flow in snapshot.ClosedFlows)
            {
                flows.Add(ToWire(flow, observedAt));
            }

            var counters = new ProbeCounters
            {
                Established = (long)snapshot.Counters.Established,
                Closed = (long)snapshot.Counters.Deleted,
                CurrentOpen = (long)snapshot.Counters.CurrentOpen,
                MapEntries = snapshot.MapEntries,
                MapCapacity = snapshot.MapCapacity,
            };

            await _collector.SendAsync(flows, counters, ct).ConfigureAwait(false);

            stopwatch.Stop();

            if (flows.Count > 0)
            {
                _logger.LogDebug(
                    "Scraped {Closed} closed flow(s) in {Elapsed}ms, {Buffered} buffered",
                    flows.Count, stopwatch.ElapsedMilliseconds, _collector.Buffered);
            }

            // The eBPF program ignores the return value of bpf_map_update_elem,
            // so a full map fails silently in the kernel. Say so loudly here.
            if (snapshot.MapCapacity > 0 && snapshot.MapEntries >= snapshot.MapCapacity * 0.9)
            {
                _logger.LogWarning(
                    "flow_map is {Entries}/{Capacity} full. Once it fills, the kernel drops new "
                  + "flows without reporting an error.", snapshot.MapEntries, snapshot.MapCapacity);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape failed");
        }
    }

    /// <summary>
    /// Flattened for the wire. Duration is milliseconds rather than a TimeSpan
    /// because the far end is Python and would have to parse a .NET duration
    /// string; the kernel gives nanoseconds, and a double of milliseconds keeps
    /// sub-millisecond flows from rounding to zero.
    /// </summary>
    private static ProbeFlow ToWire(FlowRecord flow, DateTimeOffset observedAt) => new()
    {
        // Stable across a redelivery of the same flow: the four-tuple plus the
        // moment it ended identifies one connection, so an at-least-once retry
        // overwrites its own row rather than counting the connection twice.
        Id = FlowId(flow, observedAt),
        ObservedAt = observedAt,
        LocalAddress = flow.LocalAddress.ToString(),
        LocalPort = flow.LocalPort,
        RemoteAddress = flow.RemoteAddress.ToString(),
        RemotePort = flow.RemotePort,
        Direction = flow.IsOutbound ? "outbound" : "inbound",
        State = flow.IsOpen ? "open" : "closed",
        DurationMs = flow.Duration.TotalMilliseconds,
        ProcessId = flow.ProcessId,
        ProcessName = flow.ProcessName,
        ClientPort = flow.ClientPort,
    };

    private static string FlowId(FlowRecord flow, DateTimeOffset observedAt)
    {
        Span<byte> buffer = stackalloc byte[32];
        int written = 0;

        BitConverter.TryWriteBytes(buffer[written..], flow.LocalPort); written += 2;
        BitConverter.TryWriteBytes(buffer[written..], flow.RemotePort); written += 2;
        BitConverter.TryWriteBytes(buffer[written..], flow.ProcessId); written += 4;
        BitConverter.TryWriteBytes(buffer[written..], observedAt.ToUnixTimeMilliseconds()); written += 8;
        BitConverter.TryWriteBytes(buffer[written..], flow.Duration.Ticks); written += 8;

        var hash = System.Security.Cryptography.SHA256.HashData(buffer[..written]);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static FlowSnapshot EmptySnapshot => new()
    {
        ClosedFlows = [],
        ActiveFlows = [],
        Counters = default,
        MapEntries = 0,
        MapCapacity = 0,
    };

    public override void Dispose()
    {
        _source.Dispose();
        base.Dispose();
    }
}
