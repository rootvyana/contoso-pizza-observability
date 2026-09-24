// Connection-level instrumentation for the ContosoPizza web API.
//
// eBPF for Windows has no uprobe/kprobe support, so this cannot see HTTP.
// It records the TCP connection lifecycle for the app's listening port:
// when a flow is established, when it is torn down, and how long it lived.
// The OpenTelemetry side of the app tags each span with client.port, which
// is the join key back into flow_map below.
//
// Written against include/ebpf_nethooks.h and modelled on tests/sample/sockops.c.

#include "bpf_helpers.h"
#include "bpf_endian.h"
#include "net/ip.h"

// Must match applicationUrl in Properties/launchSettings.json.
#define CONTOSO_PORT 5176

#define MAX_FLOWS 1024

// Slots in stats_map.
#define STAT_ESTABLISHED  0
#define STAT_DELETED      1
#define STAT_DURATION_NS  2
#define STAT_CURRENT_OPEN 3
#define STAT_COUNT        4

typedef struct _flow_key
{
    uint32_t local_ip4;   // offset 0
    uint32_t remote_ip4;  // offset 4
    uint16_t local_port;  // offset 8   (host order)
    uint16_t remote_port; // offset 10  (host order)
} flow_key_t;             // 12 bytes

typedef struct _flow_stats
{
    uint64_t established_ns; // offset 0
    uint64_t deleted_ns;     // offset 8
    uint64_t duration_ns;    // offset 16
    uint32_t process_id;     // offset 24
    uint32_t family;         // offset 28
    uint32_t open;           // offset 32  1 = live, 0 = closed
    uint32_t outbound;       // offset 36  1 = we connected out, 0 = inbound accept
} flow_stats_t;              // 40 bytes

struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, MAX_FLOWS);
    __type(key, flow_key_t);
    __type(value, flow_stats_t);
} flow_map SEC(".maps");

struct
{
    __uint(type, BPF_MAP_TYPE_ARRAY);
    __uint(max_entries, STAT_COUNT);
    __type(key, uint32_t);
    __type(value, uint64_t);
} stats_map SEC(".maps");

static inline void
bump(uint32_t slot, uint64_t delta)
{
    uint64_t* counter = bpf_map_lookup_elem(&stats_map, &slot);
    if (counter != NULL) {
        *counter += delta;
    }
}

SEC("sockops")
int
contoso_connection_monitor(bpf_sock_ops_t* ctx)
{
    // IPv4 only: flow_key_t has no room for v6 addresses, so v6 flows would
    // collide on a zeroed key. See README.
    if (ctx->family != AF_INET) {
        return 0;
    }

    // Ports arrive in network byte order (see ebpf_nethooks.h).
    uint16_t local_port = bpf_ntohs((uint16_t)ctx->local_port);
    uint16_t remote_port = bpf_ntohs((uint16_t)ctx->remote_port);

    // Keep only traffic belonging to the API listener.
    if (local_port != CONTOSO_PORT && remote_port != CONTOSO_PORT) {
        return 0;
    }

    // On loopback BOTH ends are local, so the client side (ACTIVE) and the
    // server side (PASSIVE) each fire for one TCP connection. Aggregate
    // counters therefore only count the server side, or they double on 127.0.0.1.
    bool server_side = (local_port == CONTOSO_PORT);

    flow_key_t key = {0};
    key.local_ip4 = ctx->local_ip4;
    key.remote_ip4 = ctx->remote_ip4;
    key.local_port = local_port;
    key.remote_port = remote_port;

    uint64_t now = bpf_ktime_get_boot_ns();

    switch (ctx->op) {
    case BPF_SOCK_OPS_ACTIVE_ESTABLISHED_CB:
    case BPF_SOCK_OPS_PASSIVE_ESTABLISHED_CB: {
        flow_stats_t stats = {0};
        stats.established_ns = now;
        stats.family = ctx->family;
        stats.open = 1;
        stats.outbound = (ctx->op == BPF_SOCK_OPS_ACTIVE_ESTABLISHED_CB) ? 1 : 0;
        // High 32 bits are the process id; low 32 are the thread id.
        stats.process_id = (uint32_t)(bpf_get_current_pid_tgid() >> 32);
        bpf_map_update_elem(&flow_map, &key, &stats, BPF_ANY);
        if (server_side) {
            bump(STAT_ESTABLISHED, 1);
            bump(STAT_CURRENT_OPEN, 1);
        }
        break;
    }
    case BPF_SOCK_OPS_CONNECTION_DELETED_CB: {
        flow_stats_t* stats = bpf_map_lookup_elem(&flow_map, &key);
        if (stats != NULL) {
            stats->deleted_ns = now;
            stats->open = 0;
            if (now > stats->established_ns) {
                stats->duration_ns = now - stats->established_ns;
                if (server_side) {
                    bump(STAT_DURATION_NS, stats->duration_ns);
                }
            }
            if (server_side) {
                bump(STAT_CURRENT_OPEN, (uint64_t)-1);
            }
        }
        if (server_side) {
            bump(STAT_DELETED, 1);
        }
        break;
    }
    default:
        break;
    }

    return 0;
}
