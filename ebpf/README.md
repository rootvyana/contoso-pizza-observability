# ContosoPizza instrumentation

Two layers, because neither one alone is sufficient on Windows.

| | eBPF (`sockops`) | OpenTelemetry |
|---|---|---|
| Connection open/close, duration | yes | no |
| Client address and port | yes | yes |
| Bytes on the wire | yes | no |
| HTTP route, method, status | **no** | yes |
| Request latency, exceptions | **no** | yes |
| GC / thread pool / runtime | no | yes |
| Requires app code change | no | yes |
| Requires admin | yes | no |

**eBPF for Windows has no uprobe, kprobe or tracepoint support.** The registered
program types on this machine are `bind`, `cgroup/{bind,connect,listen,recv_accept}{4,6}`,
`cgroup/connect_authorization{4,6}` and `sockops` — all network hooks. Nothing can
attach to a CLR function, so HTTP-level detail has to come from inside the process.

The two layers join on **client port**: `flow_map`'s key carries `remote_port`, and
`Program.cs` tags every span with `client.port`.

## Order of operations

### 0. The runtime — do this first

`setup.ps1` installs the *toolchain*, not the eBPF runtime. The runtime is a
prerequisite and is not installed by anything in this repository.

Install **eBPF for Windows v1.5.0** from
<https://github.com/microsoft/ebpf-for-windows/releases>, and note which build:

| | Loads an unsigned `.o`? |
|---|---|
| `ebpf-for-windows.x64.1.5.0.msi` | **no** — JIT and interpreter are compiled out |
| `Build.Release.x64.zip` (349 MB) | yes |

