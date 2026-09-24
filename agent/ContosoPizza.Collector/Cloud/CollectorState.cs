using ContosoPizza.Shared.Wire;

namespace ContosoPizza.Collector.Cloud;

/// <summary>
/// Live state shared between the ingress endpoints, the spool pump and the
/// heartbeat: the configuration the cloud has pushed down, and the health
/// reported back up.
///
/// Cloud-pushed values live here rather than in IOptions because they change at
/// runtime; IOptions binding is start-up configuration.
/// </summary>
public sealed class CollectorState
{
    private long _flowsReceived;
    private long _spansReceived;
    private long _logsReceived;
    private long _recordsDelivered;
    private long _deliveryFailures;
    private long _segmentsDropped;
    private long _metricsDropped;
    private long _scrapeIntervalMs = 2000;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public TimeSpan Uptime => DateTimeOffset.UtcNow - StartedAt;

    // ---- cloud-pushed configuration --------------------------------------

    /// <summary>
    /// The interval the probes are told to use, in the ack to every POST.
    /// Volatile because the heartbeat thread writes it and the request threads
    /// read it.
    /// </summary>
    public int ScrapeIntervalMs
    {
        get => (int)Interlocked.Read(ref _scrapeIntervalMs);
        set => Interlocked.Exchange(ref _scrapeIntervalMs, value);
    }

    /// <summary>
    /// Lets the cloud pause a noisy fleet without anyone touching a server. The
    /// collector keeps running and keeps heartbeating, so health still reports.
    /// </summary>
    public bool CollectionEnabled { get; set; } = true;

    public int ConfigVersion { get; set; } = 1;

    public void Apply(CloudConfig? config)
    {
        if (config is null)
        {
            return;
        }

        // Clamped again here. The dashboard clamps, and the probe clamps; this
        // is the one in the middle, and it is what protects a fleet if a future
        // caller writes to the store without going through the router.
        ScrapeIntervalMs = Math.Clamp(config.ScrapeIntervalMs, 250, 300_000);
        CollectionEnabled = config.CollectionEnabled;
    }

    // ---- health reported upward ------------------------------------------

    public bool CloudAuthenticated { get; set; }

    public DateTimeOffset? LastSuccessfulDelivery { get; set; }

    public string? LastDeliveryError { get; set; }

    public int SpoolSegmentsPending { get; set; }

    public long SpoolBytes { get; set; }

    public long FlowsReceived => Interlocked.Read(ref _flowsReceived);

    public long SpansReceived => Interlocked.Read(ref _spansReceived);

    public long LogsReceived => Interlocked.Read(ref _logsReceived);

    public long RecordsDelivered => Interlocked.Read(ref _recordsDelivered);

    public long DeliveryFailures => Interlocked.Read(ref _deliveryFailures);

    public long SegmentsDropped => Interlocked.Read(ref _segmentsDropped);

    /// <summary>
    /// OTLP metric points received and not forwarded. Reported rather than
    /// silently discarded: the cloud intake takes logs, spans and flows, and an
    /// operator who pointed a metrics exporter here deserves to see the number
    /// climbing instead of wondering where their metrics went.
    /// </summary>
    public long MetricsDropped => Interlocked.Read(ref _metricsDropped);

    public void AddFlowsReceived(int count) => Interlocked.Add(ref _flowsReceived, count);

    public void AddSpansReceived(int count) => Interlocked.Add(ref _spansReceived, count);

    public void AddLogsReceived(int count) => Interlocked.Add(ref _logsReceived, count);

    public void AddRecordsDelivered(int count) => Interlocked.Add(ref _recordsDelivered, count);

    public void AddMetricsDropped(int count) => Interlocked.Add(ref _metricsDropped, count);

    public void IncrementDeliveryFailures() => Interlocked.Increment(ref _deliveryFailures);

    public void AddSegmentsDropped(int count) => Interlocked.Add(ref _segmentsDropped, count);

    /// <summary>What the heartbeat carries up, and what the dashboard renders.</summary>
    public Dictionary<string, object?> Health() => new()
    {
        ["uptime_seconds"] = (long)Uptime.TotalSeconds,
        ["cloud_authenticated"] = CloudAuthenticated,
        ["flows_received"] = FlowsReceived,
        ["spans_received"] = SpansReceived,
        ["logs_received"] = LogsReceived,
        ["records_delivered"] = RecordsDelivered,
        ["delivery_failures"] = DeliveryFailures,
        ["spool_segments_pending"] = SpoolSegmentsPending,
        ["spool_bytes"] = SpoolBytes,
        ["segments_dropped"] = SegmentsDropped,
        ["otlp_metrics_dropped"] = MetricsDropped,
        ["last_successful_delivery"] = LastSuccessfulDelivery?.ToString("O"),
        ["last_delivery_error"] = LastDeliveryError,
        ["collection_enabled"] = CollectionEnabled,
        ["scrape_interval_ms"] = ScrapeIntervalMs,
    };
}
