using System.Runtime.InteropServices;
using System.Text;

namespace ContosoPizza.Probe.Interop;

/// <summary>
/// Mirrors "struct bpf_map_info" at ebpf-for-windows/include/ebpf_structs.h:449.
///
///     ebpf_id_t id;                 // uint32
///     ebpf_map_type_t type;         // enum, uint32
///     uint32_t key_size;
///     uint32_t value_size;
///     uint32_t max_entries;
///     char name[BPF_OBJ_NAME_LEN];  // BPF_OBJ_NAME_LEN == 64
///     uint32_t map_flags;
///     // Windows-specific tail:
///     ebpf_id_t inner_map_id;       // uint32
///     uint32_t pinned_path_count;
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BpfMapInfo
{
    public const int NameLength = 64;

    public uint Id;
    public uint Type;
    public uint KeySize;
    public uint ValueSize;
    public uint MaxEntries;
    public fixed byte NameBytes[NameLength];
    public uint MapFlags;
    public uint InnerMapId;
    public uint PinnedPathCount;

    /// <summary>The null-terminated map name, e.g. "flow_map".</summary>
    public string Name
    {
        get
        {
            fixed (byte* p = NameBytes)
            {
                int length = 0;
                while (length < NameLength && p[length] != 0)
                {
                    length++;
                }

                return Encoding.ASCII.GetString(p, length);
            }
        }
    }
}
