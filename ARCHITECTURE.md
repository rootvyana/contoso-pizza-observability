# Architecture — as built

What was actually built, why it differs from [the original proposal](observability-architecture.txt),
and where the seams are.

The proposal was written on 17 September 2026, before any of this existed. It surveyed
Datadog, Dynatrace and the OpenTelemetry Collector and proposed a shape. Most of it
survived contact; some of it did not, and the parts that did not are the interesting
ones. [Against the proposal](#against-the-proposal) reconciles the two line by line.

---

## Contents

1. [The three processes](#1-the-three-processes)
2. [The data path](#2-the-data-path)
3. [Why the collector is per-site, not per-host](#3-why-the-collector-is-per-site-not-per-host)
4. [Authentication](#4-authentication)
5. [Durability](#5-durability)
6. [The joins](#6-the-joins)
7. [Reading the kernel](#7-reading-the-kernel)
8. [Receiving OpenTelemetry](#8-receiving-opentelemetry)
9. [The cloud contract](#9-the-cloud-contract)
10. [Self-observability](#10-self-observability)
11. [Against the proposal](#against-the-proposal)
12. [What is not built](#what-is-not-built)

---

## 1. The three processes

```
  MONITORED SERVER (one of many)                    COLLECTOR HOST (one per site)
 ┌────────────────────────────────┐                ┌──────────────────────────────┐
 │  your app                      │                │  ContosoPizza.Collector      │
 │    OTel SDK ─── OTLP/HTTP ─────┼───────────────▶│    :5200                     │
 │                                │   LAN, plain   │                              │
 │  contoso_sockops.o  (kernel)   │   HTTP + key   │    receivers                 │
 │    flow_map / stats_map        │                │      /v1/flows   (probes)    │
 │         │                      │                │      /v1/traces  (OTLP)      │
 │         ▼ P/Invoke             │                │      /v1/logs    (OTLP)      │
 │  ContosoPizza.Probe ───────────┼───────────────▶│      /v1/metrics (counted)   │
 │    elevated, no credential     │                │                              │
 └────────────────────────────────┘                │    EventSpool (disk)         │
                                                   │    CloudSession (auth)       │
                                                   │    SpoolPump ────────────────┼──▶ HTTPS 443
                                                   │    HeartbeatService ─────────┼──▶ outbound only
                                                   └──────────────────────────────┘
```

| | Instances | Elevation | Cloud credential | Disk state |
|---|---|---|---|---|
| **App** | many | no | no | no |
| **Probe** | one per server | **yes** | no | probe id only |
| **Collector** | **one per site** | no | **yes, the only one** | credential + spool |

The app is unmodified apart from three environment variables. The probe is the smallest
thing that can read a kernel map. Everything that is hard — credentials, retries,
durability, batching — happens once, in the collector.

---

## 2. The data path

```
  produce           receive            queue              deliver
  ─────────────────────────────────────────────────────────────────────
  eBPF map    ─┐
               ├─▶  SpoolWriter  ─▶  EventSpool  ─▶  SpoolPump  ─▶  cloud
  OTLP export ─┘                     (disk)          (batched)
```

One queue, not three. Flows, spans and logs are different shapes but they share a
segment file, because splitting them would mean three orderings that drain unevenly
during an outage and three chances to lose one of them.

`SpooledRecord` carries a `kind` with each line; `SpoolPump` regroups them into the
three arrays the cloud expects at the last possible moment.

**Append-only, segment-based.** Records go to `current.jsonl`; the pump rolls that to
`<ticks>.seg`, sends it, and deletes it **only on 2xx**. A crash mid-send costs a
duplicate, never a loss.

---

## 3. Why the collector is per-site, not per-host

This is the largest deviation from the proposal, which had a privileged probe and an
unprivileged agent on *the same machine*, talking over a named pipe — Datadog's
system-probe / core-agent split.

That split solves least privilege. It does not solve the two problems that actually
appear at ten machines:

**A credential per server.** Fifty machines is fifty secrets to issue, rotate and
revoke, and fifty things to get wrong.

**No fleet view.** An agent can only report on itself. The server that stopped
reporting yesterday is invisible *precisely because* it stopped. Something has to know
the fleet's membership independently of whether each member is currently alive — and
that something cannot be the members.

So the unprivileged half moved off the host and became a site-wide tier. This is
Dynatrace's ActiveGate, and the OpenTelemetry Collector's gateway pattern. The named
pipe became HTTP over the LAN because the two halves are no longer on one machine.

Least privilege is preserved: the probe still holds nothing worth stealing.

---

## 4. Authentication

Three tiers, each with a different lifetime, because they answer different questions.

| | Lifetime | Where it lives | Crosses the wire |
|---|---|---|---|
| Enrollment token | 24h, single use | pasted in once | once |
| **Collector credential** | **never expires** | DPAPI-encrypted on disk | on renewal only |
| Session | 12h, sliding | memory | every request |

**The credential has no clock on it, deliberately.** A collector is installed once,
inside a customer's network, often by somebody who will not log in again for a year. An
expiring credential is a silent telemetry outage on a random Tuesday. Revocation is
explicit, and takes effect on the collector's *next request* — the server re-reads the
collector's status on every call rather than trusting the session.

**Sessions slide.** Every authenticated request pushes the expiry forward, so a
collector that is sending never reaches it. One that is refused anyway mints a new
session from the credential and retries the same batch. There is no state the collector
can reach where a human has to intervene — except revocation, which is a human's
decision by definition.

Neither secret is stored server-side in a replayable form: the lookup key is a SHA-256
of the token.

### At rest

The credential is DPAPI-encrypted under the **local machine** account, with an entropy
string tying it to this purpose. Machine scope and not user scope because the collector
runs as a service under an account nobody logs in as; a user-scoped blob written by the
installing administrator would be undecryptable by the service that has to read it.

A state directory copied to another machine therefore decrypts to nothing — and says
so, rather than silently re-enrolling and appearing as a duplicate.

---

## 5. Durability

| Condition | Behaviour |
|---|---|
| Cloud unreachable | spool, exponential backoff, retry forever |
| 5xx, 408, 429 | retry |
| **401** | renew the session, retry once, then treat as retryable |
| Other 4xx | log loudly and **discard** |
| Spool over `MaxSpoolBytes` (256 MB) | drop oldest segments, report the loss |
| Crash mid-send | segment survives; duplicate possible, loss is not |

Discarding on a permanent 4xx is deliberate: otherwise one malformed batch blocks every
batch behind it, forever. The loss is loud, and the alternative is a queue that never
drains again.

The spool has a hard ceiling because an agent must never fill the disk of the machine
it is monitoring. Past it the *oldest* segments go — during an outage, what is
happening now matters more than what happened an hour ago — and the count surfaces as
`segments_dropped` rather than being hidden.

Delivery is **at-least-once**. Every flow carries a stable id derived from its
four-tuple and end time; spans carry `span_id`. A redelivery overwrites its own row.

**The probe deliberately does not spool.** It buffers in memory and then drops. A LAN
hop to a machine in the same building is not the failure mode at-least-once delivery
exists to survive — the internet is, and that hop is behind the collector's spool.
Durability in one place, with one quota to reason about.

---

## 6. The joins

Three signals. Any one alone is a pile of data; the joins are the product.

```
                    trace_id
        span  ◀───────────────────▶  log
          │
     client.port
          │  (+ host)
          ▼
        flow
```

### Entity join — always works

`service.name`, `service.instance.id` (`MACHINE:PID`) and `host.name` travel as OTel
Resource attributes on every span and log. This survives port reuse, process restarts
and clock skew. It is what Datadog and Dynatrace actually rely on, and it is the
foundation.

### Connection join — precise, and the layer on top

The app tags every span with `client.port`; the eBPF probe reports the same number as
the flow's peer port. Nothing else can tie a request to a socket: a pid is per-process,
and a timestamp is not precise enough on a busy listener.

**Qualified by host**, because ports are per-machine and get reused. Matching on the
number alone would present a different machine's connection as this request's — a
fabricated correlation, which is worse than no correlation.

`client.port` is recorded on **inbound flows only**. On an outbound row the remote port
is the server's and joins nothing.

### Trace join — span to its own log lines

The logging provider stamps the active trace and span id on every record. The route
drill-down uses it to show the lines a route's own requests wrote, and the trace view
uses the span id to show them against the span that wrote them.

This is what turns *"this span failed"* into *"this span failed because the inventory
service did not answer"*.

---

## 7. Reading the kernel

`EbpfApi.dll` by direct P/Invoke — no `bpftool`, no JSON, no parsing. Seven exports:

```
bpf_map_get_next_id   bpf_map_get_fd_by_id   bpf_obj_get_info_by_fd
bpf_map_get_next_key  bpf_map_lookup_elem    bpf_map_delete_elem
ebpf_close_fd
```

Maps are found by walking ids and matching on name, so nothing depends on a pin path or
a load order.

### One struct definition

`FlowKey` (12 bytes) and `FlowStats` (40 bytes) are declared once, as
`[StructLayout(LayoutKind.Sequential)]`. They replaced three hand-written byte-offset
decoders — in `watch.ps1`, `collect-logs.ps1` and the C source — that had to be edited
in lockstep. P/Invoke enforces the match now instead of a human doing it.

### `IFlowSource`

The kernel source sits behind an interface, which is Datadog's Windows lesson taken
directly: on Windows their system-probe uses a WFP driver rather than eBPF, but *"the
user-space surface is the same — same HTTP endpoints, same encoders."*

Only `EbpfFlowSource` exists. An ETW implementation is the intended second one, and it
matters more here than it would elsewhere: loading unsigned eBPF bytecode on Windows
required swapping the shipped `NativeOnly` runtime for a JIT-capable build, which
lowers the machine's security posture. ETW needs none of that and survives a reboot.

### The leak, fixed without touching the C

`contoso_sockops.c` never calls `bpf_map_delete_elem`. Closed flows accumulate until
`MAX_FLOWS` (1024), after which `bpf_map_update_elem` fails, its return value is
ignored, and the program **silently stops recording**.

The probe collects keys first, then deletes each closed flow after exporting it. That
fixes the leak without modifying the eBPF program and guarantees each flow is reported
exactly once. Map occupancy is reported on every scrape and warned above 90%.

---

## 8. Receiving OpenTelemetry

The collector serves the standard OTLP/HTTP paths, so an SDK exporter configured with
its base URL finds them with no other change.

| | |
|---|---|
| `POST /v1/traces` | decoded to spans |
| `POST /v1/logs` | decoded to log records |
| `POST /v1/metrics` | **counted, not stored** |

Both encodings are accepted, decided by `Content-Type`: **protobuf** (every SDK's
default) and **JSON**.

The protobuf decoder is hand-written — a 150-line wire reader plus the field numbers
for the three OTLP messages. There is no published .NET package for the OTLP protos,
and pulling in `Grpc.Tools` to run `protoc` at build time would add a native toolchain
dependency to a project whose reason to exist is being small enough to drop onto a
customer's server. Field numbers are part of the wire contract and do not change
between releases; unknown fields are skipped by wire type, so a newer OTLP release
cannot break it.

The JSON decoder handles proto3's canonical mapping, where **int64 is encoded as a
string** — `"startTimeUnixNano": "1700000000000000000"`. A decoder expecting numbers
reads zero for every timestamp and files every span at the epoch.

### Metrics

Accepted, counted, and dropped, because the cloud intake takes logs, spans and flows.
Returning an error would make a correctly configured exporter retry forever; returning
success silently would lose them unremarked. The count surfaces as
`otlp_metrics_dropped` on every heartbeat.

---

## 9. The cloud contract

Four POST endpoints, all outbound over 443:

```
/api/v1/collectors/enroll       no auth        once per collector
/api/v1/collectors/session      credential     on demand
/api/v1/collectors/heartbeat    session        every 30s
/api/v1/collectors/telemetry    session        every 5s when busy
```

Full request and response shapes, and a working receiver in about fifty lines of
Python, are in [agent/BACKEND-PROTOCOL.txt](agent/BACKEND-PROTOCOL.txt).

### Why heartbeat polling rather than a WebSocket

An earlier version held a persistent outbound WebSocket for the control channel. It was
removed. The backend runs FastAPI under Mangum on Lambda, where the process exists only
for the duration of a request and **cannot hold a socket open at all**.

Polling on an interval *the server chooses* carries the same two things — health up,
configuration down — over the transport that deployment actually has. It also keeps the
session permanently fresh as a side effect, since every heartbeat is an authenticated
request.

Configuration pushed down this way reaches each probe in the ack to its next POST, so
one change in the dashboard retunes a whole fleet without anybody logging into a server.

### The env selector

One hostname can front several isolated backends with a router choosing between them by
an opaque token. Without it every call is refused with *"The env selector is required in
the JSON payload"*, which reads exactly like a broken collector and is not one.

`EnvSelectorHandler` is a `DelegatingHandler` that adds it to every outgoing body. A
handler rather than a field on each request record, because `env` is not part of the
API's contract — it belongs to the edge in front of it, so it is added at the edge of
this process, once, where a route added later cannot forget it.

---

## 10. Self-observability

Every heartbeat carries:

```
uptime_seconds          records_delivered       spool_segments_pending
cloud_authenticated     delivery_failures       spool_bytes
flows_received          otlp_metrics_dropped    segments_dropped
spans_received          last_successful_delivery
logs_received           last_delivery_error
```

Plus the list of servers currently reporting through this collector — which is the only
way anything can tell "the fleet is quiet" from "the fleet is gone".

The eBPF leak in section 7 is the argument for all of it: a map that silently stops
recording is invisible without something counting.

Locally, `GET /status` answers the same question for somebody standing at the machine
with no dashboard access, and `--diagnose` answers "can this machine reach the cloud
and will it be let in" without starting the collector or touching the spool.

---

## Against the proposal

### The six changes that were said to carry the weight

| | Proposed | Built |
|---|---|---|
| 1 | `IFlowSource` abstraction | **yes** — only the eBPF implementation exists |
| 2 | Push, not poll (ring buffer) | **no** — still polls. See below |
| 3 | Two-level join | **yes** — entity foundation, connection on top |
| 4 | OTel semantic conventions | **yes** — `network.peer.*`, `network.local.*`, durations |
| 5 | One struct definition | **yes** — three hand-rolled decoders deleted |
| 6 | Self-observability | **yes** — section 10 |

**Why the ring buffer was not built.** It needs a `BPF_MAP_TYPE_RINGBUF` added to
`contoso_sockops.c`, which means editing and reloading the eBPF program — and reloading
requires the JIT-capable runtime swap that section 7 describes as a security downgrade
we would rather reverse than depend on further. The cost is a real blind spot: a
connection that opens and closes entirely between two scrapes is never seen. It is the
first thing to build if the kernel layer is taken further.

### The two defects

**Defect 1, the `flow_map` leak.** Fixed, by the proposal's *alternative*: the collector
deletes entries after exporting them. The suggested `BPF_MAP_TYPE_LRU_HASH` one-word fix
was not applied, for the same reason as the ring buffer — it means touching the C.

**Defect 2, `client.port` is not a safe join key.** Addressed as the proposal argued it
should be: entity join as the foundation, connection join as enrichment, and qualified
by host rather than trusting the port alone.

### Where the shape changed

| | Proposed | Built | Why |
|---|---|---|---|
| Split | probe + agent on **each host**, named pipe | probe per host, collector per **site**, HTTP | section 3 |
| Backend | undecided; leaning local `otel-lgtm` | loveheartbeat, bespoke 4-endpoint contract | its OTLP route needs Elasticsearch, which a Lambda deployment does not have |
| Export | OTLP/gRPC to the backend | OTLP/HTTP *into* the collector; JSON out | as above |
| Backpressure | `memory_limiter` first, `batch` last | disk spool with a byte quota, batched at 200 | a spool survives a restart; a memory limiter does not |
| Control channel | — | heartbeat polling (was a WebSocket) | Lambda cannot hold a socket |

### What the proposal did not anticipate

Enrollment and a non-expiring credential; DPAPI at rest; the hand-written OTLP protobuf
decoder; the env selector; and the whole question of how a *fleet* is represented,
which is what moved the collector off the host.

---

## What is not built

Deliberate omissions, roughly in the order they would matter.

- **Ring buffer.** Collection polls. Short-lived connections between scrapes are missed.
- **Metrics forwarding.** Counted and dropped; reported, not hidden.
- **ETW fallback.** `IFlowSource` exists so it can be added without touching anything
  above it. Only `EbpfFlowSource` is written.
- **Collector clustering.** One per site, no failover pair. The spool protects against
  the cloud being unreachable, not against the collector itself being down.
- **Auto-update** for either binary.
- **mTLS on the LAN hop.** A shared key over plain HTTP, assuming a trusted network.
- **The eBPF program does not survive a reboot.** Re-run `load.ps1`.
