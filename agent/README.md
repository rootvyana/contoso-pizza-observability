# ContosoPizza fleet telemetry

Two binaries. A **probe** on every monitored server, and one **collector** per
site that streams the whole fleet to loveheartbeat.com.

Both instrumentation layers are unchanged. This is the transport that was
missing.

```
  ON-PREM                                              LOVEHEARTBEAT.COM
 +--------------------------------------------+
 | web-01  eBPF -> probe --+                  |
 |         app  -> OTLP  --+                  |
 |                          \                 |
 | web-02  eBPF -> probe ----+--> COLLECTOR   |==== 443, outbound ===> /api/v1/collectors
 |         app  -> OTLP  ----+    one per site|     one credential
 |                          /                 |     spooled, durable
 | db-01   eBPF -> probe --+                  |
 +--------------------------------------------+
```

## Why a collector rather than an agent per server

A per-server agent that talks to the cloud directly needs a credential per
server, so fifty servers is fifty secrets to issue, rotate and revoke. Worse,
nothing can then answer "is this customer's fleet reporting?" -- each agent can
only tell you about itself, and the one that stopped reporting yesterday is
invisible precisely because it stopped.

The collector is the aggregation tier the same way Dynatrace's ActiveGate and
an OpenTelemetry Collector in gateway mode are. It holds the only credential,
the only disk spool, and the only view of the whole fleet.

The probe stays because it has to: eBPF maps are kernel-local and something must
run on each machine to read them. It is deliberately the smallest thing that
can -- no credential, no spool, no cloud endpoint.

| | Probe | Collector |
|---|---|---|
| Runs on | every monitored server | one per site |
| Needs elevation | yes (reads kernel maps) | no |
| Cloud credential | none | one, durable |
| Disk spool | none | yes |
| Listens on | nothing | LAN only |

## Connectivity

Borrowed from the Power BI on-premises data gateway:

- **Outbound only.** The collector opens connections to the cloud; the cloud
  never opens one to it. No inbound port from the internet, anywhere.
- **Health reported upward** continuously, so the cloud knows whether a
  collector is *working* rather than merely connected.
- **Config pushed downward**, so a scrape interval can be changed from the
  dashboard without touching a customer's machine. It reaches the collector on
  its heartbeat and each probe in the ack to its next POST.
- **A connectivity diagnostic** (`--diagnose`, `--check`), the counterpart to
  Power BI's network ports test, because "can this machine reach the service" is
  the most common deployment failure.

Where it differs: Power BI's gateway is request/response and needs Azure Relay
so the cloud can initiate contact. Telemetry only flows one way -- nobody asks
for it -- so there is no relay and nothing to pay per message.

## Authentication

Three pieces, so that authentication never expires in practice and never needs a
human twice.

| | Lifetime | Where it lives |
|---|---|---|
| Enrollment token | 24h, single use | pasted in once, at install |
| Collector credential | **never expires** | DPAPI-encrypted on the collector's disk |
| Session | 12h, sliding | memory only, renewed automatically |

1. An operator adds a collector in the dashboard and gets a one-time token.
2. The collector exchanges it for a durable credential, which it encrypts under
   the local machine account. The token is spent and is never needed again.
3. Every request carries a short-lived session, not the credential. The server
   slides the session forward on each use, and the collector renews it early on
   the server's own schedule -- so a running collector never presents an
   expired one.
4. If a session is refused anyway, the collector mints a new one from the
   credential and retries the same batch. No data is lost and nobody is paged.

Only an explicit **revoke** in the dashboard ends it, and that takes effect on
the collector's next request rather than whenever its session happens to lapse.

Neither secret is stored server-side: the lookup key is a SHA-256 of the token,
so a dump of the table yields nothing replayable.

## Data channel

Everything a receiver accepts is written to a **disk-backed spool** and drained
by `SpoolPump`. A segment is deleted only after the server returns 2xx.

| Behaviour | Policy |
|---|---|
| Cloud unreachable | spool, exponential backoff, retry forever |
| 5xx, 408, 429 | retry |
| 401 | renew the session, retry once, then treat as retryable |
| Other 4xx | log loudly and discard, so one bad batch cannot block the queue |
| Spool over `MaxSpoolBytes` | drop oldest segments, report the loss |
| Crash mid-send | segment survives; a duplicate is possible, a loss is not |

Delivery is **at-least-once**. Every flow carries a stable id so the far side
overwrites its own row rather than counting a connection twice.

## What the collector accepts

| Endpoint | From | Goes to |
|---|---|---|
| `POST /v1/flows` | probes | `agent-logs`, `source: ebpf` |
| `POST /v1/traces` | any OTLP exporter | `agent-spans` -> API Monitoring |
| `POST /v1/logs` | any OTLP exporter | `agent-logs`, `source: otel` |
| `POST /v1/metrics` | any OTLP exporter | counted, not stored (see below) |
| `GET /health`, `GET /status` | you | local answer, no dashboard needed |

