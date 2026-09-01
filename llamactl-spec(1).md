# llamactl — control plane for local llama.cpp fleet

Draft spec, v0.1

## Purpose

Replace the manual workflow (SSH, `systemctl`, hand-edited `models.ini`,
`hf download`, ad-hoc `llama-bench`) with a web UI over one or more nodes
running llama.cpp on ROCm.

### Goals

- Manage llama.cpp **instances** — router mode, single-model, or ephemeral
- Edit and validate model presets without hand-editing INI over SSH
- Download models with size-vs-VRAM awareness before committing
- Run and retain benchmarks
- See what's happening: slots, logs, GPU telemetry
- Work across multiple nodes from one pane

### Non-goals

- **Installing ROCm.** One-time, root-level, reboot-adjacent. Belongs in an
  idempotent shell script or Ansible role — a web app can't bootstrap the
  machine it runs on. Sibling project.
- Being a chat UI. llama.cpp ships one; link to it instead.
- Training or fine-tuning.

## Architecture

```
┌──────────────────────────┐
│  llamactl.Web (Blazor)   │  control plane — one instance
│  SQLite + EF Core        │  history, config, node registry
└───────────┬──────────────┘
            │ HTTPS + SignalR (agent-initiated)
   ┌────────┼────────┬─────────────┐
   │        │        │             │
┌──▼──┐  ┌──▼──┐  ┌──▼──┐
│agent│  │agent│  │agent│           llamactl.Agent — one per node
└──┬──┘  └─────┘  └─────┘
   │
   ├── llama.cpp instances (spawned + supervised)
   ├── /models filesystem + HF cache
   ├── rocm-smi
   └── systemd (optional, for boot persistence)
```

**Control plane** (`llamactl.Web`): Blazor, no direct access to any node's
filesystem or processes. Holds the database, aggregates node state, serves UI.

**Agent** (`llamactl.Agent`): small ASP.NET Core worker on each node, inside
the same container as llama.cpp. Owns everything local. Connects *outbound* to
the control plane over SignalR so nodes don't need inbound firewall rules, and
so a node coming back after reboot re-registers itself.

Both target .NET 10. Agent ships as a single-file publish plus a systemd unit.

## Core abstraction: the instance

