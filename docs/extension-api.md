# D1 — C# Extension API Design

**Status:** Proposed — requires sign-off before wave 6 begins
**Gates:** T6.9 (extension host), all of wave 7 (85 extension ports), and the shape of ~56k LOC in `Pi.CodingAgent`
**Upstream reference:** `reference/pi/packages/coding-agent/src/core/extensions/types.ts` (1,791 LOC, 157 exported types)

---

## Decision

**Mirror Pi's `ExtensionAPI` in C# as closely as the language permits.** Same event names, same
registration verbs, same context surface, same semantics. Deviate only where C# cannot follow, and
where it cannot, apply the fixed patterns in §4 — never an ad-hoc choice per site.

### Why mirroring, not an idiomatic redesign

We cannot run the TypeScript extensions under any design. The only question is what happens to the
85 bundled extensions and the community catalogue.

- **Mirrored API** — porting an extension becomes mechanical: rename members to PascalCase, swap
  `AbortSignal` for `CancellationToken`, compile. One Codex packet handles 2–4 extensions. Wave 7 is
  a real wave.
- **Idiomatic redesign** — every extension becomes a bespoke rewrite requiring someone to understand
  both the extension's intent and the new API. Wave 7 stops being delegatable, and in practice most
  extensions never get ported.

The mirror also gives Codex an unambiguous specification for T6.9. An idiomatic design would have to
be invented, reviewed, and then explained in prose — the exact situation the port is trying to avoid.

**Cost of the decision:** the C# API will read as un-idiomatic in places (event-name strings, a
context object rather than injected services). Accept this. It buys a portable ecosystem, and
`docs/translation-patterns.md` keeps the un-idiomatic parts uniform.

---

## 1. Entry point

TypeScript:

```ts
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

export default function (pi: ExtensionAPI) { ... }
```

C#:

```csharp
using Pi.CodingAgent.Extensions;

public sealed class MyExtension : IPiExtension
{
    public ValueTask ConfigureAsync(IExtensionApi pi, CancellationToken ct) { ... }
}
```

Extensions are .NET assemblies discovered from the package directories, loaded into a collectible
`AssemblyLoadContext` so `/reload` can unload them. The TS factory may be sync or async; the C#
equivalent is always `ValueTask` — a synchronous body returns `ValueTask.CompletedTask`.

**Rejected:** an attribute-driven registration model (`[PiEvent("tool_call")]`). It reads better in
C# but destroys the 1:1 correspondence that makes wave 7 mechanical.

---

## 2. Event subscription — 37 events

TypeScript uses 37 `on()` overloads discriminated by a string literal. C# uses one generic method
with a static event descriptor, which preserves the name, gives compile-time payload typing, and
keeps the call site nearly identical.

```csharp
// TS: pi.on("tool_call", async (event, ctx) => { ... });
pi.On(PiEvents.ToolCall, async (e, ctx, ct) => { ... });
```

```csharp
public interface IExtensionApi
{
    void On<TEvent>(EventDescriptor<TEvent> ev, ExtensionHandler<TEvent> handler);
    void On<TEvent, TResult>(EventDescriptor<TEvent, TResult> ev, ExtensionHandler<TEvent, TResult> handler);
}

public delegate ValueTask ExtensionHandler<TEvent>(TEvent e, IExtensionContext ctx, CancellationToken ct);
public delegate ValueTask<TResult?> ExtensionHandler<TEvent, TResult>(TEvent e, IExtensionContext ctx, CancellationToken ct);
```

`PiEvents` is a static class of descriptors, one per upstream event, carrying the exact wire name:

```csharp
public static class PiEvents
{
    public static readonly EventDescriptor<ToolCallEvent, ToolCallEventResult> ToolCall = new("tool_call");
    public static readonly EventDescriptor<SessionStartEvent> SessionStart = new("session_start");
    // ... 35 more
}
```

The full event list is the source of truth in `types.ts` lines 1257–1301. Port all 37. Do not
subset: a missing event silently breaks whichever extension needed it.

Events returning a result are the interception points and carry the real semantics — `tool_call`
(block), `tool_result` (rewrite), `context` (inject messages), `input` (transform), `message_end`
(replace), `before_agent_start` (replace system prompt), `user_bash` (replace execution), plus the
five `session_before_*` cancellation events.

---

## 3. Registration and action surface

Mirror every member. Grouped as upstream groups them:

| Group | TS members | C# |
|---|---|---|
| Tools | `registerTool` | `RegisterTool<TParams>(ToolDefinition<TParams>)` |
| Commands | `registerCommand` | `RegisterCommand(string, CommandOptions)` |
| Shortcuts | `registerShortcut` | `RegisterShortcut(KeyId, ShortcutOptions)` |
| Flags | `registerFlag`, `getFlag` | `RegisterFlag(string, FlagOptions)`, `GetFlag(string)` |
| Rendering | `registerMessageRenderer`, `registerEntryRenderer`, `registerMarkdownTransformer` | same, PascalCase |
| Providers | `registerProvider` | `RegisterProvider(string, ProviderOptions)` |
| Actions | `sendMessage`, `sendUserMessage`, `appendEntry`, `exec` | same, PascalCase; `exec` → `ExecAsync` |
| Tools state | `getActiveTools`, `getAllTools`, `setActiveTools`, `getCommands` | same |
| Session meta | `setSessionName`, `getSessionName`, `setLabel` | same |
| Model | `setModel`, `getThinkingLevel`, `setThinkingLevel` | `SetModelAsync`, others same |

