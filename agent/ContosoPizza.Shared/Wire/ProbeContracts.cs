using System.Text.Json.Serialization;

namespace ContosoPizza.Shared.Wire;

/// <summary>
/// What a probe sends to the collector, over the LAN.
///
/// This hop is deliberately the dumbest thing that works: one POST of plain
/// JSON, no batching protocol, no acknowledgement semantics beyond the HTTP
/// status. Durability lives in the collector, which is the single place that
/// spools to disk and the single place that holds a cloud credential. A probe
/// that cannot reach the collector holds a small in-memory buffer and then
/// drops, because a probe that spools to disk is a second copy of the
/// collector running on every server -- which is the design this replaced.
/// </summary>
public sealed record ProbeBatch
{
    /// <summary>The monitored server. Becomes host.name on every record.</summary>
    [JsonPropertyName("server")]
    public required string Server { get; init; }

    /// <summary>Stable per-installation id, so restarts do not look like new servers.</summary>
    [JsonPropertyName("probeId")]
    public required string ProbeId { get; init; }

    [JsonPropertyName("probeVersion")]
    public required string ProbeVersion { get; init; }

    [JsonPropertyName("sentAt")]
    public required DateTimeOffset SentAt { get; init; }

    [JsonPropertyName("flows")]
    public required IReadOnlyList<ProbeFlow> Flows { get; init; }

    [JsonPropertyName("counters")]
    public ProbeCounters? Counters { get; init; }
}

/// <summary>One completed TCP connection, flattened for the wire.</summary>
public sealed record ProbeFlow
{
    /// <summary>
    /// Stable id for this flow. The delivery path is at-least-once, so the same
    /// flow can arrive twice; a stable id lets the far end overwrite its own row
    /// instead of double-counting it.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("observedAt")]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyName("localAddress")]
    public required string LocalAddress { get; init; }

    [JsonPropertyName("localPort")]
    public required int LocalPort { get; init; }

    [JsonPropertyName("remoteAddress")]
    public required string RemoteAddress { get; init; }

    [JsonPropertyName("remotePort")]
    public required int RemotePort { get; init; }

    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("durationMs")]
    public required double DurationMs { get; init; }

    [JsonPropertyName("processId")]
    public required long ProcessId { get; init; }

    [JsonPropertyName("processName")]
    public string? ProcessName { get; init; }

    /// <summary>
    /// The client's ephemeral port -- the join key back into the application's
    /// OpenTelemetry spans, which carry client.port. Null on outbound rows,
    /// where the remote port is the server's and joins nothing.
    /// </summary>
    [JsonPropertyName("clientPort")]
    public int? ClientPort { get; init; }
}

/// <summary>Kernel-side totals, carried so the collector can report probe health.</summary>
public sealed record ProbeCounters
{
    [JsonPropertyName("established")]
    public long Established { get; init; }

    [JsonPropertyName("closed")]
    public long Closed { get; init; }

    [JsonPropertyName("currentOpen")]
    public long CurrentOpen { get; init; }

    [JsonPropertyName("mapEntries")]
    public int MapEntries { get; init; }

    [JsonPropertyName("mapCapacity")]
    public long MapCapacity { get; init; }
}

/// <summary>The collector's reply. Carries configuration back down to the probe.</summary>
public sealed record ProbeAck
{
    [JsonPropertyName("accepted")]
    public int Accepted { get; init; }

    /// <summary>
    /// Scrape interval the probe should use from now on. Originates in the
    /// dashboard, reaches the collector on its heartbeat, and reaches the probe
    /// here -- so one setting change retunes an entire fleet without anybody
    /// logging into a server.
    /// </summary>
    [JsonPropertyName("scrapeIntervalMs")]
    public int ScrapeIntervalMs { get; init; }

    [JsonPropertyName("collectionEnabled")]
    public bool CollectionEnabled { get; init; } = true;
}