Everything the agent runs is an **instance** — a supervised process with a spec
and a lifecycle. The platform cares only whether it is long-lived or runs to
completion; what it actually launches is decided by its runtime provider (see
[Runtimes](#runtimes)).

```csharp
enum InstanceKind { Managed, Ephemeral }
// full InstanceSpec defined under Runtimes
```

- **Managed** — long-lived, supervised, optionally persistent across reboot.
- **Ephemeral** — runs to completion, output captured, then discarded.

For llama.cpp, the `Profile` field selects between its two managed shapes:

- **`router`** — today's setup. Agent starts one parent; llama.cpp spawns its
  own children per model. Runtime state read from the router's HTTP API.
- **`single`** — one model, one process, explicit flags. For pinning a model on
  a dedicated port, or trying flags without touching `models.ini`.

Ephemeral covers `llama-bench` runs and `llama-cli -no-cnv` smoke tests.

Multiple instances coexist. The agent tracks port allocation and refuses specs
that would exceed a configured VRAM budget for the node — a platform concern,
identical for every runtime.

## Node configuration

Every path the agent touches is configuration, not a constant. Nodes will differ
— different containers, different mount points, different volume sizes — and
hardcoding tonight's layout would make the second node painful.

**Path configuration lives in the control plane, not on the node.** The agent
ships with only a bootstrap file (control plane URL, node name, shared secret);
everything else is pushed down after the operator configures it in the UI. That
way adding a node is a form, not an SSH session, and the config is backed up and
versioned centrally along with everything else.

This makes one rule non-negotiable throughout the rest of the spec: **no feature
may assume a path.** Downloads, flat-dir reconciliation, preset editing,
benchmarks and log reading all resolve their paths from the node record. A node
with models on `/mnt/tank/models` and another on `/models` are equally normal.

Stored per node, shown here as YAML for readability:

```yaml
node:
  name: node-01
  vramBudgetMiB: 98304        # BIOS carve-out; instances are refused past this
  portRange: [48000, 48999]   # for agent-assigned instance ports

paths:
  llamaBin:    /opt/llama.cpp/build/bin   # llama-server, llama-bench, llama-cli
  llamaSource: /opt/llama.cpp             # for git pull + rebuild
  rocm:        /opt/rocm                  # symlink → /opt/rocm-7.2.1
  modelsRoot:  /models                    # the dedicated volume
  hfHome:      /models                    # HF_HOME; cache lands in $hfHome/hub
  flatDir:     /models/flat               # what --models-dir points at
  presetFile:  /models/models.ini
  emptyCache:  /models/emptycache         # LLAMA_CACHE, see note
  systemdDir:  /etc/systemd/system
  configRepo:  /models/.llamactl-git      # git history for presets + units

defaults:
  ngl: 99
  jinja: true
```

Notes that belong in the config rather than in someone's head:

- **`emptyCache`** exists because the router scans the HF cache *in addition* to
  `--models-dir`. Pointing `LLAMA_CACHE` at an empty directory is what stops
  every model appearing twice and bare draft heads being offered as chat models.
  The agent should create it and set the variable, not leave it to be
  rediscovered.
- **`hfHome` and `modelsRoot` are usually the same volume** but need not be. The
  fit calculator cares about free space on `modelsRoot`; downloads write to
  `hfHome`.
- **`flatDir` is derived, not authoritative.** It's rebuilt from the HF cache by
  the reconciliation rules. Nothing should live there that isn't a symlink.

### Onboarding a node

The flow when a new agent first connects, and the main place path configuration
gets set:

1. **Agent connects** with its bootstrap secret and reports what it can see
   without being told: distro, kernel, GPU from `rocminfo`, VRAM, llama.cpp
   version if a binary is on PATH, ROCm version, and the mounted filesystems
   with their free space.
2. **Agent proposes paths.** Rather than presenting empty boxes, it scans for
   likely candidates — `llama-server` on PATH or under `/opt`, an existing
   `HF_HOME` or `~/.cache/huggingface`, `/opt/rocm*`, the largest writable
   non-root mount as a models volume. Each proposal is a suggestion with a
   reason shown, never applied silently.
3. **Operator confirms or overrides** each path in the UI, sets the VRAM budget
   and port range. Defaults derive from what the agent found: VRAM budget from
   what `rocminfo` reports, not typed in from memory.
4. **Agent applies.** Creates any missing directories (`flatDir`, `emptyCache`,
   `configRepo`), initialises the git repo, writes the environment into the
   systemd units it generates rather than depending on `/etc/profile.d`, which
   interactive-but-not-login shells don't read.
5. **Validation runs** (below). Failures block activation and name the specific
   problem.
6. **Adopt existing setup.** If a models directory and `models.ini` already
   exist — as on an already-configured node — the agent imports them rather
   than overwriting:
   presets become the first git revision, existing GGUFs get catalogued, and any
   already-running llama.cpp process is offered for adoption as a managed
   instance.

Reconfiguration later uses the same form. Changing `modelsRoot` on a live node
is the interesting case: the agent should refuse while instances are running,
and offer to re-reconcile the flat directory against the new location rather
than leaving dangling symlinks.

### Validation on registration

When an agent connects, it checks and reports: binaries present and executable,
ROCm resolvable, each path exists and is writable, free space on the models
volume, and whether `rocminfo` enumerates a GPU. A node failing any of these
shows as degraded with the specific failure rather than silently accepting
instance starts that will fail at spawn.

Worth including because it catches the two failures from this build: a rootfs
too small for the next model unpack, and PATH not carrying the llama.cpp bin
directory.

## Modules

### 1. Nodes

Registry of agents: hostname, last seen, llama.cpp build (`--version`), ROCm
version, GPU model, total/free VRAM, disk free on the models volume.

The agent parses `llama-server --help` at startup and reports the **full flag
schema** to the control plane. This matters: nodes will run different llama.cpp
builds, and flag validation must be per-node, not hardcoded. It's how the preset
editor knows `spec-type` exists on one node and not another.

### 2. Instances

List, start, stop, restart, edit. Live status via agent push. Log tail streamed
to the browser (agent reads the process's stdout/stderr, and journald when the
instance is systemd-managed).

Persistent instances get a generated systemd unit so they survive reboot. The
agent owns the unit file — no hand-editing.

### 3. Presets (`models.ini`)

Parse and emit the INI format, including the `[*]` global section and the
precedence rules (CLI > model section > global).

- Form-based editor per model, with a raw text fallback
- Keys validated against the node's reported flag schema — a typo fails in the
  browser, not at model load
- Diff preview before write
- Every write commits to a git repo (LibGit2Sharp), so a bad edit is one click
  to revert
- Explicit warning that changing a preset requires a router restart, which
  drops loaded models

Known llama.cpp gotcha to encode: preset section names must match the model
name the router derives from the filename or directory name.

### 4. Model library

The filesystem view of `/models`, and the part most worth automating because
the rules are fiddly:

- Single-file models → loose symlink in the flat dir
- Multi-shard → own subdirectory, **all** shards inside (llama.cpp resolves
  siblings from the same directory)
- Multimodal → model plus its `mmproj*` file in one subdirectory
- Draft heads (`dflash*`, MTP sidecars) → excluded from the flat dir, since the
  router would otherwise offer them as chat models

The agent reconciles the flat directory from the HF cache by these rules, so
"rebuild library" is a button rather than a shell one-liner with `nullglob`.

Also: disk usage per model, orphaned blobs (HF cache blobs with no snapshot
symlink), and delete that removes both the symlink and the blob.

### 5. Downloads

Browse or search HF, list a repo's GGUF files with sizes, pick a quant, pull it
onto a chosen node. Downloads run on the agent (files must land on that node's
disk), with progress streamed to the UI.

**Fit calculator.** Before download, sum the shards of each variant and compare
against the node's VRAM, flagging what fits. This is the feature that would have
caught Qwen3.8-Flash-Next's Q4_K_XL at 103.7 GiB overshooting a 96 GiB budget
without arithmetic by hand.

Rough KV estimate alongside it, with the caveat that it varies wildly by
architecture — a hybrid Mamba/linear-attention model carries far less cache per
token than a dense transformer, so the estimate should be labelled as such
rather than presented as a hard number.

**GGUF metadata inspection** on completion: read `general.architecture`,
`n_ctx_train`, and scan for `nextn`/MTP tensors. If a draft head is present in
the file, surface a prompt to enable `spec-type = draft-mtp` — otherwise it sits
idle and unused, which is easy to miss.

### 6. Benchmarks

Run `llama-bench` as an ephemeral instance, parse its markdown table, store the
rows against (node, model, quant, llama.cpp build, flags).

- Compare quants of one model, or one model across nodes
- Chart size vs decode speed
- Regression view: same model before and after a llama.cpp upgrade
- Export the whole set as markdown

Guard: refuse to start a benchmark while a serving instance holds VRAM, or warn
loudly — contended runs produce numbers that aren't comparable.

Speculative decoding needs separate handling: `llama-bench` has no draft flags,
so drafted configurations must be measured through a real generation on a fixed
prompt, recorded as a distinct measurement type so the two never get compared
directly.

### 7. Slots & runtime

Poll `/slots?model=<name>` per loaded model on router instances. Show per slot:
prompt tokens, cached tokens, decoded tokens, sampling params in effect.

Alerts worth having, all drawn from real failures:

- `n_predict: -1` — client sent no `max_tokens`, so nothing bounds a runaway
- decoded tokens past a threshold — likely a reasoning loop
- `n_prompt_tokens_cache: 0` on large prompts — full prefill every turn

Cancel needs verification: llama.cpp may not expose per-slot cancellation, in
which case the honest fallback is restarting the instance, and the UI should say
so rather than pretending to be surgical.

### 8. Telemetry

`rocm-smi` (or `amdgpu_top --json`) on an interval: VRAM used, utilisation,
temperature, power. Overlay tokens/sec from active slots. Retain briefly —
enough to see a load spike, not a metrics platform.

### 9. Logs

Streamed tail with level filter and search. Promote the load-time warnings that
actually matter to a per-model health panel, since they're buried in journald
today:

- `special_eot_id is not in special_eog_ids` — stop tokens misregistered
- `model has unused tensor blk.N.nextn.*` — idle draft head
- `chat template supports preserving reasoning`
- `failed to fit params to free device memory`

### 10. Security

Currently the servers run with CORS `*` and no API key. The panel should
manage `--api-key` per instance, show current exposure, and warn on
`--host 0.0.0.0` without a key.

The agent runs privileged (systemd, filesystem, process spawn), so agent↔control
authentication needs a shared secret or mTLS from day one, not later.

## Solution shape — vertical slice architecture

```
llamactl.sln
├── llamactl.Web/              Blazor Server + control plane
│   ├── Features/              ← slices live here, nothing else does
│   │   ├── Nodes/
│   │   │   ├── Onboard/       Command, Handler, Validator, Onboard.razor
│   │   │   ├── Configure/     paths, budgets, port range
│   │   │   ├── List/
│   │   │   └── Health/
│   │   ├── Instances/
│   │   │   ├── Start/  Stop/  Edit/  Adopt/  TailLog/
│   │   ├── Presets/
│   │   │   ├── Edit/  Validate/  History/  Revert/
│   │   ├── Models/
│   │   │   ├── Library/  Reconcile/  Inspect/  Delete/
│   │   ├── Downloads/
│   │   │   ├── Search/  EstimateFit/  Start/  Progress/
│   │   ├── Benchmarks/
│   │   │   ├── Run/  Compare/  Export/
│   │   └── Observability/
│   │       ├── Slots/  Telemetry/  Alerts/
│   ├── Platform/              cross-cutting only
│   │   ├── NodeGateway/       the one place that talks to agents
│   │   ├── Persistence/       DbContext, migrations
│   │   ├── Auth/
│   │   └── Pipeline/          validation, logging, error mapping
│   └── Program.cs
├── llamactl.Agent/            node-side worker
│   ├── Features/              mirrors the command set
│   │   ├── Instances/  Files/  Downloads/  Bench/  Telemetry/  Logs/
│   └── Platform/              ProcessSupervisor, HubClient, PathResolver
└── llamactl.Contracts/        the wire protocol — shared by both, nothing else
```

### Slice anatomy

One folder per use case, containing everything that use case needs. No
`Services/`, no `Repositories/`, no shared `IInstanceService` that fourteen
features depend on.

Built on **ImmediatePlatform** (Immediate.Handlers / .Apis / .Validations) —
source-generated, so the request is an ordinary class with an ordinary
constructor and the endpoint is generated from an attribute. See
`llamactl-web-spec.md` for the full treatment.

```csharp
// Features/Instances/Start/StartInstance.cs
[Handler]
[DefaultBehaviors]
[MapPost("/api/v1/nodes/{nodeId:guid}/instances")]
public sealed partial class StartInstance(
    IDbContextFactory<LlamactlDb> dbFactory, INodeGateway nodes)
{
    [Validate]
    public sealed partial record Command : IValidationTarget<Command>
    {
        public required Guid NodeId { get; init; }
        public required InstanceSpec Spec { get; init; }
    }

    public sealed record Response(Guid InstanceId, int Port);

    private async ValueTask<Result<Response>> HandleAsync(
        Command command, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);

        var node = await db.Nodes.FindAsync([command.NodeId], token);
        if (node is null)     return Result.NotFound("node");
        if (!node.IsHealthy)  return Result.Conflict("node is degraded");

        var committed = await db.VramCommittedAsync(node.Id, token);
        if (committed + command.Spec.EstimatedVramMiB > node.VramBudgetMiB)
            return Result.Conflict("would exceed the node's VRAM budget");

        var ack = await nodes.SendAsync(node.Id,
            new StartInstanceMessage(command.Spec), token);   // → llamactl.Contracts
        …
    }
}
```

The slice owns its own DTOs — they're nested inside the handler class, so
`StartInstance.Response` and `ListInstances.Response` coexist without
contortion. That's the point, not an accident to be refactored away.

**The one shared thing is `llamactl.Contracts`** — the control-plane↔agent
protocol. It's shared because both sides must agree on it byte for byte, and it
versions independently of the UI.

### Cross-cutting rules

- Slices talk to agents only through `INodeGateway`. Nothing else opens a
  connection to a node.
- **A slice may call another slice directly** by injecting its generated
  `Handler` type. No wrapper interface, no service extracted purely to make the
  call look decoupled — the dependency is real, so let it be visible in the
  constructor and greppable.
- Two constraints on that, both about keeping it navigable rather than pure:
  call the other slice's *contract* (its command, query or result), not its
  internals; and no cycles — if A calls B, B must not call A. A cycle means the
  shared part belongs in `Platform`, or the two slices are one slice.
- Fan-in is the signal to watch. When three or more slices call the same
  handler, it has stopped being a feature and become platform logic. Move it
  then, not before.
- Blazor components live *in* the slice, next to the handler they call. Shared
  components go in `Platform/Components` and stay dumb.

## UI contract

Blazor Server means components can call handlers in-process — there's no need
for an HTTP hop just to render a page. So the "UI contract" is the command and
query surface, plus the live channels.

An HTTP API over the same handlers is worth exposing anyway (thin controllers,
one line each) so a CLI or a script can drive it. It is *not* what the Blazor UI
uses.

### Commands and queries

| Slice | Contract | Returns |
|---|---|---|
| Nodes/Onboard | `OnboardNodeCommand(bootstrapToken, name)` | `NodeView` + proposed paths |
| Nodes/Configure | `ConfigureNodeCommand(nodeId, NodePaths, budgets, rowVersion)` | `Result<NodeView>` |
| Nodes/List | `ListNodesQuery()` | `IReadOnlyList<NodeSummary>` |
| Instances/Start | `StartInstanceCommand(nodeId, spec)` | `Result<InstanceView>` |
| Instances/Stop | `StopInstanceCommand(instanceId, force)` | `Result` |
| Instances/Adopt | `AdoptInstanceCommand(nodeId, pid, port)` | `Result<InstanceView>` |
| Presets/Validate | `ValidatePresetQuery(nodeId, iniText)` | `IReadOnlyList<PresetDiagnostic>` |
| Presets/Edit | `SavePresetCommand(nodeId, iniText, gitSha)` | `Result<PresetRevision>` |
| Presets/History | `PresetHistoryQuery(nodeId, take)` | revisions + diffs |
| Models/Library | `ModelLibraryQuery(nodeId)` | files, sizes, orphaned blobs |
| Models/Reconcile | `ReconcileFlatDirCommand(nodeId, dryRun)` | planned symlink ops |
| Downloads/EstimateFit | `EstimateFitQuery(nodeId, repo)` | per-quant size + verdict |
| Downloads/Start | `StartDownloadCommand(nodeId, repo, includeGlobs)` | `DownloadId` |
| Benchmarks/Run | `RunBenchmarkCommand(nodeId, modelFileId, flags)` | `BenchRunId` |
| Benchmarks/Compare | `CompareBenchmarksQuery(filter)` | rows for charting |
| Observability/Slots | `SlotsQuery(instanceId)` | slot snapshots |

### Error model

Handlers return `Result<T>` with a typed reason — `NotFound`, `Conflict`,
`Validation`, `NodeUnreachable`, `AgentError` — never exceptions for expected
failures. The HTTP layer maps these to `ProblemDetails`; Blazor components map
them to inline messages.

`AgentError` carries the node's own message verbatim. When `llama-server` exits
because a preset key is invalid, the operator should see llama.cpp's stderr, not
"an error occurred".

### Live channels

The UI runs on an interactive server circuit, which *is* a live SignalR
connection — so the browser opens no second hub. These are the topics on an
in-process bus that components subscribe to; fan-out happens server-side once,
so ten open tabs cause no extra agent traffic. Delivery mechanics, backpressure
and subscription lifetime are specified in `llamactl-web-spec.md`.

| Event | Group | Rate |
|---|---|---|
| `NodeStateChanged` | `node:{id}` | on change |
| `InstanceStateChanged` | `node:{id}` | on change |
| `LogAppended` | `logs:{instanceId}` | batched, ~200ms |
| `DownloadProgress` | `download:{id}` | throttled, ~1s |
| `SlotSnapshot` | `slots:{instanceId}` | polled, ~2s |
| `TelemetrySample` | `telemetry:{nodeId}` | ~2s |
| `BenchProgress` | `bench:{runId}` | per line |
| `AlertRaised` | `node:{id}` | on change |

Subscriptions are explicit — a component joins the group it needs on render and
leaves on dispose. Nothing streams to nobody.

### Concurrency

Preset edits and node configuration carry a token (`gitSha` and `rowVersion`
respectively). A stale token returns `Conflict` with the current value so the UI
can show a diff rather than silently clobbering a change made from another tab
or by another operator.

## Control plane ↔ node contract

### Transport

Agent-initiated SignalR over WebSocket, outbound only. Nodes need no inbound
firewall rules, and a rebooted node re-establishes on its own. Authentication is
a per-node bearer token issued at onboarding; mTLS if you want it, but the token
is the baseline and must exist from day one since the agent runs privileged.

If WebSocket is unavailable, SignalR's own long-polling fallback applies. No
second transport to maintain.

### Envelope

```csharp
public sealed record Envelope<T>(
    Guid      MessageId,      // idempotency key
    Guid?     CorrelationId,  // ties results to the originating command
    Guid      NodeId,
    int       SchemaVersion,
    DateTimeOffset SentAt,
    T         Payload);
```

The agent keeps a bounded LRU of handled `MessageId`s and returns the cached
result for a repeat. Reconnects will redeliver; commands must be safe to receive
twice.

### Commands (control plane → agent)

| Command | Payload | Result |
|---|---|---|
| `DescribeNode` | — | OS, GPU, ROCm, llama build, mounts, free space |
| `ProposePaths` | — | candidate paths with the reason each was suggested |
| `ApplyConfiguration` | `NodePaths`, budgets, env | validation report |
| `StartInstance` | `InstanceSpec` | pid, port, state |
| `StopInstance` | id, force, timeout | final state |
| `ListProcesses` | — | running llama.cpp processes, for adoption |
| `ReadPreset` / `WritePreset` | ini text, expected gitSha | new gitSha |
| `GetFlagSchema` | — | parsed `--help` for this build |
| `ReconcileFlatDir` | dryRun | planned/applied symlink operations |
| `ScanModels` | — | GGUF inventory with metadata |
| `InspectGguf` | path | arch, n_ctx_train, tensor names, draft head |
| `StartDownload` | repo, globs | download id |
| `CancelDownload` | id | — |
| `DeleteModel` | paths | freed bytes |
| `RunBenchmark` | model, flags | bench run id |
| `TailLog` | instanceId, fromSeq | stream handle |
| `GetSlots` | instanceId | slot snapshots |
| `UpgradeLlamaCpp` | ref, rebuild flags | build log stream |

`UpgradeLlamaCpp` earns its place — `git pull` plus rebuild was needed twice in
one evening for new architectures, and it changes the flag schema, so the agent
must re-report capabilities afterwards.

### Events (agent → control plane)

`NodeAnnounced`, `HeartBeat`, `InstanceStateChanged`, `InstanceExited` (with
exit code and last stderr), `LogChunk`, `DownloadProgress`, `DownloadCompleted`,
`BenchLine`, `BenchCompleted`, `SlotSnapshot`, `TelemetrySample`,
`ValidationFailed`, `ModelDiscovered`.

### Desired state vs actual state

The control plane stores **desired** state; the agent reports **actual**. A
reconciler compares them and acts — this is what makes reboots and network
partitions boring:

- Agent reconnects → sends `NodeAnnounced` with a full snapshot of running
  instances, model inventory hash, and current preset gitSha
- Control plane diffs against desired state
- Persistent instances that should be running but aren't get restarted
- Instances running that aren't in desired state are surfaced for adoption or
  stopping, never killed automatically

Commands are *not* replayed on reconnect. Reconciliation handles it, which
avoids a queue of stale "start this model" messages arriving twenty minutes late.

### Streaming

Log tails, download progress, build output and benchmark output are sequenced
streams: each chunk carries `(streamId, seq)`, and `TailLog` accepts `fromSeq`
so a browser refresh resumes rather than restarting. The agent keeps a bounded
ring buffer per stream; beyond it, the client is told to re-fetch from journald.

### Versioning and capabilities

`SchemaVersion` is a single integer on the envelope. The control plane refuses a
mismatched major version and marks the node as needing an agent upgrade rather
than failing per-command in confusing ways.

Beyond the protocol, the agent reports a **capability set** — what this node's
llama.cpp build can actually do (`spec-type` values, router mode, available
backends). Features check capabilities, not versions. A node whose build lacks
router mode should show the router option disabled with a reason, not throw at
spawn time.

### Heartbeat

Agent → control plane every 10s. Missed for 30s marks the node `Unreachable`;
existing instances are assumed still running, and nothing destructive happens on
a partition. The UI shows last-known state greyed out with the timestamp.

## Should this use Akka.NET?

Short answer: not for v1, and probably not at all — but the instinct behind the
question is sound, and there's one place it would genuinely fit.

**What actors would give you.** Per-instance and per-node state machines with
supervision, mailbox serialisation of concurrent commands against one instance,
and a natural retry/restart story. Those are real problems here: two operators
hitting Start and Stop on the same instance, an agent flapping, a download that
should resume.

**Why it's probably the wrong trade.**

- The concurrency is tiny. A handful of nodes, a few instances each, events at
  human pace. Actors solve contention you don't have.
- The hard supervision problem is *OS process* supervision, and actors don't
  help there. `Process.Exited`, a `Channel<T>`, and a hosted service already
  express "restart llama-server if it dies" directly. Wrapping that in an actor
  adds a layer without adding a guarantee.
- Akka.Remote/Cluster would duplicate the transport you already need. You're
  running SignalR for the browser regardless; using it for agents too means one
  connection model, one auth story. Adding cluster remoting means two.
- It fights VSA. Actor systems pull toward a central hierarchy with shared
  message types and a supervision tree that everything routes through — exactly
  the shared-service coupling that slices exist to avoid. You'd end up with
  slices that are thin wrappers over `IActorRef` lookups.
- Persistence. The interesting state (desired config, benchmark history) is
  relational and queried by the UI. Akka.Persistence event-sourcing would be a
  second storage paradigm alongside EF Core for no clear gain.

**What to do instead.** One `InstanceSupervisor` hosted service per agent
holding a `Dictionary<Guid, SupervisedProcess>`, each with a `Channel<Command>`
consumed by a single loop. That gives you the same serialisation-per-instance
guarantee in about fifty lines, with no framework.

**When to revisit.** If the fleet grows past a dozen nodes with real scheduling
("run this benchmark wherever there's capacity"), or if you want the control
plane itself to be HA with failover between replicas, Akka.Cluster starts paying
for itself. Design note: keeping `INodeGateway` as the single boundary means
swapping the implementation later is contained — that decision doesn't need
making now.

## Runtimes

Runtime is a first-class concept in the model from day one, even though only
llama.cpp is implemented in v1. The point is not to support vLLM now — it's that
nothing in the platform should have to change when a second runtime arrives.

### The runtime as a domain concept

A **runtime** is a way of serving models on a node. It owns: how it's
configured, what model formats it understands, how it's launched, how its
runtime stats are read, and whether it can be benchmarked.

Everything *around* that — node registry, path config, process supervision,
port allocation, desired-state reconciliation, log streaming, downloads,
telemetry, git-backed config history — belongs to the platform and is written
once.

```csharp
public enum RuntimeId { LlamaCpp = 1 /*, Vllm, Ollama, … */ }

public sealed record RuntimeDescriptor(
    RuntimeId              Id,
    string                 DisplayName,
    string?                Version,            // null when not installed
    string?                BinPath,
    bool                   Installed,
    ConfigFormat           ConfigFormat,
    IReadOnlySet<ModelFormat> ModelFormats,
    RuntimeCapabilities    Capabilities,
    IReadOnlyDictionary<string,string> FlagSchema);   // may be empty
```

Each node reports one descriptor per runtime it knows about, `Installed` false
where the binary isn't present. That's what lets the UI show "vLLM — not
installed on this node" rather than silently omitting it.

### Capabilities

Feature gating is by capability, never by runtime identity or version. Anything
that asks "is this llama.cpp?" is a bug waiting for the second runtime.

```csharp
[Flags]
public enum RuntimeCapabilities
{
    None              = 0,
    MultiModelRouting = 1 << 0,  // one process fronting several models
    OnDemandLoad      = 1 << 1,  // loads on first request
    PerModelConfig    = 1 << 2,  // presets keyed by model
    SpeculativeDecode = 1 << 3,
    Multimodal        = 1 << 4,
    SlotIntrospection = 1 << 5,  // per-request runtime state
    PrometheusMetrics = 1 << 6,
    NativeBenchmark   = 1 << 7,  // a bench tool, not just timed generation
    SelfManagedModels = 1 << 8,  // owns its own model store
    InPlaceUpgrade    = 1 << 9,  // agent can pull + rebuild it
}
```

### Instance shape, generalised

`Router` and `Single` were llama.cpp vocabulary. The platform only needs to know
whether an instance is long-lived or runs to completion; the rest is a
runtime-specific profile string the provider interprets.

```csharp
public enum InstanceKind { Managed, Ephemeral }

public sealed record InstanceSpec(
    string        Name,
    RuntimeId     Runtime,
    InstanceKind  Kind,
    string?       Profile,        // "router" | "single" for llama.cpp
    string?       ModelRef,       // path, directory, or runtime-native id
    string?       ConfigRef,      // preset document id
    int?          Port,
    IReadOnlyDictionary<string,string?> Args,
    bool          Persistent);
```

### Provider interface (agent side)

One implementation per runtime. The provider never spawns or supervises
anything itself — it *describes* what to launch, and the shared supervisor does
the rest. That keeps restart, port allocation and exit handling identical
across runtimes.

```csharp
public interface IRuntimeProvider
{
    RuntimeId Id { get; }

    Task<RuntimeDescriptor> DescribeAsync(NodePaths paths, CancellationToken ct);

    // Config
    ConfigFormat ConfigFormat { get; }
    Task<IReadOnlyList<ConfigDiagnostic>> ValidateConfigAsync(
        ConfigDocument doc, RuntimeDescriptor self, CancellationToken ct);

    // Launch — returns a description, does not start anything
    LaunchPlan BuildLaunchPlan(InstanceSpec spec, NodePaths paths,
                               ConfigDocument? config);

    // Runtime introspection of a running instance
    Task<RuntimeStats> ReadStatsAsync(RunningInstance inst, CancellationToken ct);
    Task<HealthResult> ProbeAsync(RunningInstance inst, CancellationToken ct);

    // Models
    Task<ModelInspection> InspectModelAsync(string path, CancellationToken ct);
    Task<LibraryPlan> PlanLibraryAsync(NodePaths paths, CancellationToken ct);

    // Optional, gated by capability
    Task<BenchmarkPlan?> BuildBenchmarkPlanAsync(
        string modelPath, IReadOnlyDictionary<string,string?> flags,
        CancellationToken ct);
    Task<UpgradePlan?> BuildUpgradePlanAsync(string? gitRef, CancellationToken ct);
}

public sealed record LaunchPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string,string> Environment,
    string WorkingDirectory,
    ReadinessProbe Readiness);   // how to know it's up
```

`ReadinessProbe` matters more than it looks: llama.cpp is ready when its HTTP
port answers, but a model load can take a minute for a 60 GB file, and the
process is alive but useless throughout. Probes are per-runtime.

### Config documents

```csharp
public enum ConfigFormat { LlamaCppIni, Yaml, CliArgs, Modelfile }

public sealed record ConfigDocument(
    Guid Id, RuntimeId Runtime, ConfigFormat Format,
    string Content, string GitSha);
```

The platform stores, versions and diffs content as text; the provider parses and
validates it. The editor component is chosen by `Format` — a form-based editor
for the formats with a known schema, a text editor with diagnostics for the rest.

### Normalised stats

The UI needs a common shape, but the interesting details are runtime-specific.
Provide both, and don't pretend the union is universal.

```csharp
public sealed record RuntimeStats(
    int?    LoadedModels,
    int?    ActiveRequests,
    double? TokensPerSecond,
    long?   VramUsedMiB,
    IReadOnlyList<SlotSnapshot> Slots,      // empty without SlotIntrospection
    JsonElement Raw);                        // provider's native payload
```

The alerts from the incident above (`n_predict: -1`, runaway decode, zero prompt
cache) are llama.cpp-shaped and live in the llama.cpp provider as rules over its
own slot data, not in the platform.

### Model formats

```csharp
public enum ModelFormat { Gguf, Safetensors, RuntimeNative }
```

`PlanLibraryAsync` is where format-specific layout rules live — the GGUF shard
and `mmproj` subdirectory rules, the draft-head exclusion, the symlink
reconciliation. A runtime with `SelfManagedModels` returns an empty plan and the
library view falls back to read-only.

### Registration

Providers are registered in DI keyed by `RuntimeId`; the agent resolves the set,
calls `DescribeAsync` on each at startup and after any upgrade, and reports the
descriptors upward. A provider whose binary is absent still reports itself, with
`Installed = false` — that's how the UI can offer "install vLLM on this node"
later without the control plane hardcoding a list.

### v1 scope

Ship `LlamaCppProvider` and nothing else. The enum has one member, DI has one
registration, and there is no base class, no `RuntimeProviderBase`, no
abstraction extracted from a single implementation.

The interface above is the contract, and the discipline is negative: no slice
may branch on `RuntimeId`, no DTO may carry an INI string, no query may assume
GGUF. Those three rules are what make the second provider an addition rather
than a refactor — and they cost nothing to follow today.

Worth sketching the vLLM provider on paper before finalising the interface,
purely to check the shape holds. Don't ship it.


### What each runtime needs beyond the shared core

| Concern | llama.cpp | vLLM | Ollama |
|---|---|---|---|
| Config | `models.ini` presets | CLI args / YAML | Modelfile |
| Flag discovery | parse `--help` | parse `--help` | fixed API |
| Model store | HF cache + flat dir | HF cache, no flat dir | own blob store |
| Runtime stats | `/slots?model=` | `/metrics` (Prometheus) | `/api/ps` |
| Lifecycle | agent-supervised | agent-supervised | self-managed |
| Benchmarks | `llama-bench` | no equivalent — generation timing only | generation timing only |

### Verdict per target

**vLLM is a good fit.** A bare process with no model management of its own,
which is the gap this tool fills. Downloads, fit estimation, supervision and
benchmark history all transfer. The differences are config format and stats
scraping — both contained.

**Ollama is a poor fit.** It already manages models, lifecycle, keep-alive and
unloading, with its own store and API. Wrapping it means a manager inside a
manager, and most of the value disappears. If it's ever wanted, the honest
implementation is a read-only view over `/api/tags` and `/api/ps` plus
start/stop of the daemon — not a peer of the llama.cpp provider.

Concretely: build llama.cpp only, but let `Runtime` exist as an enum with one
member and keep preset content opaque. Don't build a provider abstraction with
a single implementation beyond naming the seam.

## Data model (SQLite)

```
Node            id, hostname, endpoint, lastSeen, llamaBuild, rocmVersion,
                gpuName, vramTotal, flagSchemaJson
RuntimeInstall  id, nodeId, runtimeId, installed, version, binPath,
                capabilities, configFormat, flagSchemaJson, describedAt
Instance        id, nodeId, name, runtimeId, kind, profile, specJson,
                configDocId, persistent, state, port
ConfigDocument  id, nodeId, runtimeId, format, content, gitSha, authoredAt
ModelFile       id, nodeId, repo, quant, format, path, sizeBytes, arch,
                nCtxTrain, hasDraftHead, isShardSet, hasMmproj
BenchRun        id, nodeId, modelFileId, runtimeId, runtimeVersion, flagsJson,
                startedAt
BenchResult     id, benchRunId, testType (pp512/tg128/generation), tokensPerSec,
                stddev, measurementKind (bench|generation)
Alert           id, nodeId, instanceId, kind, firstSeen, acknowledgedAt
```

## Phasing

**Phase 1 — replace SSH for daily ops.** Agent + node registry, instance
lifecycle (all three modes), log streaming, preset editor with per-node flag
validation. At the end of this you stop editing `models.ini` over SSH.

**Phase 2 — model management.** Library view, flat-dir reconciliation, HF
browse and download, fit calculator, GGUF inspection.

**Phase 3 — measurement.** Benchmark runner, history, charts, markdown export.

**Phase 4 — observability.** Slots, alerts, telemetry, health panel.

**Phase 5 — hardening.** Git-backed config history, API key management, VRAM
budget enforcement across instances, multi-node scheduling ("run this benchmark
wherever there's capacity").

## Risks

- **Upstream churn.** llama.cpp moves fast: flags get renamed, the router is
  new, the default port is changing to 9931. Parsing `--help` per node rather
  than hardcoding is the main defence, but output parsing (`llama-bench` tables,
  log lines) will break periodically. Keep parsers isolated and tested against
  captured fixtures.
- **Router restart drops loaded models.** Any preset change costs a reload of a
  60 GB model. The UI must make that cost visible; batching edits before
  applying is worth designing for.
- **Agent privilege.** It spawns processes and writes systemd units as root
  inside the container. Compromise of the control plane means compromise of
  every node.
- **Scope.** Phases 3–5 are where this becomes a project rather than a tool.
  Phase 1 alone is genuinely useful; ship it before deciding on the rest.

## Open questions

1. Does llama.cpp expose per-slot cancellation? Determines whether the slot
   monitor can kill a runaway or only restart the instance.
2. Can the router rescan without a full restart? If a future version can, the
   preset workflow gets much better.
3. Where does the control plane run — its own container, or alongside an agent
   on one node?
4. Is `llama-swap` worth evaluating first? It solves a slice of this (model
   lifecycle and routing) and might reduce what needs building.

## Resources

Everything referenced while building the setup this spec automates.

### Documentation

| What | Where |
|---|---|
| Router mode, presets, model sources | `/opt/llama.cpp/tools/server/README.md` — the only complete reference for the INI schema and the multi-shard/multimodal directory rules |
| Flag schema (per build) | `llama-server --help` — parse this rather than trusting docs |
| llama.cpp troubleshooting (Qwen-centric) | https://netclaw.dev/troubleshooting/llama-cpp/ |
| Nemotron 3.5 usage notes | https://unsloth.ai/docs/models/nemotron-3.5 |
| Strix Halo container images (not used — podman declined) | https://github.com/kyuz0/amd-strix-halo-toolboxes |

### ROCm on gfx1151

- AMD installer deb: `https://repo.radeon.com/amdgpu-install/7.2.1/ubuntu/noble/amdgpu-install_7.2.1.70201-1_all.deb`
- Install line: `amdgpu-install -y --usecase=rocm --no-dkms` — `--no-dkms` is
  required in an LXC; the host owns the kernel module
- The noble build works on newer Ubuntu; `rocm-gdb` and `rocprofiler-systems`
  may fail to unpack and neither matters for inference

### Model repositories in use

| Model | Repo |
|---|---|
| Nemotron 3.5 Lightning | `ggml-org/NVIDIA-Nemotron-3.5-Lightning-30B-A3B-GGUF` |
| gpt-oss-120b | `unsloth/gpt-oss-120b-GGUF` (UD-Q6_K_XL) |
| Muse Glimmer 30B | `unsloth/Muse-Glimmer-30B-GGUF` (+ `mmproj`, `dflash-kquant.gguf`) |
| Qwen3.8-27B | `unsloth/Qwen3.8-27B-GGUF` (UD-Q4_K_XL) |
| Qwen3.8-27B MTP head | `a4lg/Qwen3.8-27B-MTP-ONLY-GGUF` |
| Qwen3.8-Flash-Next | `unsloth/Qwen3.8-Flash-Next-GGUF` |
| Qwen3-14B | `unsloth/Qwen3-14B-GGUF` |

### Upstream changes to track

- Default server port moving to 9931 — llama.cpp PR #26508
- CORS/API-key warning rationale — llama.cpp PR #25655

### Reference build

The environment this spec targets, as configured:

```
Host      Proxmox VE 9.2.10, kernel 7.0.14-11-pve, in-tree amdgpu (no DKMS)
Guest     unprivileged LXC, Ubuntu 24.04.4, 16 cores / 24 GB / 64 GB root
GPU       Radeon 8060S (gfx1151), 96 GB BIOS carve-out of 128 GB unified
Devices   dev0 /dev/dri/renderD128 gid 993
          dev1 /dev/dri/card0      gid 44
          dev2 /dev/kfd            gid 993
ROCm      7.2.1 at /opt/rocm-7.2.1
Build     cmake -B build -G Ninja -DGGML_HIP=ON -DAMDGPU_TARGETS=gfx1151 \
                -DCMAKE_BUILD_TYPE=Release -DLLAMA_OPENSSL=ON
```

`-DLLAMA_OPENSSL=ON` is not optional — without it, `-hf` downloads fail with
"HTTPS is not supported".

Benchmark numbers for this build are in `strix-halo-llama-benchmarks.md`.
