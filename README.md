# localai-ctl-plane

`llamactl` is a control plane for operating a fleet of nodes that run local
llama.cpp workloads. The control plane is a .NET 10 Blazor Server application;
each node will run an outbound-connected agent that owns its local processes,
files, downloads, and telemetry.

This repository is under active development. Phase 1 daily operations and
Phase 2 model management are operational. Measurement and benchmarks are next.

## Specifications

- [System specification](<llamactl-spec(1).md>)
- [Web control-plane specification](llamactl-web-spec.md)

## Completed

### Foundation

- .NET 10 solution with separate Web, Agent, Contracts, and Tests projects
- Blazor with global Interactive Server rendering and prerendering disabled
- MudBlazor operator shell with responsive desktop and mobile layouts
- Immediate.Handlers, Immediate.Apis, and Immediate.Validations integration
- Repository-local `dotnet-ef` and Slopwatch tools

### Shared contracts

- Versioned control-plane/agent message envelope
- Node discovery, path configuration, validation, and health contracts
- Runtime descriptors, capability flags, and model/config formats
- Runtime-neutral managed and ephemeral instance specification

### Node registry

- SQLite persistence through `IDbContextFactory<LlamactlDb>`
- Initial EF Core migration, applied automatically at startup
- SQLite WAL mode
- Node list query and generated `GET /api/v1/nodes` endpoint
- Node onboarding command and generated `POST /api/v1/nodes` endpoint
- Duplicate-name conflicts returned as typed domain results
- Bootstrap tokens hashed before storage
- Node list and onboarding UI with pending, healthy, degraded, and unreachable
  display states
- Validated agent bootstrap configuration and resilient outbound SignalR client
- Bootstrap-token authenticated agent hub with connection-to-node mapping
- Versioned node announcements with OS, kernel, mounted-filesystem, ROCm GPU,
  llama.cpp runtime, flag-schema, capability, and path-proposal discovery
- Full announcement payload persistence with node-list hardware summaries
- Periodic authenticated heartbeats with healthy, degraded, and unreachable
  transitions
- Node configuration UI for explicit paths, VRAM budget, defaults, and port range
- Agent-side path, write-access, binary, ROCm, and existing-setup validation
- Persisted validation results shown to operators
- llama.cpp process supervisor for router and single-model profiles
- Automatic configured-port allocation and collision prevention
- Instance create, edit, adopt-by-PID, start, stop, restart, and delete workflows
- Durable desired and observed instance state with process error reporting
- Pull-based desired-state reconciliation after changes, reconnects, crashes,
  and agent restarts, including revisioned PID-file recovery
- Sequenced stdout/stderr streaming with bounded agent and control-plane buffers
- Responsive instance log tail with stream filtering and search
- Agent-owned atomic preset reads/writes with INI parsing and per-node flag validation
- Preset diff preview and explicit router-restart warning
- Operator cookie authentication and separate `X-Api-Key` API authentication
- RFC ProblemDetails exception mapping and guarded Blazor handler invocation
- Liveness endpoint at `GET /health/live`
- Database readiness endpoint at `GET /health/ready`

### Model management

- Node-owned GGUF inventory across separate models and Hugging Face volumes
- HF snapshot, blob, disk-usage, free-space, and orphaned-blob discovery
- Dry-run and applied flat-library reconciliation for single-file, sharded,
  multimodal, and excluded draft-head models
- Hugging Face GGUF repository search and file browsing
- Complete-variant shard selection with disk and VRAM fit verdicts
- Agent-owned background downloads with bounded progress and cancellation
- HF-compatible blob/snapshot layout, partial-file cleanup, and automatic
  post-download library reconciliation
- GGUF architecture, training-context, tensor-count, and MTP/nextn inspection
- Model, linked blob, flat-link, and orphaned-blob deletion
- Responsive model library, fit, inspection, and download workspace

### Verification

- Shared-contract JSON round-trip tests
- Port-range boundary tests
- SQLite-backed onboarding, duplicate-name, and token-hashing test
- Desktop and mobile browser verification with no horizontal overflow
- Forty-three automated tests currently passing
- Slopwatch strict scan currently reports no issues

## Still Missing

### Phase 3: Measurement

- Ephemeral benchmark execution
- `llama-bench` output parsing and retained results
- Comparison, regression, charting, and Markdown export
- Separate drafted-generation measurements

### Phase 4: Observability

- Slot polling and runaway-request alerts
- ROCm GPU telemetry and short-term retention
- Per-model warning extraction and health panels
- Server-side real-time bus with bounded, coalesced topics

### Phase 5: Hardening

- Git-backed configuration history and preset reverts
- API-key exposure management for llama.cpp instances
- Full VRAM-budget enforcement across concurrent instances
- Multi-node scheduling
- Structured logging, OpenTelemetry, metrics, and production authentication
- Container, systemd, backup, and deployment assets

## Project Layout

```text
llamactl.Web/        Blazor control plane, feature slices, and persistence
llamactl.Agent/      Node-side processes, model files, downloads, and discovery
llamactl.Contracts/  Shared wire protocol and runtime-neutral contracts
llamactl.Tests/      Contract and handler tests
```

## Local Development

Requires the .NET 10 SDK.

```powershell
dotnet tool restore
dotnet restore llamactl.slnx
dotnet test llamactl.slnx
dotnet run --project llamactl.Web
```

Development uses `local-operator-password` for browser sign-in and
`local-development-api-key` for `X-Api-Key`. Override both outside development:

```powershell
$env:Llamactl__Security__OperatorPassword = "<operator-password>"
$env:Llamactl__Security__ApiKey = "<api-key>"
```

Non-development startup fails when either secret is absent or too short.

After onboarding a node, configure its agent with the returned node ID and the
same bootstrap token. In Windows PowerShell:

```powershell
$env:Llamactl__ControlPlaneUrl = "http://127.0.0.1:5187"
$env:Llamactl__NodeId = "<onboarded-node-id>"
$env:Llamactl__BootstrapToken = "<bootstrap-token>"
dotnet run --project llamactl.Agent
```

Keep the bootstrap token outside committed configuration.

The default database path is `llamactl.Web/data/llamactl.db`. The directory is
created automatically and excluded from source control.

Run the strict code-quality scan with:

```powershell
dotnet tool run slopwatch analyze --no-baseline --fail-on warning
```

Update this status as vertical slices become operational; keep detailed design
decisions in the specification documents.
