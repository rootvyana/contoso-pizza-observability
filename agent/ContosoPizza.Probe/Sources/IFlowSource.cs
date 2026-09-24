using ContosoPizza.Shared.Model;

namespace ContosoPizza.Probe.Sources;

/// <summary>
/// The kernel data source, behind a stable interface.
///
/// This exists because of how Datadog handles the same problem: on Windows
/// their system-probe uses a Windows Filtering Platform driver rather than
/// eBPF, but "the user-space surface is the same -- same HTTP endpoints, same
/// encoders". Everything above this interface is unaware of which
/// implementation is underneath.
///
/// Only EbpfFlowSource exists today. An ETW implementation
/// (Microsoft-Windows-Kernel-Network) is the intended second one, because it
/// needs neither the JIT-capable runtime swap nor an elevated reload after
/// every reboot.
/// </summary>
public interface IFlowSource : IDisposable
{
    /// <summary>Short identifier used in logs and on the agent's own metrics.</summary>
    string Name { get; }

    /// <summary>
    /// Attach to the source. Returns false with a human-readable reason rather
    /// than throwing, so the host can report a clean diagnostic and exit.
    /// </summary>
    bool TryConnect(out string? error);

    /// <summary>Read current state. Closed flows are consumed exactly once.</summary>
    FlowSnapshot Scrape();
}
