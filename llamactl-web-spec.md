# llamactl.Web — control plane project spec

Companion to `llamactl-spec.md`. That document covers the system; this one
covers the ASP.NET Core / Blazor project only.

## Scope

`llamactl.Web` is the single control plane. It:

- serves the operator UI
- hosts the agent hub (nodes connect *in*, outbound from their side)
- owns the database
- runs long-lived background work (reconciliation, alert evaluation)

It never touches a node's filesystem or processes directly. Everything goes
through `INodeGateway`.

## Render model

**Blazor with `InteractiveServer` globally.** Not static SSR, not WebAssembly,
not Auto.

```csharp
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();
```

Rationale: the UI is a live view of remote state that changes without user
action — instance states, log lines, download progress, slot snapshots. A
persistent circuit gives push for free. WebAssembly would mean building a
public HTTP API purely to feed it, plus a second auth story, for an app with
one concurrent user most of the time.

### Prerendering: off

```razor
@rendermode @(new InteractiveServerRenderMode(prerender: false))
```

Set globally on `<Routes>`. Prerender would run every page's initial load twice
— once statically, once on circuit start — which for pages that query nodes
means duplicate agent round-trips and log-subscription churn. The cost is a
brief loading state on first paint, which is the right trade for an admin tool
on a LAN.

Where a page is genuinely static (about, docs), prerender can be re-enabled
per-component.

### Circuit assumptions

- A circuit is per browser tab. Two tabs is two circuits with independent state.
- Circuits die on disconnect after the retention window. **Nothing durable may
  live in circuit state.** A user closing a laptop must not cancel a download.
- Reconnect shows the framework's reconnect UI, restyled. On reconnect failure
  the page reloads and rehydrates from the database — components are written to
  survive this, holding no unsaved state beyond a form in progress.

```csharp
builder.Services.Configure<CircuitOptions>(o => {
    o.DetailedErrors = builder.Environment.IsDevelopment();
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
});
```

## Project layout

```
llamactl.Web/
├── Features/                        one folder per slice
│   ├── Nodes/
│   │   ├── List/
│   │   │   ├── NodeList.razor               @page "/nodes"
│   │   │   ├── ListNodesQuery.cs            contract + handler
│   │   │   └── NodeSummary.cs               view model, owned here
│   │   ├── Configure/
│   │   │   ├── ConfigureNode.razor          @page "/nodes/{id:guid}/config"
│   │   │   ├── ConfigureNodeCommand.cs
│   │   │   ├── ConfigureNodeValidator.cs
│   │   │   └── PathsEditor.razor            slice-local component
│   │   └── Onboard/
│   ├── Instances/{List,Detail,Start,Stop,Adopt,Logs}/
│   ├── Presets/{Edit,History,Revert}/
│   ├── Models/{Library,Reconcile,Inspect}/
│   ├── Downloads/{Search,Fit,Start,Progress}/
│   ├── Benchmarks/{Run,Compare,Export}/
│   └── Observability/{Slots,Telemetry,Alerts}/
├── Platform/
│   ├── Components/                  shell, layout, primitives — dumb only
│   ├── NodeGateway/                 agent hub + INodeGateway impl
│   ├── Realtime/                    in-process bus, circuit subscriptions
│   ├── Persistence/                 DbContext, configurations, migrations
│   ├── Behaviors/                   logging, telemetry, exception mapping
│   ├── Results/                     Result<T>, error kinds, UI mapping
│   ├── Auth/
│   ├── Api/                         route groups, TransformResult, OpenAPI
│   └── Jobs/                        background services
├── wwwroot/
├── App.razor  Routes.razor  _Imports.razor
└── Program.cs
```

**Rules.** A slice may reference `Platform`, and may call another slice directly
by injecting its generated `Handler` — `OtherSlice.Handler` — and calling
`HandleAsync`. Nothing gets wrapped in an interface just to avoid the reference.

The limits are practical, not architectural purity: call the other slice's
contract rather than its internals, and keep the call graph acyclic. If two
slices need each other, they're one slice or the shared part is `Platform`.

