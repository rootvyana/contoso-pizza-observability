using System.Net;

namespace ContosoPizza.Shared.Model;

/// <summary>
/// One TCP flow as seen by the kernel, normalised away from the raw map layout
/// so that nothing downstream depends on which IFlowSource produced it.
/// </summary>
public sealed record FlowRecord
{
    public required IPAddress LocalAddress { get; init; }
    public required ushort LocalPort { get; init; }
    public required IPAddress RemoteAddress { get; init; }
    public required ushort RemotePort { get; init; }
    public required uint ProcessId { get; init; }
    public required bool IsOutbound { get; init; }
    public required bool IsOpen { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// The client's ephemeral port, which is the connection-level join key back
    /// into the OpenTelemetry layer (Program.cs tags every span with
    /// client.port). Only meaningful on an inbound flow: on an outbound row
    /// RemotePort is the server port, not the client's.
    /// </summary>
    public ushort? ClientPort => IsOutbound ? null : RemotePort;

    /// <summary>Resolved lazily by the collector; null if the process has exited.</summary>
    public string? ProcessName { get; init; }
}

/// <summary>Aggregate counters read from stats_map.</summary>
public readonly record struct KernelCounters(
    ulong Established,
    ulong Deleted,
    ulong TotalDurationNs,
    ulong CurrentOpen);

/// <summary>The result of one scrape of the kernel maps.</summary>
public sealed record FlowSnapshot
{
    /// <summary>
    /// Flows that closed since the last scrape. These have been removed from
    /// flow_map by the collector, so each one is reported exactly once.
    /// </summary>
    public required IReadOnlyList<FlowRecord> ClosedFlows { get; init; }

    /// <summary>Flows still established at the moment of the scrape.</summary>
    public required IReadOnlyList<FlowRecord> ActiveFlows { get; init; }

    public required KernelCounters Counters { get; init; }

    /// <summary>Entries present in flow_map, for occupancy monitoring.</summary>
    public required int MapEntries { get; init; }

    /// <summary>max_entries from bpf_map_info -- MAX_FLOWS, currently 1024.</summary>
    public required uint MapCapacity { get; init; }
}
