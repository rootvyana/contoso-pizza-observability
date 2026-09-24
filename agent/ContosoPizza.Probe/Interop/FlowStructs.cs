using System.Net;
using System.Runtime.InteropServices;

namespace ContosoPizza.Probe.Interop;

/// <summary>
/// Mirrors flow_key_t in ebpf/contoso_sockops.c -- 12 bytes.
///
/// THIS IS THE ONLY MANAGED COPY OF THE LAYOUT. It replaces the three
/// hand-written byte-offset decoders that previously had to be edited in
/// lockstep (contoso_sockops.c, watch.ps1, collect-logs.ps1). P/Invoke
/// enforces the match instead of a human doing it.
///
/// Ports are stored in HOST order: the eBPF program already ran them through
/// bpf_ntohs before writing the key.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct FlowKey : IEquatable<FlowKey>
{
    public readonly uint LocalIp4;
    public readonly uint RemoteIp4;
    public readonly ushort LocalPort;
    public readonly ushort RemotePort;

    public IPAddress LocalAddress => new(BitConverter.GetBytes(LocalIp4));

    public IPAddress RemoteAddress => new(BitConverter.GetBytes(RemoteIp4));

    public bool Equals(FlowKey other) =>
        LocalIp4 == other.LocalIp4 &&
        RemoteIp4 == other.RemoteIp4 &&
        LocalPort == other.LocalPort &&
        RemotePort == other.RemotePort;

    public override bool Equals(object? obj) => obj is FlowKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(LocalIp4, RemoteIp4, LocalPort, RemotePort);

    public override string ToString() => $"{LocalAddress}:{LocalPort} <-> {RemoteAddress}:{RemotePort}";
}

/// <summary>
/// Mirrors flow_stats_t in ebpf/contoso_sockops.c -- 40 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct FlowStats
{
    public readonly ulong EstablishedNs;
    public readonly ulong DeletedNs;
    public readonly ulong DurationNs;
    public readonly uint ProcessId;
    public readonly uint Family;
    public readonly uint Open;
    public readonly uint Outbound;

    /// <summary>True while the TCP connection is still established.</summary>
    public bool IsOpen => Open == 1;

    /// <summary>
    /// True when this end initiated the connection (ACTIVE_ESTABLISHED).
    /// On loopback both ends are local, so each TCP connection produces one
    /// outbound row and one inbound row -- see contoso_sockops.c.
    /// </summary>
    public bool IsOutbound => Outbound == 1;

    public TimeSpan Duration => TimeSpan.FromTicks((long)(DurationNs / 100));
}

/// <summary>
/// Slot indices in stats_map. Must match the STAT_* defines in contoso_sockops.c.
/// </summary>
internal static class StatSlot
{
    public const uint Established = 0;
    public const uint Deleted = 1;
    public const uint DurationNs = 2;
    public const uint CurrentOpen = 3;
    public const uint Count = 4;
}