Shared components in `Platform/Components` take parameters and raise events —
they never inject `ISender` or a DbContext.

Routes live with their component, so the URL map is discoverable by looking at
the folder tree rather than a central route table.

## Handlers, endpoints and validation — ImmediatePlatform

The stack is **Immediate.Handlers + Immediate.Apis + Immediate.Validations**.
All three are Roslyn source generators: the pipeline is built at compile time,
there is no assembly scanning, no reflection on the request path, and no
service-locator lookup. Mistakes surface as build errors with specific
diagnostics rather than at runtime.

This replaces what would otherwise be three dependencies — a mediator,
FluentValidation, and hand-written minimal-API endpoint registration — and the
combination is the documented Blazor Server cookbook stack.

### There is no `ISender`

This is the shape change to internalise. A handler is a `[Handler]`-marked
partial class with nested `Query`/`Command` and `Response` records and exactly
one private `HandleAsync`. The generator emits a nested `Handler` class, and
**consumers inject that concrete type**:

```csharp
// Features/Instances/Start/StartInstance.cs
[Handler]
[DefaultBehaviors]
public sealed partial class StartInstance(
    IDbContextFactory<LlamactlDb> dbFactory,
    INodeGateway nodes)
{
    [Validate]
    public sealed partial record Command : IValidationTarget<Command>
    {
        public required Guid NodeId { get; init; }
        [NotEmpty] public required string Name { get; init; }
        public required InstanceSpec Spec { get; init; }
    }

    public sealed record Response(Guid InstanceId, int Port);

    private async ValueTask<Result<Response>> HandleAsync(
        Command command, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        …
    }
}
```

```razor
@inject StartInstance.Handler StartInstance

@code {
    async Task Start() {
        var result = await StartInstance.HandleAsync(
            new StartInstance.Command { NodeId = _nodeId, Name = _name, Spec = _spec },
            CancellationToken.None);
    }
}
```

Two consequences worth stating plainly:

- **A component's dependencies are visible in its injects.** No opaque `ISender`
  that could reach anything. What a page can invoke is declared at the top.
- **Slice-to-slice calls are just an injection** of the other slice's
  `Handler`. That is exactly the direct-call rule from the system spec, with no
  extra machinery — and the compiler enforces the contract boundary, since
  internals aren't on the generated handler.

### Registration

```csharp
builder.Services.AddLlamactlWebHandlers();   // Immediate.Handlers
…
app.MapLlamactlWebEndpoints();               // Immediate.Apis
```

Both method names derive from the assembly identifier, so they follow the
project name rather than being configured. Behaviour dependencies are registered
automatically, closed over each handler's request and response types — only the
behaviours that actually attach.

### Behaviours

Cross-cutting concerns derive from `Behavior<TRequest, TResponse>` and call
`Next`. The base class exposes `HandlerType`, which the generated handler sets
to the concrete handler type — useful for log scopes and telemetry tags without
threading a name through every request.

Planned behaviours:

| Behaviour | Purpose |
|---|---|
| `LoggingBehavior<,>` | log scope + duration per handler, tagged with `HandlerType` |
| `TelemetryBehavior<,>` | OpenTelemetry activity per command |
| `ValidationBehavior<,>` | supplied by Immediate.Validations — **throws** on failure |

Applied through a bundle attribute rather than repeated per handler:

```csharp
[Behaviors(
    typeof(LoggingBehavior<,>),
    typeof(TelemetryBehavior<,>),
    typeof(ValidationBehavior<,>)
)]
public sealed class DefaultBehaviorsAttribute : Attribute;
```

**First listed is outermost** — logging enters first and exits last.

Three generator rules that will otherwise cost an afternoon:

- A `[Behaviors]` attribute on a handler **replaces** the assembly-wide list
  rather than appending to it. That's precisely why the bundle attribute exists
  here instead of `[assembly: Behaviors(...)]` — one convention, no silent
  divergence when a handler needs one extra behaviour.
- Generic behaviours must be referenced **unbound**: `typeof(LoggingBehavior<,>)`,
  never closed. Closed generics report `IHR0008`.
