using System.Diagnostics;
using ContosoPizza.Probe.Interop;
using ContosoPizza.Shared.Model;
using Microsoft.Extensions.Logging;

namespace ContosoPizza.Probe.Sources;

/// <summary>
/// Reads flow_map and stats_map directly through EbpfApi.dll.
///
/// Requires elevation, exactly as bpftool does today. The eBPF program itself
/// is untouched: this reads the maps that contoso_sockops.c already maintains.
/// </summary>
public sealed class EbpfFlowSource : IFlowSource
{
    private const string FlowMapName = "flow_map";
    private const string StatsMapName = "stats_map";

    private readonly ILogger<EbpfFlowSource> _logger;
    private readonly string _installPath;
    private readonly Dictionary<uint, string?> _processNameCache = new();

    private int _flowMapFd = -1;
    private int _statsMapFd = -1;
    private uint _flowMapCapacity;
    private bool _disposed;

    public EbpfFlowSource(ILogger<EbpfFlowSource> logger, string installPath)
    {
        _logger = logger;
        _installPath = installPath;
    }

    public string Name => "ebpf";

    public bool TryConnect(out string? error)
    {
        EbpfApi.RegisterResolver(_installPath);

        string dll = Path.Combine(_installPath, "EbpfApi.dll");
        if (!File.Exists(dll))
        {
            error = $"EbpfApi.dll not found at {dll}. Is eBPF for Windows installed?";
            return false;
        }

        List<(uint Id, string Name, uint MaxEntries)> maps;
        try
        {
            maps = EnumerateMaps().ToList();
        }
        catch (DllNotFoundException ex)
        {
            error = $"Could not load {dll}: {ex.Message}";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"{dll} is missing an expected libbpf export: {ex.Message}";
            return false;
        }

        if (maps.Count == 0)
        {
            error = "No eBPF maps are loaded. Run ebpf\\load.ps1 from an elevated prompt, " +
                    "and make sure this agent is also elevated.";
            return false;
        }

        _logger.LogDebug("Found {Count} loaded map(s): {Names}",
            maps.Count, string.Join(", ", maps.Select(m => $"{m.Name}#{m.Id}")));

        var flow = maps.FirstOrDefault(m => m.Name == FlowMapName);
        var stats = maps.FirstOrDefault(m => m.Name == StatsMapName);

        if (flow.Name is null || stats.Name is null)
        {
            error = $"Expected maps '{FlowMapName}' and '{StatsMapName}' were not found. " +
                    $"Loaded maps: {string.Join(", ", maps.Select(m => m.Name))}. " +
                    "Is contoso_sockops.o loaded?";
            return false;
        }

        _flowMapFd = EbpfApi.MapGetFdById(flow.Id);
        _statsMapFd = EbpfApi.MapGetFdById(stats.Id);
        _flowMapCapacity = flow.MaxEntries;

        if (_flowMapFd < 0 || _statsMapFd < 0)
        {
            error = $"Could not open map descriptors (flow={_flowMapFd}, stats={_statsMapFd}). " +
                    "This usually means the agent is not running elevated.";
            return false;
        }

        _logger.LogInformation(
            "Attached to {FlowMap} (id {FlowId}, capacity {Capacity}) and {StatsMap} (id {StatsId})",
            FlowMapName, flow.Id, _flowMapCapacity, StatsMapName, stats.Id);

        error = null;
        return true;
    }

    /// <summary>
    /// Walk every live map id and read its info. This is what bpftool does to
    /// resolve "map dump name flow_map"; the maps are not pinned at a path we
    /// could look up directly.
    /// </summary>
    private static IEnumerable<(uint Id, string Name, uint MaxEntries)> EnumerateMaps()
    {
        uint id = 0;

        while (EbpfApi.MapGetNextId(id, out uint next) == 0)
        {
            id = next;

            int fd = EbpfApi.MapGetFdById(id);
            if (fd < 0)
            {
                continue;
            }

            try
            {
                var info = default(BpfMapInfo);
                uint length;
                unsafe { length = (uint)sizeof(BpfMapInfo); }

                if (EbpfApi.ObjGetInfoByFd(fd, ref info, ref length) == 0)
                {
                    yield return (info.Id, info.Name, info.MaxEntries);
                }
            }
            finally
            {
                EbpfApi.CloseFd(fd);
            }
        }
    }

