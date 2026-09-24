using System.Runtime.InteropServices;

namespace ContosoPizza.Probe.Interop;

/// <summary>
/// P/Invoke surface for the libbpf C API exported by EbpfApi.dll.
///
/// Every entry point below was verified against the eBPF for Windows headers in
/// ebpf/ebpf-for-windows/include/bpf/{bpf,libbpf}.h and confirmed present in
/// ebpfapi/Source.def. This replaces shelling out to bpftool.exe and parsing its
/// JSON, which is what watch.ps1 and collect-logs.ps1 do today.
///
/// libbpf convention: 0 on success, negative on failure with errno set.
/// bpf_map_get_fd_by_id is the exception -- it returns the fd, or negative.
/// </summary>
internal static partial class EbpfApi
{
    private const string Lib = "EbpfApi";

    /// <summary>Default install location of the eBPF for Windows runtime.</summary>
    public const string DefaultInstallPath = @"C:\Program Files\ebpf-for-windows";

    private static int _resolverRegistered;

    /// <summary>
    /// EbpfApi.dll lives in the install directory, which is not on the default
    /// probing path, so the first P/Invoke would fail to load it. Register a
    /// resolver that points at the real file before any other call.
    /// </summary>
    public static void RegisterResolver(string installPath)
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 1)
        {
            return;
        }

        string dll = Path.Combine(installPath, "EbpfApi.dll");

        NativeLibrary.SetDllImportResolver(typeof(EbpfApi).Assembly, (name, assembly, searchPath) =>
            name == Lib && File.Exists(dll)
                ? NativeLibrary.Load(dll)
                : IntPtr.Zero);
    }

    // ---- map enumeration -------------------------------------------------
    // The maps are not pinned at a known path, so they are located by walking
    // every live map id and matching on the name in bpf_map_info. This is the
    // same thing "bpftool map dump name flow_map" does internally.

    [LibraryImport(Lib, EntryPoint = "bpf_map_get_next_id")]
    internal static partial int MapGetNextId(uint startId, out uint nextId);

    [LibraryImport(Lib, EntryPoint = "bpf_map_get_fd_by_id")]
    internal static partial int MapGetFdById(uint id);

    [LibraryImport(Lib, EntryPoint = "bpf_obj_get_info_by_fd")]
    internal static partial int ObjGetInfoByFd(int fd, ref BpfMapInfo info, ref uint infoLen);

    // ---- flow_map: key = FlowKey (12 bytes), value = FlowStats (40 bytes) --

    /// <summary>First key in the map. Pass IntPtr.Zero as the key.</summary>
    [LibraryImport(Lib, EntryPoint = "bpf_map_get_next_key")]
    internal static partial int MapGetFirstFlowKey(int fd, IntPtr nullKey, out FlowKey nextKey);

    [LibraryImport(Lib, EntryPoint = "bpf_map_get_next_key")]
    internal static partial int MapGetNextFlowKey(int fd, in FlowKey key, out FlowKey nextKey);

    [LibraryImport(Lib, EntryPoint = "bpf_map_lookup_elem")]
    internal static partial int MapLookupFlow(int fd, in FlowKey key, out FlowStats value);

    [LibraryImport(Lib, EntryPoint = "bpf_map_delete_elem")]
    internal static partial int MapDeleteFlow(int fd, in FlowKey key);

    // ---- stats_map: key = uint32 slot, value = uint64 counter -------------

    [LibraryImport(Lib, EntryPoint = "bpf_map_lookup_elem")]
    internal static partial int MapLookupStat(int fd, in uint key, out ulong value);

    // ---- lifetime --------------------------------------------------------
    // Windows has no close(2) for these; the runtime exports its own. Returns
    // ebpf_result_t (0 == EBPF_SUCCESS), not an errno.

    [LibraryImport(Lib, EntryPoint = "ebpf_close_fd")]
    internal static partial int CloseFd(int fd);
}