- **Nullability participates in constraint matching.** A behaviour fixed to
  `Result<T>` will not attach to a handler returning `Result<T>?`. This is the
  documented common cause of "why isn't my behaviour running".

Behaviour constraints are also the mechanism for selective attachment — a
behaviour constrained `where TRequest : INodeScoped` attaches only to handlers
whose command carries a node id, which is how node-health preconditions get
applied without repeating them in every handler.

### Validation

Rules live on the request type as attributes, not in a separate validator class:

```csharp
[Validate]
public sealed partial record Command : IValidationTarget<Command>
{
    [NotEmpty] public required string Name { get; init; }
    [GreaterThan(0)] public required int Port { get; init; }
}
```

The type must be `partial` — the generator writes the other half. Nested objects
and collections are recursed into automatically, which matters for
`InstanceSpec` and `NodePaths`, both of which are nested and both of which need
validating.

Anything attributes can't express goes in an `AdditionalValidations` method on
the type itself. That's where the interesting rules live here, since most of
this domain's validation is contextual rather than shape-based:

- a preset section name must match a model the node actually reports
- an instance's `Profile` must be supported by that runtime's capabilities
- a requested port must fall inside the node's configured range

Note that several of those need node state, so they can't be pure attribute
checks. Where a rule needs a database or agent round-trip it belongs in the
handler returning `Result.Validation`, not in `AdditionalValidations` — keep
`Validate` synchronous and self-contained.

### Validation failures throw

`ValidationBehavior` throws rather than short-circuiting into a `Result`. That's
settled, and it means one deliberate split:

- **`Result<T>`** carries *expected domain outcomes* — node degraded, VRAM
  budget exceeded, preset revision stale, node unreachable. These are answers,
  not faults.
- **Exceptions** carry *malformed input and genuine faults*. A validation
  failure means the caller sent something the contract forbids.

No `ExceptionToResultBehavior`. Converting the throw back into a `Result` inside
the pipeline would give two competing mechanisms for the same thing and make the
API's status mapping ambiguous. It throws; each surface catches it once.

The catch differs by surface, because **ASP.NET Core's exception middleware does
not run for a Blazor circuit call** — the component invokes the handler
in-process, with no HTTP request in flight. This is the trap worth naming
explicitly.

#### HTTP surface — `IExceptionHandler`