`registerTool` uses typebox schemas upstream. In C#, take a JSON Schema derived from the parameter
type via source generation, and keep the emitted schema byte-identical to typebox's output for the
same shape — provider payloads depend on it. See `docs/translation-patterns.md §JSON Schema`.

---

## 4. The five translation problems

These are the only places where a faithful mirror is impossible. Each has one fixed resolution.
Applying a different resolution anywhere is a defect.

### 4.1 In-place mutation of `event.input`

Upstream contract: rewrite a tool call by mutating `event.input` directly; the return value is
reserved for blocking.

**Resolution:** event payloads are mutable classes (not records) with settable properties. Handlers
mutate them exactly as in TS. The runner reads the mutated instance after all handlers complete.

```csharp
public sealed class BashToolCallEvent : ToolCallEvent
{
    public BashToolInput Input { get; set; } = default!;   // mutable by design
}
```

This is the one place we deliberately choose a mutable model over a record. Document it at the type.

### 4.2 Callback-taking session methods

`newSession({ setup, withSession })`, `fork(...)`, `switchSession(...)` take closures that receive a
freshly-bound context.

**Resolution:** direct `Func<>` parameters. No inversion, no builder.

```csharp
ValueTask<SessionResult> NewSessionAsync(
    string? parentSession = null,
    Func<ISessionManager, CancellationToken, ValueTask>? setup = null,
    Func<IReplacedSessionContext, CancellationToken, ValueTask>? withSession = null,
    CancellationToken ct = default);
```

### 4.3 `AbortSignal`

**Resolution:** `CancellationToken` throughout. `ctx.signal` (undefined when not streaming) becomes
`ctx.Signal` returning `CancellationToken?`. `ctx.abort()` → `ctx.Abort()`.

### 4.4 Synchronous veto semantics

A handler returning `{ block: true }` stops tool execution before it starts.

**Resolution:** preserved exactly — handlers are awaited in registration order before dispatch, and
the first non-null blocking result wins. Because everything is in-process, timing is comparable to
upstream. Do not introduce queuing or fire-and-forget anywhere on the interception path.

### 4.5 `undefined` vs `null`

TS distinguishes them on the wire; several results use `undefined` for "no opinion" and `null` for
"explicitly cleared" (for example `LabelEntry.label`).

**Resolution:** nullable types plus `JsonIgnoreCondition.WhenWritingNull` for `undefined`, and an
explicit `JsonValue.Null` sentinel where TS genuinely writes `null`. Never collapse the two. Per-site
rules live in `docs/translation-patterns.md`.

---

## 5. UI context

`ExtensionUIContext` splits cleanly into two tiers, and they carry very different risk.

**Tier 1 — dialogs (portable, low risk).** `Confirm`, `Select`, `Input`, `Editor`, `Notify`.
Mirror directly as async methods.

**Tier 2 — component ownership (high risk).** Custom components, overlays, widgets with placement,
`OnTerminalInput`, `AutocompleteProviderFactory`, `EditorFactory`, working-indicator control.
These hand the extension a region of the render tree.

Tier 2 is only mirrorable if `Pi.Tui` preserves upstream's component and layout contracts. This is
the dependency that drives `docs/tui-strategy.md` to recommend porting `pi-tui` rather than adopting
Terminal.Gui's widget model. **If that decision is reversed, tier 2 cannot be mirrored and roughly
41% of extensions become bespoke rewrites — D1's value collapses with it.**

---

## 6. Explicitly out of scope

- **No `jiti` equivalent.** Extensions are compiled assemblies. There is no runtime TypeScript path,
  and adding one would reintroduce a Node dependency.
- **No sandbox.** Upstream ships no permission system and extensions run fully privileged; we match
  that and inherit the same containerisation guidance. Do not invent a permission model here — that
  is a separate product decision.
- **No source compatibility.** `.ts` extensions will not load. Wave 7 ports them.

---

## 7. Acceptance test for this design

Before wave 6 starts, port three extensions by hand against the draft API:

| Extension | Exercises |
|---|---|
| `permission-gate.ts` | `tool_call` interception, blocking, `ui.confirm` |
| `todo.ts` | tool registration, `appendEntry`, custom entry rendering |
| `status-line.ts` | tier-2 UI — widget placement and render ownership |

**The design passes if each ports in under a day with no API changes required.** If any of the three
forces a change, revise this document before wave 6 — not during it. If `status-line.ts` cannot be
ported at all, escalate: that is the signal that the TUI strategy and D1 are in conflict.