Both OTLP encodings work -- protobuf (every SDK's default) and JSON -- decided
by `Content-Type`.

Flows go to the log stream rather than the span stream on purpose: `route_stats`
groups spans on `http.route`, and a TCP flow has none, so filing them there
would put rows on the API Monitoring page that are not API traffic.

## Setup

### 1. In loveheartbeat, add a collector

```
POST /api/v1/collectors   {"name": "HQ collector"}
```

The response carries a one-time `enrollment_token`. The server needs
`PINGHOLD_RECORDS_TABLE` set (DynamoDB). **No Elasticsearch is involved** --
this path uses the same DynamoDB streams the API Monitoring page already reads.

### 2. Run the collector, once per site

```
cd ContosoPizza.Collector
set CONTOSO_COLLECTOR_ENROLLMENT_TOKEN=<token from step 1>
set CONTOSO_PROBE_KEY=<a secret you choose>
set Cloud__Endpoint=https://your-api-host
dotnet run
```

It listens on `0.0.0.0:5200`. Check it with `dotnet run -- --diagnose`.

### 3. Run a probe on each server, elevated

```
cd ContosoPizza.Probe
set CONTOSO_PROBE_KEY=<the same secret>
set Probe__CollectorEndpoint=http://collector-host:5200
dotnet run
```

Check it with `dotnet run -- --check` (no elevation needed).

### 4. Point each application's OTLP exporter at the collector

No code change -- the standard environment variables are enough:

```
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector-host:5200
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=X-Probe-Key=<the same secret>
```

## Commands

| Command | Binary | Elevation | Purpose |
|---|---|---|---|
| `--exports` | probe | no | resolve every `EbpfApi.dll` export it P/Invokes |
| `--check` | probe | no | can this server reach its collector |
| `--dump` | probe | **yes** | one-shot read of the kernel maps |
| *(none)* | probe | **yes** | run |
| `--diagnose` | collector | no | can this machine reach and authenticate to the cloud |
| *(none)* | collector | no | run |

`dump-flows.cmd` and `run-probe.cmd` are right-click-run-as-administrator
wrappers. `verify.cmd` runs the whole chain end to end.

## Running as Windows services

Both binaries support it.

```
sc.exe create ContosoCollector binPath= "...\ContosoPizza.Collector.exe" start= auto
sc.exe create ContosoProbe     binPath= "...\ContosoPizza.Probe.exe"     start= auto
```

The probe must run as an account with administrator rights to read the eBPF
maps. The collector does not.

## Verified end to end

Against the real loveheartbeat backend, the real OpenTelemetry .NET SDK and the
real ContosoPizza app:

| Check | Result |
|---|---|
| Enrollment -> durable credential -> session | pass |
| Credential encrypted at rest | `ccred_` not present in the file; DPAPI blob |
| Restart with **no** enrollment token | reuses the stored credential, 0 re-enrollments |
| Probe flows reach `agent-logs` with attributes intact | 3/3, `client.port` preserved |
| OTLP protobuf traces reach `agent-spans` | pass |
| OTLP JSON traces, int64-as-string | pass, 500-status row correct |
| OTLP protobuf logs | pass |
| Real app -> collector -> API Monitoring page | `WeatherForecast`, 7 requests, 6.7ms avg |
| Wrong probe key | 401 |
| **Cloud outage** | 9 records spooled, 0 lost, all 9 delivered on recovery |
| **Refused session** | 401 -> renewed -> retried -> delivered, 0 lost |
| Fleet view | 3 servers under one collector, health `healthy` |

`--dump` against the real kernel maps still needs an elevated shell; run
`verify.cmd` as administrator.

## Not built

Deliberate omissions, roughly in the order they would matter:

- **Metrics forwarding.** The cloud intake takes logs, spans and flows. OTLP
  metric points are counted and dropped, and the count is reported as
  `otlp_metrics_dropped` on every heartbeat rather than being hidden.
- **Probe durability.** A probe buffers in memory and then drops. Durability is
  the collector's job, in one place, with one quota. A LAN hop is not the
  failure mode at-least-once delivery exists to survive.
- **Collector clustering.** One per site, no failover pair.
- **Auto-update** for either binary.
- **Ring buffer.** Collection still polls, so a connection that opens and closes
  between two scrapes is missed. Fixing it means editing and reloading
  `contoso_sockops.c`.
- **ETW fallback.** `IFlowSource` exists so it can be added without touching
  anything above it; only `EbpfFlowSource` is written.
- **mTLS on the LAN hop.** A shared key over plain HTTP inside the customer's
  network.

## A defect this fixes for free

`contoso_sockops.c` never calls `bpf_map_delete_elem`. Closed flows accumulate
until `MAX_FLOWS` (1024), after which `bpf_map_update_elem` fails, its return
value is ignored, and the program **silently stops recording**.

The probe deletes each closed flow after reading it. That fixes the leak without
modifying the eBPF program and guarantees each flow is reported exactly once.
The probe warns when the map passes 90% full.