```csharp
internal sealed class ValidationExceptionHandler(IProblemDetailsService problems)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx, Exception ex, CancellationToken ct)
    {
        if (ex is not ValidationException vex) return false;

        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problems.TryWriteAsync(new ProblemDetailsContext {
            HttpContext = ctx,
            ProblemDetails = new ValidationProblemDetails(vex.ToDictionary()) {
                Title  = "Validation failed",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            }});
    }
}
```

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();  // 500, logged, no detail leaked
app.UseExceptionHandler();
```

Handlers are tried in registration order, each returning `false` to pass the
exception along. Errors come back as RFC 9457 `ValidationProblemDetails` keyed
by property name, which is what an OpenAPI client expects.

#### Blazor surface — explicit wrapper

Forms call the generated `Command.Validate(...)` *before* invoking the handler,
so ordinary bad input renders inline and never reaches the pipeline. The throw
is then a backstop for the cases a form can't catch — a stale value, a
programmatic call, a race against changed node state.

A single helper in `Platform/Results` does the catching, so no component writes
a `try`:

```csharp
public static class Invoke
{
    public static async Task<Result<T>> Guarded<T>(
        Func<CancellationToken, ValueTask<Result<T>>> call,
        ILogger logger, CancellationToken ct = default)
    {
        try                            { return await call(ct); }
        catch (ValidationException ex) { return Result.Validation(ex.ToFieldErrors()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) {
            logger.LogError(ex, "Unhandled failure in handler call");
            return Result.Failure("Something went wrong. Check the logs.");
        }
    }
}
```

```razor
var result = await Invoke.Guarded(
    t => StartInstance.HandleAsync(command, t), Logger);
```

`OperationCanceledException` is deliberately rethrown — a cancelled render or a
disposed circuit is not an error to display.

`<ErrorBoundary>` around the page body remains the last resort for anything
thrown during render rather than during a handler call.

## DI lifetimes

Blazor Server's "scoped" is **per circuit**, not per request. This is the single
most common source of bugs in this style of app.

| Service | Lifetime | Note |
|---|---|---|
| `IDbContextFactory<LlamactlDb>` | singleton | **use this, not a scoped DbContext** |
| Generated `*.Handler` types | per `AddLlamactlWebHandlers()` | injected directly into components and other handlers |
| Behaviours | closed per handler, registered automatically | only those that actually attach |
| `INodeGateway` | singleton | wraps the hub, shared by everything |
| `IRealtimeBus` | singleton | in-process pub/sub |
| `CircuitSubscriptions` | scoped | per-circuit subscription bag, disposable |
| `CurrentNodeContext` | scoped | which node the operator is looking at |

A scoped `DbContext` in Blazor Server lives as long as the tab and is not
thread-safe against concurrent renders. `AddDbContextFactory` plus
`await using var db = await factory.CreateDbContextAsync()` per operation is
the rule, with no exceptions.

## Real-time

### The circuit is the transport

The system spec's "SignalR → browser" table describes the *events*, not a second
connection. With `InteractiveServer` the circuit already is a live SignalR
connection, so the browser must **not** open a `HubConnection` back to the same
app. Components subscribe to an in-process bus and re-render.

```csharp
public interface IRealtimeBus
{
    IDisposable Subscribe<T>(string topic, Func<T, ValueTask> handler);
    ValueTask Publish<T>(string topic, T message);
}
```

Topics mirror the system spec's groups: `node:{id}`, `logs:{instanceId}`,
`download:{id}`, `slots:{instanceId}`, `telemetry:{nodeId}`, `bench:{runId}`.

Producers are the agent hub and background jobs. Consumers are components.

### Component pattern

```csharp
protected override void OnInitialized()
{
    _sub = Bus.Subscribe<LogChunk>($"logs:{InstanceId}", async chunk => {
        _buffer.Append(chunk);
        await InvokeAsync(StateHasChanged);
    });
}

public void Dispose() => _sub?.Dispose();
```

Two non-negotiables: every `Subscribe` is disposed, and every handler marshals
through `InvokeAsync` because bus callbacks arrive on background threads.

To make leaks structural rather than a matter of discipline, subscriptions are
taken from the scoped `CircuitSubscriptions` bag, which a `CircuitHandler`
disposes on circuit close — covering the case where a tab dies without
`Dispose` running cleanly.

### Backpressure

A tailing log at a few thousand lines a second will render a browser to death.

- Bus channels are bounded with drop-oldest, per topic
- Log and telemetry topics coalesce on a timer (~200ms for logs, ~1s for
  progress) rather than publishing per line
- The log component keeps a bounded ring (say 2,000 lines) and uses
  `<Virtualize>`
- Slot and telemetry snapshots are polled by a background job, not per circuit —
  ten open tabs must not mean ten times the agent traffic

That last point is why the bus exists at all: fan-out happens server-side, once.

## Agent hub

`Platform/NodeGateway` hosts the hub agents connect to, and is the only place
that speaks the wire protocol.

```csharp
app.MapHub<AgentHub>("/hubs/agent");
```

- Authentication by per-node bearer token, validated in `OnConnectedAsync`;
  tokens stored hashed
- Connection → node identity map held by the gateway
- Inbound events are translated to bus messages and/or persisted; the hub itself
  contains no business logic
- `INodeGateway.SendAsync` correlates request/response over the connection with
  a timeout, surfacing `NodeUnreachable` as a `Result`, never an exception
- The agent hub and the Blazor circuit are separate hubs on separate paths, with
  separate auth. They meet only at the bus.

## Long-running operations

Downloads, benchmarks and rebuilds outlive any circuit and must not be tied to
one.

- Started by a command that persists a job row and returns its id immediately
- Progress arrives as agent events → bus → whichever circuits care
- Cancellation is an explicit command, never a `CancellationToken` from a
  component
- On startup, a hosted service reconciles jobs left `Running` in the database
  against what agents report, marking orphans `Unknown` rather than silently
  failed

## Background services

| Service | Interval | Job |
|---|---|---|
| `ReconcileNodeState` | on node connect + 60s | desired vs actual instance state |
| `PollSlots` | 2s per loaded instance | pull `/slots`, publish to bus |
| `PollTelemetry` | 2s per healthy node | `rocm-smi`, publish + retain briefly |
| `EvaluateAlerts` | on slot/telemetry sample | rules → raise/clear alerts |
| `ReapOrphanedJobs` | startup + 5m | jobs left `Running` after a restart |

Implemented with **Immediate.Jobs** rather than hand-written `BackgroundService`
classes. It's a reflection-free scheduler built on Immediate.Handlers,
generating typed schedulers, payload metadata and DI registrations at compile
time, with cron and interval scheduling and a real-time dashboard.

Three reasons it fits here rather than being an extra dependency for its own
sake:

- **A job is a handler.** The same logic is reachable from the UI, the API and
  the schedule with one implementation — no duplicate "service plus handler"
  pair, and jobs get the same behaviour pipeline for logging and telemetry.
- **The dashboard answers a real question.** When a node's telemetry looks
  stale, "did the poll run and fail, or not run at all?" is exactly what you
  want to see without reading logs.
- **Consistency.** Same idiom, same registration style, same compile-time
  guarantees as everything else in the project.

Scheduling rules that hold regardless of library:

- Every job skips nodes marked `Unreachable` rather than hammering them
- Per-node work runs with a bounded degree of parallelism; one slow node must
  not delay the fleet
- Polling jobs are skipped, not queued, if the previous run is still going —
  a 2s poll against a node taking 5s must not build a backlog
- Jobs are idempotent, since a restart mid-run will re-run them

**To confirm from the manual:** the exact scheduling attributes, and whether the
job store is in-process or needs a table in SQLite. If it wants its own storage
model that conflicts with EF Core migrations, the five jobs above are simple
enough to hand-write instead.

## Error handling

Handlers return `Result<T>`. Components map it, and the mapping is one shared
helper so every slice fails the same way.

```csharp
var result = await StartInstance.HandleAsync(
    new StartInstance.Command { NodeId = nodeId, Name = name, Spec = spec }, token);

if (result.IsSuccess) Nav.NavigateTo($"/instances/{result.Value.InstanceId}");
else _error = result.ToDisplay();     // typed reason → message + severity
```

- `AgentError` renders the node's own stderr verbatim in a `<pre>`, because
  llama.cpp's message is nearly always the actual answer
- `NodeUnreachable` renders as a banner with last-seen time, not a red toast
- An `<ErrorBoundary>` wraps the page body so an unhandled render exception
  doesn't tear down the circuit
- Unhandled exceptions in bus handlers are caught and logged at the subscription
  wrapper; one bad component must not kill the bus

## Shell and navigation

- Left rail: nodes with health dots; selecting one sets `CurrentNodeContext`
- Node-scoped pages (`/nodes/{id}/…`) read that context; fleet-wide pages
  (benchmark comparison, alerts) ignore it
- Top bar: connection state for the circuit itself, plus a fleet health summary
- Breadcrumbs derived from the route, since the hierarchy is genuinely nested:
  node → instance → logs

**Component library: MudBlazor.** Decided. Custom CSS confined to the shell; no
second component library, and no hand-rolled equivalents of things MudBlazor
already provides.

The components carrying real weight here are `MudDataGrid` (model library,
benchmark comparison, node list — all wanting sort, filter and virtualisation),
`MudDialog` for destructive confirmations, `MudSnackbar` for command outcomes,
and `MudChart` for the benchmark charts, which avoids pulling in a charting
library separately.

## Forms

- `EditForm` bound to the handler's `Command` record, validated by the same
  generated `Command.Validate(...)` the pipeline runs. One set of attributes,
  one result — client and server cannot disagree, because it is literally the
  same generated method
- The preset editor is the special case: `MudTextField` multiline with
  diagnostics from `ValidatePresetQuery` listed beneath, debounced ~500ms,
  validated against *that node's* flag schema. Save is blocked while errors
  exist, with a diff shown before write — rendered server-side with DiffPlex,
  no JavaScript editor

  Monaco (the VS Code editor, via BlazorMonaco) was considered and deferred. It
  would give inline error markers and a native diff view, at the cost of a
  multi-megabyte JS dependency whose state lives in the browser behind interop.
  Revisit only if hand-editing INI becomes routine; it's one component's worth
  of change
- Destructive actions (delete model, stop instance holding 60 GB, revert
  preset) require typed confirmation naming the target

## HTTP API

Ships in v1. The UI does not use it — Blazor calls handlers in-process — but it
exists so the fleet is scriptable, and so anything the UI can do is reachable
from a terminal.

### Shape

There is no endpoint file. Immediate.Apis generates the `app.MapPost(...)` call
from an attribute on the handler itself:

```csharp
[Handler]
[DefaultBehaviors]
[MapPost("/api/v1/nodes/{nodeId:guid}/instances")]
[Authorize(Policy = Policies.ApiKey)]
public sealed partial class StartInstance(…)
{
    …
}
```

The handler is the endpoint. Request binding, the authorization convention and
assembly-wide registration are all emitted at compile time — no reflection, no
runtime endpoint scanning, no central route table.

`Platform/Api` holds only what the generator hands off to: route groups for the
shared `/api/v1` prefix and its conventions, `TransformResult` for mapping
`Result<T>` to an `IResult`, the ProblemDetails shape, and OpenAPI setup.

Two constraints from the generator worth knowing up front:

- Every endpoint must also be a `[Handler]`, or nothing is generated and
  `IAPI0001` fails the build. That is the right direction of travel here anyway.
- **Authorization is policy-only** — `[Authorize(Policy = …)]`, not roles. Fine
  for this app, which has API keys and one operator, but it forecloses
  role-based rules without a policy wrapper.

### Conventions

- Versioned path prefix `/api/v1` from the start — cheap now, awkward later
- `Result<T>` maps to status codes in one place, via `TransformResult`:
  `NotFound` → 404, `Conflict` → 409, `Validation` → 400 with per-field errors,
  `NodeUnreachable` → 503, `AgentError` → 502 with the node's stderr in `detail`
- Long-running commands (download, benchmark, upgrade) return **202** with the
  job id and a `Location` header pointing at its status resource. They never
  block
- Progress and log tails are exposed as **Server-Sent Events**, not SignalR —
  `curl -N` should work without a client library
- OpenAPI document served at `/openapi/v1.json`, with Scalar for a browsable UI

### Authentication

API keys, separate from both the operator cookie and the agent tokens. Sent as
`X-Api-Key`, stored hashed, each with a label and a last-used timestamp so a
forgotten script key can be identified before revoking it. Managed from the
security page.

Rate limiting via the built-in limiter — generous, but present, so a looping
script can't saturate the agents.

### Scope boundary

The API mirrors the command and query surface and nothing more — and with
Immediate.Apis it holds by construction rather than by discipline, since the
endpoint *is* the handler the UI calls. There is no separate code path that can
drift.

Not every handler should be exposed, though. A handler without a `[MapX]`
attribute simply has no endpoint, which is the mechanism for keeping
UI-internal queries off the public surface. **Tagged registration** is the other
lever, letting a subset of endpoints be registered per host or per environment —
useful if the API is ever wanted on a separate port from the UI.

## Authentication

Single operator, LAN-only, but not none:

- Cookie auth against a local account store; OIDC behind a feature flag if it
  ever joins an SSO setup
- `[Authorize]` on `Routes.razor` fallback, not per page — opt out explicitly
- Separate API keys for `/api` (CLI/scripts), independent of the agent tokens
- Antiforgery on the HTTP surface; Blazor's circuit is authenticated at
  connection time
- CSP without `unsafe-inline`; Blazor's requirements are documented and met

## Persistence

- EF Core + SQLite, WAL enabled, single file next to the app
- `IDbContextFactory` everywhere (see DI lifetimes)
- Migrations applied at startup, guarded so a failed migration stops the app
  rather than running against a half-updated schema
- Retention: telemetry samples ~24h, log chunks not persisted at all (journald
  on the node is the record), benchmark results kept forever — they're the
  point
- Backup: the SQLite file plus the config git repos; both small enough for a
  nightly copy

## Configuration

```jsonc
{
  "Llamactl": {
    "Database": { "Path": "/var/lib/llamactl/llamactl.db" },
    "Agents":   { "HeartbeatSeconds": 10, "UnreachableAfterSeconds": 30 },
    "Polling":  { "SlotsSeconds": 2, "TelemetrySeconds": 2 },
    "Realtime": { "LogBatchMs": 200, "LogRingLines": 2000 },
    "Limits":   { "MaxConcurrentDownloadsPerNode": 1 }
  }
}
```

Secrets (agent token pepper, cookie keys) from environment or a mounted file,
never appsettings. Data protection keys persisted to disk or the container
loses every session on restart.

## Hosting

- Runs in its own container, not on a GPU node — it must stay up while a node
  reboots
- Kestrel behind a reverse proxy for TLS
- systemd unit, `Restart=always`
- Health endpoints: `/health/live`, `/health/ready` (ready includes database
  reachable; deliberately *not* "all nodes healthy", or a rebooting node would
  take the UI out of rotation)

## Observability

- Serilog to console and journald, structured
- OpenTelemetry traces on command handling and agent round-trips; a slow
  "start instance" should be attributable to the node, not guessed at
- Metrics: command durations, agent RTT, bus queue depth, dropped bus messages.
  Dropped messages are the early warning that backpressure limits are wrong

## Testing

| Layer | Approach |
|---|---|
| Handlers | construct the handler directly (it's an ordinary class), SQLite in-memory, faked `INodeGateway` — no mediator to stand up |
| Validation | call the generated `Command.Validate(...)` and assert on `Errors` |
| Slices end-to-end | `WebApplicationFactory` + a stub agent connecting to the real hub |
| Components | bUnit for logic-bearing ones (preset editor, fit calculator) |
| Wire protocol | contract tests over `llamactl.Contracts` shared with the agent |
| Parsers | fixture-driven, from captured `--help`, `llama-bench` and log output |

That last row matters most: parsers of upstream output are the part guaranteed
to break on a llama.cpp upgrade, and fixtures make the break a failing test
rather than a broken page.

## Performance budgets

Modest, but worth stating so they can be violated visibly:

- Page interactive < 500ms on LAN
- Bus → rendered update < 250ms
- Log tail sustains 1,000 lines/sec without circuit lag (via batching + ring)
- 10 concurrent tabs cause no additional agent traffic

## Decisions taken

- **ImmediatePlatform** — Immediate.Handlers, Immediate.Apis, Immediate.Validations. No MediatR, no FluentValidation, no hand-written endpoint registration
- **MudBlazor** for components
- **`/api` ships in v1**, SSE for streams, API keys separate from operator auth
- **No Monaco in v1** — `MudTextField` plus DiffPlex-rendered diffs
- **Prerendering off**, `InteractiveServer` globally
- **`IDbContextFactory`**, never a scoped `DbContext`
- **Validation throws**; `IExceptionHandler` on the HTTP surface, an `Invoke.Guarded`
  wrapper on the Blazor surface, and no `ExceptionToResultBehavior`
- **Immediate.Jobs** for scheduled work

## Open questions

1. Multi-user: if a second operator ever exists, preset edit conflicts need more
   than an optimistic token. Premature to design for now.
2. Does the API need long-poll as well as SSE, for clients behind proxies that
   buffer? Only worth solving if it actually bites.
3. Whether `MudChart` is enough for benchmark comparison, or whether the charts
   want something with better axis control.
4. Immediate.Jobs' storage model — in-process, or its own tables alongside the
   EF Core schema?
5. Immediate.Cache for the expensive read-mostly queries (flag schema, model
   inventory) — it coalesces concurrent requests for one key onto a single
   execution, which fits the ten-tabs-one-node case exactly.
