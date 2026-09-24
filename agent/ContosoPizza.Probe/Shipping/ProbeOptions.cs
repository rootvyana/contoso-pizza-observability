namespace ContosoPizza.Probe.Shipping;

/// <summary>
/// Everything the probe needs to know, which is deliberately almost nothing:
/// where the kernel runtime is, where the collector is, and how to prove to the
/// collector that it is allowed to talk to it.
///
/// There is no cloud endpoint here and no cloud credential. A probe that knew
/// how to reach loveheartbeat.com would be a second collector, and fifty of
/// them would be fifty credentials to rotate.
/// </summary>
public sealed class ProbeOptions
{
    public const string SectionName = "Probe";

    /// <summary>Where the eBPF for Windows runtime is installed.</summary>
    public string EbpfInstallPath { get; set; } = Interop.EbpfApi.DefaultInstallPath;

    /// <summary>
    /// The collector's base URL on the LAN, e.g. http://collector.corp:5200.
    /// Plain HTTP is the default because this hop does not leave the customer's
    /// network; set an https:// URL and it is used as given.
    /// </summary>
    public string CollectorEndpoint { get; set; } = "http://localhost:5200";

    /// <summary>
    /// Shared secret presented as X-Probe-Key, so a stray host on the same LAN
    /// cannot inject flows into someone's fleet view. One value for the whole
    /// site: this authenticates "a probe we installed", not each machine, and a
    /// per-probe secret would recreate the key-distribution problem that moving
    /// the credential into the collector was meant to remove.
    ///
    /// Prefer the CONTOSO_PROBE_KEY environment variable over appsettings.json.
    /// </summary>
    public string? ProbeKey { get; set; }

    /// <summary>
    /// The name this server reports as. Defaults to the machine name, which is
    /// right almost always and wrong on a machine that has been renamed or
    /// cloned from an image.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Starting scrape interval. The collector overrides it in every ack, so
    /// this only governs the interval before the first successful POST.
    ///
    /// Polling means a connection that opens and closes entirely between two
    /// scrapes is never seen; a ring buffer in the eBPF program would remove
    /// that blind spot, at the cost of editing and reloading contoso_sockops.c.
    /// </summary>
    public TimeSpan ScrapeInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Flows held in memory while the collector is unreachable. Past this the
    /// oldest are dropped.
    ///
    /// The probe does not spool to disk, and that is the deliberate asymmetry in
    /// this design: durability is the collector's job, in one place, with one
    /// disk quota to reason about. A LAN hop to a machine in the same building
    /// is not the failure mode that at-least-once delivery exists to survive --
    /// the internet is, and that hop is behind the collector's spool.
    /// </summary>
    public int MaxBufferedFlows { get; set; } = 10_000;

    public string ResolveProbeKey() =>
        Environment.GetEnvironmentVariable("CONTOSO_PROBE_KEY") is { Length: > 0 } fromEnv
            ? fromEnv
            : ProbeKey ?? string.Empty;

    public string ResolveServerName() =>
        string.IsNullOrWhiteSpace(ServerName) ? Environment.MachineName : ServerName.Trim();
}
