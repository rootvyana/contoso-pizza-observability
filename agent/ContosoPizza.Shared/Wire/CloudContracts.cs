using System.Text.Json.Serialization;

namespace ContosoPizza.Shared.Wire;

/// <summary>
/// The collector's side of the loveheartbeat.com contract. Mirrors
/// backend/handlers/routers/collectors.py; the field names are what that router
/// deserialises, so they are snake_case where it is and camelCase where it is.
/// </summary>
public sealed record EnrollRequest
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed record EnrollResponse
{
    [JsonPropertyName("collector_id")]
    public string CollectorId { get; init; } = "";

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    /// <summary>
    /// The durable credential. It has no expiry, which is the whole point: this
    /// is the one secret the collector persists, and everything else it needs it
    /// can mint from this without a human.
    /// </summary>
    [JsonPropertyName("collector_credential")]
    public string CollectorCredential { get; init; } = "";

    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = "";

    [JsonPropertyName("session_expires_at")]
    public DateTimeOffset? SessionExpiresAt { get; init; }

    [JsonPropertyName("renew_after_seconds")]
    public int RenewAfterSeconds { get; init; }

    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; init; }

    [JsonPropertyName("config")]
    public CloudConfig? Config { get; init; }

    [JsonPropertyName("config_version")]
    public int ConfigVersion { get; init; }
}

public sealed record SessionRequest
{
    [JsonPropertyName("collector_id")]
    public required string CollectorId { get; init; }
}

public sealed record SessionResponse
{
    [JsonPropertyName("collector_id")]
    public string CollectorId { get; init; } = "";

    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = "";

    [JsonPropertyName("session_expires_at")]
    public DateTimeOffset? SessionExpiresAt { get; init; }

    [JsonPropertyName("renew_after_seconds")]
    public int RenewAfterSeconds { get; init; }

    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; init; }

    [JsonPropertyName("config")]
    public CloudConfig? Config { get; init; }

    [JsonPropertyName("config_version")]
    public int ConfigVersion { get; init; }
}

/// <summary>Settings the dashboard pushes down; applied on the next heartbeat.</summary>
public sealed record CloudConfig
{
    [JsonPropertyName("scrape_interval_ms")]
    public int ScrapeIntervalMs { get; init; } = 2000;

    [JsonPropertyName("batch_size")]
    public int BatchSize { get; init; } = 200;

    [JsonPropertyName("collection_enabled")]
    public bool CollectionEnabled { get; init; } = true;
}

public sealed record HeartbeatRequest
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Which servers are currently reporting through this collector.</summary>
    [JsonPropertyName("servers")]
    public IReadOnlyList<string> Servers { get; init; } = [];

    [JsonPropertyName("health")]
    public IReadOnlyDictionary<string, object?> Health { get; init; } =
        new Dictionary<string, object?>();
}

public sealed record HeartbeatResponse
{
    [JsonPropertyName("collector_id")]
    public string CollectorId { get; init; } = "";

    [JsonPropertyName("session_expires_at")]
    public DateTimeOffset? SessionExpiresAt { get; init; }

    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; init; }

    [JsonPropertyName("config")]
    public CloudConfig? Config { get; init; }

    [JsonPropertyName("config_version")]
    public int ConfigVersion { get; init; }
}

/// <summary>
/// One telemetry batch. The three lists are the three things a fleet produces:
/// application spans, application logs, and kernel flows. They travel together
/// because they describe the same moment on the same machines, and splitting
/// them would mean three deliveries to keep consistent instead of one.
/// </summary>
public sealed record CloudTelemetryBatch
{
    [JsonPropertyName("logs")]
    public IReadOnlyList<Dictionary<string, object?>> Logs { get; init; } = [];

    [JsonPropertyName("spans")]
    public IReadOnlyList<Dictionary<string, object?>> Spans { get; init; } = [];

    [JsonPropertyName("flows")]
    public IReadOnlyList<Dictionary<string, object?>> Flows { get; init; } = [];
}

/// <summary>
/// A record on its way out, as one line in a spool segment.
///
/// The kind travels with the payload rather than being implied by which file it
/// is in: a segment is written by several producers at once (the probe
/// endpoint, the OTLP endpoint) and splitting the spool per kind would mean
/// three queues with three independent orderings and three chances to drain
/// unevenly during an outage.
/// </summary>
public sealed record SpooledRecord
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("record")]
    public required Dictionary<string, object?> Record { get; init; }

    public const string KindFlow = "flow";
    public const string KindSpan = "span";
    public const string KindLog = "log";
}
