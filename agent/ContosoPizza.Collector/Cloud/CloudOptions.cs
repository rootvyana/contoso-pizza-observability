namespace ContosoPizza.Collector.Cloud;

/// <summary>
/// How the collector reaches loveheartbeat.com, and how it proves who it is.
///
/// Connectivity follows the Power BI on-premises data gateway's rule: every
/// connection is outbound over 443, and no inbound port to the internet is ever
/// opened. The collector does listen -- but only on the LAN, for its own
/// probes and for the applications exporting OpenTelemetry to it.
/// </summary>
public sealed class CloudOptions
{
    public const string SectionName = "Cloud";

    /// <summary>
    /// Base URL of the loveheartbeat API, e.g. https://api.loveheartbeat.com.
    /// The collector routes are appended: /api/v1/collectors/...
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>
    /// The one-time token from the dashboard ("Add collector"). Used exactly
    /// once, on first start, and never needed again -- after enrollment the
    /// collector holds a durable credential of its own.
    ///
    /// Prefer the CONTOSO_COLLECTOR_ENROLLMENT_TOKEN environment variable: this
    /// is a bearer secret and config files get committed by accident.
    /// </summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>
    /// Pre-issued credential, for a deployment that provisions collectors out
    /// of band rather than through the enrollment flow. Normally null.
    /// </summary>
    public string? CollectorCredential { get; set; }

    /// <summary>Set alongside CollectorCredential when skipping enrollment.</summary>
    public string? CollectorId { get; set; }

    /// <summary>
    /// The deployment's env selector, sent in the body of every request.
    ///
    /// app.loveheartbeat.com is one hostname in front of three isolated backends and
    /// the router between them picks by matching this. Without it nothing reaches an
    /// application: every call answers "The env selector is required in the JSON
    /// payload", which reads as a broken collector and is not one.
    ///
    /// Not a credential -- the chosen backend still authenticates every request -- but
    /// it is deployment-specific, so it is configuration rather than a constant. A
    /// browser reads it from runtime-config.js; a collector has no page load to read.
    /// Leave empty when talking straight to a backend, as local development does.
    /// </summary>
    public string? EnvToken { get; set; }

    /// <summary>
    /// Renew the session this long before it expires, as a safety margin on top
    /// of the server's own renew_after hint. Covers clock skew between here and
    /// the cloud, which is the difference between renewing early and
    /// discovering the expiry by being refused mid-batch.
    /// </summary>
    public TimeSpan SessionRenewSkew { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where the credential and the spool live. Under ProgramData so the data
    /// survives the collector running as a service under a different account
    /// than the installing user.
    /// </summary>
    public string StateDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ContosoCollector");

    /// <summary>
    /// Hard ceiling on spool size. Past this the oldest segments are dropped:
    /// a collector must never fill the disk of the machine it runs on.
    /// </summary>
    public long MaxSpoolBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// Records per request. The server caps a batch at 200, so anything larger
    /// here is rejected wholesale rather than split.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string ResolveEnvToken() =>
        Environment.GetEnvironmentVariable("CONTOSO_COLLECTOR_ENV_TOKEN") is { Length: > 0 } fromEnv
            ? fromEnv
            : EnvToken ?? string.Empty;

    public string ResolveEnrollmentToken() =>
        Environment.GetEnvironmentVariable("CONTOSO_COLLECTOR_ENROLLMENT_TOKEN") is { Length: > 0 } fromEnv
            ? fromEnv
            : EnrollmentToken ?? string.Empty;

    public string EnrollUrl => $"{Endpoint.TrimEnd('/')}/api/v1/collectors/enroll";

    public string SessionUrl => $"{Endpoint.TrimEnd('/')}/api/v1/collectors/session";

    public string HeartbeatUrl => $"{Endpoint.TrimEnd('/')}/api/v1/collectors/heartbeat";

    public string TelemetryUrl => $"{Endpoint.TrimEnd('/')}/api/v1/collectors/telemetry";
}

/// <summary>Settings for the LAN-facing side: what probes and apps talk to.</summary>
public sealed class IngressOptions
{
    public const string SectionName = "Ingress";

    /// <summary>
    /// Shared secret a probe must present as X-Probe-Key. Null accepts any
    /// probe, which is fine on a closed lab network and not fine anywhere else
    /// -- so the collector logs a warning at start-up when it is unset rather
    /// than letting it pass quietly.
    ///
    /// Prefer the CONTOSO_PROBE_KEY environment variable.
    /// </summary>
    public string? ProbeKey { get; set; }

    /// <summary>
    /// Applications exporting OTLP present this as X-Probe-Key too. Same
    /// trust boundary -- everything on this hop is inside the customer's
    /// network -- so a second key would be two things to rotate for one
    /// boundary.
    /// </summary>
    public bool RequireKeyForOtlp { get; set; } = true;

    public string ResolveProbeKey() =>
        Environment.GetEnvironmentVariable("CONTOSO_PROBE_KEY") is { Length: > 0 } fromEnv
            ? fromEnv
            : ProbeKey ?? string.Empty;
}