    public FlowSnapshot Scrape()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Collect keys first, then look up and mutate. Deleting entries while
        // walking with bpf_map_get_next_key would invalidate the iteration.
        var keys = ReadAllKeys();

        var closed = new List<FlowRecord>();
        var active = new List<FlowRecord>();

        foreach (var key in keys)
        {
            if (EbpfApi.MapLookupFlow(_flowMapFd, in key, out FlowStats stats) != 0)
            {
                // Raced with the kernel deleting it; nothing to report.
                continue;
            }

            var record = ToRecord(key, stats);

            if (stats.IsOpen)
            {
                active.Add(record);
                continue;
            }

            closed.Add(record);

            // Evict after export. contoso_sockops.c never calls
            // bpf_map_delete_elem, so without this flow_map fills to MAX_FLOWS
            // and silently stops recording new flows. Doing it here fixes the
            // leak without modifying the eBPF program, and guarantees each
            // closed flow is reported exactly once.
            int rc = EbpfApi.MapDeleteFlow(_flowMapFd, in key);
            if (rc != 0)
            {
                _logger.LogWarning("Failed to evict closed flow {Flow} (rc {Rc})", key, rc);
            }
        }

        return new FlowSnapshot
        {
            ClosedFlows = closed,
            ActiveFlows = active,
            Counters = ReadCounters(),
            MapEntries = keys.Count,
            MapCapacity = _flowMapCapacity,
        };
    }

    private List<FlowKey> ReadAllKeys()
    {
        var keys = new List<FlowKey>();

        if (EbpfApi.MapGetFirstFlowKey(_flowMapFd, IntPtr.Zero, out FlowKey key) != 0)
        {
            return keys; // empty map
        }

        keys.Add(key);

        // Bounded by capacity so a malformed map cannot spin forever.
        while (keys.Count <= _flowMapCapacity &&
               EbpfApi.MapGetNextFlowKey(_flowMapFd, in key, out FlowKey next) == 0)
        {
            keys.Add(next);
            key = next;
        }

        return keys;
    }

    private KernelCounters ReadCounters()
    {
        ulong Read(uint slot) =>
            EbpfApi.MapLookupStat(_statsMapFd, in slot, out ulong value) == 0 ? value : 0UL;

        return new KernelCounters(
            Established: Read(StatSlot.Established),
            Deleted: Read(StatSlot.Deleted),
            TotalDurationNs: Read(StatSlot.DurationNs),
            CurrentOpen: Read(StatSlot.CurrentOpen));
    }

    private FlowRecord ToRecord(FlowKey key, FlowStats stats) => new()
    {
        LocalAddress = key.LocalAddress,
        LocalPort = key.LocalPort,
        RemoteAddress = key.RemoteAddress,
        RemotePort = key.RemotePort,
        ProcessId = stats.ProcessId,
        IsOutbound = stats.IsOutbound,
        IsOpen = stats.IsOpen,
        Duration = stats.Duration,
        ProcessName = ResolveProcessName(stats.ProcessId),
    };

    /// <summary>
    /// PID to image name. Cached because a closed flow's process has usually
    /// exited by the time we see it, and the lookup then throws every scrape.
    /// </summary>
    private string? ResolveProcessName(uint pid)
    {
        if (_processNameCache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process has exited.
        }
        catch (InvalidOperationException)
        {
            // Process exited between lookup and read.
        }

        _processNameCache[pid] = name;
        return name;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_flowMapFd >= 0)
        {
            EbpfApi.CloseFd(_flowMapFd);
            _flowMapFd = -1;
        }

        if (_statsMapFd >= 0)
        {
            EbpfApi.CloseFd(_statsMapFd);
            _statsMapFd = -1;
        }
    }
}