The MSI is the right choice for a machine that will run *signed native* programs. To
load `contoso_sockops.o` directly you need the second, and `swap-runtime.ps1` performs
that replacement — read the trade-off in
[ARCHITECTURE.md](../ARCHITECTURE.md#7-reading-the-kernel) before running it, because it
permits unsigned bytecode to be JIT-compiled into kernel mode.

Check what you have:

    netsh ebpf show programs          # runtime responding at all
    Get-Service eBPFCore, NetEbpfExt  # both should be Running

### 1. Toolchain and build

Non-elevated, from this folder:

    .\setup.ps1                 # winget LLVM (~2.5 GB) + clone repo for headers
    .\build.ps1                 # clang -target bpf -> contoso_sockops.o
    .\add-otel-packages.ps1     # pins real NuGet versions, then dotnet build

### 2. Load

Elevated:

    .\load.ps1                  # netsh ebpf add program ... execution=jit

`add program` is not persistent. The program is gone after a reboot; re-run `load.ps1`.

Non-elevated, in a second window — start the app, then:

    .\generate-traffic.ps1

Elevated again:

    .\watch.ps1 -Loop

To remove the program: `.\unload.ps1 -Id <id from 'netsh ebpf show programs'>`.

## Corrections made against the real headers

Two assumptions in the first draft were wrong and are now fixed:

- `BPF_SOCK_OPS_CONNECTION_ESTABLISHED_CB` does not exist. The real enum in
  `ebpf_nethooks.h` is `BPF_SOCK_OPS_ACTIVE_ESTABLISHED_CB` (outbound),
  `BPF_SOCK_OPS_PASSIVE_ESTABLISHED_CB` (inbound accept -- the one that matters
  for a server) and `BPF_SOCK_OPS_CONNECTION_DELETED_CB`. Both establish cases
  are now handled and `outbound` is recorded per flow.
- Ports are confirmed network byte order, but the hand-rolled `SWAP16` was
  replaced with `bpf_ntohs` from `bpf_endian.h`.

Also added, now that `tests/sample/sockops.c` showed it works in this hook:
`bpf_get_current_pid_tgid() >> 32` gives the owning process id per flow.

## Things to verify rather than assume

- **Loopback visibility.** The app binds `127.0.0.1:5176` and `[::1]:5176` only.
  The sockops hook sits on WFP ALE layers, which loopback *should* traverse, but
  this was not empirically confirmed. If `flow_map` stays empty after traffic,
  set `applicationUrl` to `http://0.0.0.0:5176` in `Properties/launchSettings.json`
  and drive requests at the machine's LAN address instead.
- **~~`bpf_sock_ops_t` field names and port byte order.~~** Resolved -- see above.
- **IPv4 only.** `flow_key_t` ignores `remote_ip6`/`local_ip6`. Requests over
  `[::1]` will collide on a zeroed key. Bind to IPv4 for the first run.
- **OTel package versions** in `ContosoPizza.csproj` are guesses; `add-otel-packages.ps1`
  corrects them.

## Where the data lands

Console exporter, so traces and metrics print to the app's stdout. Swap
`.AddConsoleExporter()` for `.AddOtlpExporter()` in `Program.cs` (plus the
`OpenTelemetry.Exporter.OpenTelemetryProtocol` package) to ship to Aspire
Dashboard, Jaeger or Grafana once the shape looks right.


## Load attempt: blocked on execution mode (2026-09-17)

The program compiles and **passes the verifier**:

    netsh ebpf show verification file=contoso_sockops.o type=sockops
    -> Verification succeeded. Program terminates within 0 loop iterations

But it will not load on this machine. Measured, elevated:

| Mode | Result |
|---|---|
| `execution=jit` | `error 129: could not load program` |
| `execution=interpret` | `error 129: could not load program` |
| `execution=native` | `Element not found` (expects a signed `.sys`, not a `.o`) |

### Root cause (corrected)

An earlier note here blamed "JIT is pre-release only", quoting `InstallEbpf.md`.
That was wrong -- every GitHub release is flagged pre-release, including 1.5.0.
The real chain:

1. `libs/ebpfnetsh/programs.cpp:216` prints C `errno`, not an eBPF result code.
   **errno 129 = `ENOTSUP`** in MSVC.
2. JIT and interpreter are compile-time features of `ebpfcore.sys`, gated on
   `CONFIG_BPF_JIT_DISABLED` / `CONFIG_BPF_INTERPRETER_DISABLED`
   (`Directory.Build.props:142-151`).
3. The published **MSI** is built by the `onebranch` CI job with
   `configurations: ["NativeOnlyDebug", "NativeOnlyRelease"]` -- both flags set.
   So no MSI feature selection can enable JIT; the `JIT\ebpfsvc.exe` folder and
   running `EbpfSvc` are misleading leftovers.
4. There is a second gate, `jit_permitted = !hypervisor_code_integrity_enabled`
   (`ebpf_core.c:3015`). **HVCI is OFF on this machine**, so it is not a factor.

### The fix: same version, different build

The v1.5.0 release also publishes `Build.Release.x64.zip` (349 MB), built by the
`regular` CI job from the plain `Release` configuration -- **JIT-capable**.
Installing that over the MSI keeps the version, program-type GUIDs and API
identical. `scripts/setup-ebpf.ps1 -Uninstall` reverses it, and the MSI is kept
locally for rollback.

### Native-mode prerequisites, as measured

- `bpf2c.exe` present at `C:\Program Files\ebpf-for-windowspf2c.exe` -- OK
- **testsigning is already ON** -- a self-signed driver will load, no reboot needed
- Visual Studio -- **MISSING**
- Windows Kits / WDK -- **MISSING**

So native mode needs a kernel build toolchain installed before
`bpf2c` output can become a loadable `.sys`.

### Consequence

**The loopback question is still unanswered.** Whether the sockops hook fires
on 127.0.0.1 cannot be tested until a program actually loads.


## Status (2026-09-17)

**OpenTelemetry half: done.** Packages pinned to 1.18.0 (my earlier 1.13.1 guess
was wrong), `dotnet restore` clean, `dotnet build` succeeds with 0 warnings and
0 errors. Verified by building to a scratch output dir so the running app was not
disturbed. **Not yet active** -- the app (PID 9148) must be restarted to pick it up.

**eBPF half: blocked, awaiting a decision.** The driver swap was stopped by the
permission classifier as "Security Weaken", which is a fair call and was not
worked around. Replacing the shipped NativeOnly runtime with the JIT-capable
`Build.Release.x64` build genuinely lowers this machine's security posture: it
permits unsigned eBPF bytecode to be JIT-compiled and run in kernel mode, which
is precisely what the NativeOnly MSI exists to prevent.

Downloaded but **not installed**, sitting in the session scratchpad:

- `Build.Release.x64.zip` extracted (JIT-capable, `ubpf` present, 419.3 KB driver)
- `ebpf-for-windows.x64.1.5.0.msi` (rollback artifact)

Evidence the candidate build is JIT-capable:

| | installed (MSI, NativeOnly) | candidate (Build.Release) |
|---|---|---|
| `EbpfCore.sys` size | 387.0 KB | 419.3 KB |
| `ubpf` string occurrences | 0 | 3 |

Nothing was installed, swapped or loaded. `netsh ebpf show programs` is empty and
the app is serving normally.

## OpenTelemetry: delivered and verified (2026-09-17)

App restarted via `dotnet run --launch-profile http`, serving on
`http://127.0.0.1:5176`. Console exporter confirmed live:

- **Traces**: `GET WeatherForecast`, `http.route`, `http.response.status_code: 200`,
  `Activity.Duration`, and the enrichment tag **`client.port`** (e.g. 51067) --
  the join key into the eBPF `flow_map`.
- **Metrics**: 28 instruments, including `http.server.request.duration` (histogram),
  `kestrel.connection.duration`, `kestrel.active_connections`, `dotnet.gc.*`,
  `dotnet.thread_pool.*`, `dotnet.exceptions`.

App stdout/stderr are captured at `..\app.log` and `..\app.err`.

> Note: `kestrel.connection.duration` and `kestrel.active_connections` already
> overlap with what the eBPF sockops program measures. The eBPF layer's unique
> value is connections Kestrel never accepts -- refused/reset/half-open flows,
> traffic to ports the app is not listening on -- plus PID attribution that does
> not depend on the app being healthy enough to report.

## eBPF swap: needs one action from you

`swap-runtime.ps1` is written and `C:\ebpf-jit-1.5.0\` is staged, but the elevated
run is blocked by the permission classifier. Run it yourself from an
**administrator** PowerShell:

    <repo>\ebpf\swap-runtime.ps1

It uninstalls the MSI, installs the JIT build, and immediately attempts the JIT
load, logging everything to `swap-runtime.log`.

### Rollback

    cd C:\ebpf-jit-1.5.0
    .\setup-ebpf.ps1 -Uninstall
    msiexec /i C:\ebpf-jit-1.5.0\ROLLBACK-ebpf-for-windows.x64.1.5.0.msi

## RESOLVED: loopback works (2026-09-17)

After swapping to the JIT-capable runtime, the program loaded:

    ID  Pins  Links  Mode   Type     Name
    13     1      1  JIT    sockops  contoso_connection_monitor

**The sockops hook fires on 127.0.0.1.** No rebinding to `0.0.0.0` is needed;
the earlier caveat about loopback possibly bypassing WFP ALE layers is closed.

Cross-checked against Windows' own accounting:

| | eBPF `flow_map` | `Get-NetTCPConnection` |
|---|---|---|
| Flow | `127.0.0.1:5176 <- 127.0.0.1:51125` | `127.0.0.1:5176 <- 127.0.0.1:51125` |
| PID | 3616 | 3616 |
| Count | 1 established, 1 open | 1 Established |

PID 3616 is `ContosoPizza.exe`, so per-flow process attribution is real.

### Two defects the live data exposed, both fixed

1. **Loopback double-counted.** Both ends of a 127.0.0.1 connection are local,
   so `ACTIVE_ESTABLISHED` (client) and `PASSIVE_ESTABLISHED` (server) each fire
   for one TCP connection -- the counters read `Established: 4` for 2 connections.
   Aggregate counters now only count the server side (`local_port == CONTOSO_PORT`).
   Both directions are still recorded in `flow_map`. Re-tested: `Established: 1`
   for 1 real connection.
2. **`ClientPort` was mislabeled on outbound rows**, showing 5176, because
   `remote_port` is the server port on an outbound flow. Now blank unless inbound.

### The two layers joined

Ten requests produced **one** TCP connection (HTTP keep-alive):

- eBPF: one flow, `remote_port` 51125, PID 3616, state OPEN
- OTel: **10 spans** tagged `client.port: 51125`, each with `http.route`,
  `http.response.status_code: 200` and sub-millisecond `Activity.Duration`

That contrast is the point of running both: OTel says ten requests averaged
~0.2 ms; eBPF says they shared one socket held open for seconds. Neither layer
can tell you that alone.

### Managing the loaded program

    netsh ebpf show programs                 # list (needs admin)
    .\read-results.cmd                       # traffic + decoded maps
    .\unload.ps1 -Id 13                      # remove

The program does not survive a reboot; re-run `load.ps1` (elevated) to restore it.
