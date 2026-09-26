# ContosoPizza — a worked example of on-prem telemetry

A small ASP.NET Core application, an eBPF kernel probe, and a collector that streams
both to [loveheartbeat.com](https://loveheartbeat.com) over one outbound connection.

It exists to answer one question end to end: **how does telemetry get from a machine
inside somebody's network to an observability backend outside it, without opening a
port, and arrive joined up rather than as three unrelated piles of data?**

**[ARCHITECTURE.md](ARCHITECTURE.md)** is the full design: the three processes, the
joins, the auth model, and an honest reckoning against
[the original proposal](observability-architecture.txt) — including the two things it
called essential that were not built, and why.

Everything here has been run. The numbers in [Verified](#verified) are from a real
collector talking to a real deployment, not a design document.

---

## The shape

```
  ONE MACHINE, OR FIFTY                                   LOVEHEARTBEAT
 ┌──────────────────────────────────────────┐
 │  ContosoPizza (the app)                  │
 │    OpenTelemetry SDK ──── OTLP ──┐       │
 │                                  │       │
 │  contoso_sockops.c (eBPF)        │       │
 │    kernel flow_map ──── probe ───┤       │
 │                                  ▼       │
 │                          ┌──────────────┐│
 │                          │  COLLECTOR   ││══ HTTPS 443, outbound ══▶  /api/v1/collectors
 │                          │  one per site││       one credential
 │                          └──────────────┘│       spooled to disk
 └──────────────────────────────────────────┘
```

Three processes, three jobs:

| | Runs | Elevation | Holds a cloud credential |
|---|---|---|---|
| **App** | wherever your service runs | no | no |
| **Probe** | every monitored server | **yes** — reads kernel maps | no |
| **Collector** | one per site | no | **yes, the only one** |

The app needs no agent and no code change — three standard OpenTelemetry environment
variables point its existing exporter at the collector.

### Why a collector rather than an agent on each box

An agent per server that talks to the cloud directly needs a credential per server.
Fifty machines is fifty secrets to issue, rotate and revoke. Worse, nothing can then
answer *"is this fleet reporting?"* — each agent speaks only for itself, and the one
that died yesterday is invisible precisely because it died.

The collector is the aggregation tier, the same role as Dynatrace's ActiveGate or an
OpenTelemetry Collector in gateway mode. It holds the only credential, the only disk
spool, and the only view of the whole site.

The probe stays because it has to: eBPF maps are kernel-local, so something must run on
each machine to read them. It is deliberately the smallest thing that can — no
credential, no spool, no cloud endpoint, and it drops rather than buffering to disk.

---

## How it connects to loveheartbeat

### 1. Enrollment happens once

An operator adds a collector in the dashboard and gets a one-time token. The collector
exchanges it for a **credential that does not expire**, and encrypts that credential on
its own disk with DPAPI under the machine account.

```
POST /api/v1/collectors/enroll   { "token": "<one-time>" }
  → { "collector_id": "col_…", "collector_credential": "ccred_…", "session_token": "csess_…" }
```

The token is spent on first use. After that the collector never needs a human again.

### 2. Every request carries a session, not the credential

```
POST /api/v1/collectors/session     X-Collector-Credential: ccred_…
POST /api/v1/collectors/heartbeat   X-Collector-Session: csess_…
POST /api/v1/collectors/telemetry   X-Collector-Session: csess_…
```

The server slides the session forward on each use, so an active collector never sees it
expire. If one is refused anyway, the collector mints a new one from the credential and
retries the same batch — authentication recovers itself, and no data is lost doing it.

A collector installed inside a customer's network by somebody who will not log in again
for a year is the reason the credential has no clock on it. An expiring one is a silent
telemetry outage on a random Tuesday.

### 3. Everything is outbound

The collector dials out over 443. Nothing dials in. No port forwarding, no tunnel, no
firewall change — the connectivity model of the Power BI on-premises data gateway.

It *does* listen, but only on the LAN, on `:5200`, for its own probes and for
applications exporting OTLP to it.

### 4. One detail that is easy to miss

`app.loveheartbeat.com` fronts three isolated backends and a router picks between them
by an opaque token. Without it every call is refused with:

```json
{"detail": "The env selector is required in the JSON payload"}
```

which reads exactly like a broken collector and is not one. Take it from the deployed
site's `/assets/js/runtime-config.js` and set `CONTOSO_COLLECTOR_ENV_TOKEN`.

---

## What arrives, and how it joins up

Three signals, and the joins are the point. Any of them alone is a pile of data.

| Signal | From | Stored as |
|---|---|---|
| **Spans** | app's OTel exporter | `agent-spans` → API Monitoring, Traces |
| **Logs** | app's OTel logging provider | `agent-logs`, `source: otel` |
| **Flows** | eBPF probe | `agent-logs`, `source: ebpf` |

```
       span  ──── trace_id ────▶  log
         │
    client.port
         │
         ▼
       flow  (network.peer.port)
```

**Span → log** on `trace_id`. The logging provider stamps the active trace on every
record, so a request's log lines are findable from the request rather than grepped for.

**Span → flow** on the client's ephemeral port. The app tags every span with
`client.port`; the probe reports the same number as the flow's peer port. Nothing else
ties a request to a socket — a pid is per-process, and a timestamp is not precise
enough on a busy listener. Host is checked too, because ports are reused per machine.

That join is why both instrumentation layers exist. The span says a request took two
seconds. The flow says whether the connection was open for two seconds, or closed early
and the time went somewhere else entirely.

---

## Running it

Full instructions are in **[agent/ONBOARDING.txt](agent/ONBOARDING.txt)**. The short
version, in order:

```powershell
# 1. Collector — one per site, no elevation
cd agent\ContosoPizza.Collector
$env:Cloud__Endpoint                    = "https://<your-api-host>"
$env:CONTOSO_COLLECTOR_ENV_TOKEN        = "<from runtime-config.js, if your deployment uses one>"
$env:CONTOSO_COLLECTOR_ENROLLMENT_TOKEN = "<one-time, from the dashboard>"
$env:CONTOSO_PROBE_KEY                  = "<a secret you choose>"
dotnet run

# 2. The app — no code change, three environment variables
cd ..\..
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:5200"
$env:OTEL_EXPORTER_OTLP_PROTOCOL = "http/protobuf"
$env:OTEL_EXPORTER_OTLP_HEADERS  = "X-Probe-Key=<the same secret>"
dotnet run --launch-profile http

# 3. Probe — per server, AS ADMINISTRATOR, optional
cd ebpf;  .\setup.ps1;  .\build.ps1;  .\load.ps1
cd ..\agent\ContosoPizza.Probe
$env:CONTOSO_PROBE_KEY        = "<the same secret>"
$env:Probe__CollectorEndpoint = "http://localhost:5200"
dotnet run
```

Check each hop before moving to the next:

```
collector:  dotnet run -- --diagnose     → "All checks passed"
probe:      dotnet run -- --check        → "the collector accepted this probe"
anywhere:   curl http://localhost:5200/status
```

### Endpoints that fail on purpose

A monitoring stack is only worth anything on the bad path, so the app ships three:

| Route | | |
|---|---|---|
| `GET /api/incidents/upstream` | **502** | real outbound call to a dead port — produces a child span |
| `GET /api/incidents/crash` | **500** | unhandled exception, our own bug |
| `GET /api/incidents/slow?ms=2000` | 200 | slow and successful — the case a status-code view cannot see |

---

## Layout

```
ARCHITECTURE.md                 how it is built, and where it departs from the proposal
observability-architecture.txt  the original research and proposal, 17 Sep 2026
Program.cs, Controllers/        the sample app and its OpenTelemetry setup
ebpf/                           contoso_sockops.c and the load/build scripts
agent/
  ContosoPizza.Shared/          wire contracts, shared by probe and collector
  ContosoPizza.Probe/           per-server: reads eBPF maps, posts to the collector
  ContosoPizza.Collector/       per-site: OTLP + flow receiver, spool, cloud session
  ONBOARDING.txt                install and troubleshooting, for an operator
  BACKEND-PROTOCOL.txt          the four endpoints any other backend would implement
  README.md                     collector and probe design notes
```

`ebpf/ebpf-for-windows/` is **not** committed. It is Microsoft's repository, cloned by
`ebpf/setup.ps1` so `contoso_sockops.c` can be built against its headers.

---

## Pointing it at something other than loveheartbeat

The collector speaks four HTTP endpoints. Implement those and it will talk to anything
— there is no SDK to take. **[agent/BACKEND-PROTOCOL.txt](agent/BACKEND-PROTOCOL.txt)**
has the request and response shapes, the status-code retry contract, and a working
receiver in about fifty lines of Python.

The one rule that matters: **the collector deletes its spooled copy the moment you
answer 2xx.** Write the batch through to durable storage before answering.

---

## Verified

Against a real deployment, a real OpenTelemetry SDK exporter, and this application:

| | |
|---|---|
| Enrollment → durable credential → session | pass |
| Restart with **no** enrollment token | reuses the stored credential, 0 re-enrollments |
| Credential encrypted at rest | `ccred_` absent from the file; DPAPI blob |
| **Cloud outage** | 9 records spooled, **0 lost**, all delivered on recovery |
| **Refused session** | 401 → renewed → retried → delivered, 0 lost |
| OTLP protobuf and JSON | both decoded, including int64-as-string |
| Wrong probe key | 401 |
| App → collector → API Monitoring | route, latency and status codes |
| Logs joined by `trace_id` | 16 lines for one failing route |
| Flows joined by `client.port` | 4 connections matched to their requests |

### Known limits

- **OTLP metrics are counted, not stored.** The intake takes logs, spans and flows. The
  count is reported as `otlp_metrics_dropped` on every heartbeat rather than vanishing.
- **The probe polls.** A connection opening and closing between two scrapes is missed.
- **The eBPF program does not survive a reboot.** Re-run `load.ps1`.
- **One collector per site, no failover pair.** The spool protects against the cloud
  being unreachable, not against the collector itself being down.
- **The LAN hop is a shared key over plain HTTP**, which assumes a trusted network.

---

## A defect this design fixes for free

`contoso_sockops.c` never calls `bpf_map_delete_elem`. Closed flows accumulate until
`MAX_FLOWS` (1024), after which `bpf_map_update_elem` fails, its return value is
ignored, and the program **silently stops recording**.

The probe deletes each closed flow after reading it. That fixes the leak without
modifying the eBPF program and guarantees each flow is reported exactly once. It warns
when the map passes 90% full.
