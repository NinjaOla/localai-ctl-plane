# localai-ctl-plane

`llamactl` is a control plane for operating a fleet of nodes that run local
llama.cpp workloads. The control plane is a .NET 10 Blazor Server application;
each node will run an outbound-connected agent that owns its local processes,
files, downloads, and telemetry.

This repository is under active development. The node registry is the first
implemented vertical slice; the agent and instance-management workflows are
not operational yet.

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
- Liveness endpoint at `GET /health/live`

### Verification

- Shared-contract JSON round-trip tests
- Port-range boundary tests
- SQLite-backed onboarding, duplicate-name, and token-hashing test
- Desktop and mobile browser verification with no horizontal overflow
- Six automated tests currently passing
- Slopwatch strict scan currently reports no issues

## Still Missing

### Phase 1: Daily operations

- Agent bootstrap configuration and outbound SignalR client
- Authenticated agent hub and connection-to-node identity mapping
- Node announcement, hardware discovery, path proposals, and capabilities
- Heartbeats, last-seen updates, and unreachable/degraded transitions
- Node configuration UI for paths, VRAM budget, defaults, and port range
- Agent-side configuration validation and adoption of existing installations
- Agent process supervisor and runtime provider for llama.cpp
- Instance list, start, stop, restart, edit, and adoption workflows
- Desired-state reconciliation after reconnects and restarts
- Sequenced log streaming with bounded buffers and browser tail view
- Preset read, validation, editing, diff preview, and router-restart warning
- Operator cookie authentication and separate API-key authentication
- ProblemDetails mapping and guarded Blazor handler invocation
- Readiness health check that includes database availability

### Phase 2: Model management

- Model inventory and library UI
- HF cache and flat-directory reconciliation
- Hugging Face search, download, progress, and cancellation
- Disk/VRAM fit estimation
- GGUF metadata and draft-head inspection
- Model and orphaned-blob deletion

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
llamactl.Agent/      Node-side host; currently only scaffolded
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

The default database path is `llamactl.Web/data/llamactl.db`. The directory is
created automatically and excluded from source control.

Run the strict code-quality scan with:

```powershell
dotnet tool run slopwatch analyze --no-baseline --fail-on warning
```

Update this status as vertical slices become operational; keep detailed design
decisions in the specification documents.
