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

    // Amendment A (spike): most handlers are synchronous. Without these overloads every
    // sync handler ends in `return ValueTask.CompletedTask;` — noise across all 85 ports.
    void On<TEvent>(EventDescriptor<TEvent> ev, Action<TEvent, IExtensionContext, CancellationToken> handler);
    void On<TEvent, TResult>(EventDescriptor<TEvent, TResult> ev, Func<TEvent, IExtensionContext, CancellationToken, TResult?> handler);
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

### Amendment C (spike) — tool parameter schemas

`registerTool` uses typebox schemas upstream, built as runtime values:

```ts
const TodoParams = Type.Object({
  action: StringEnum(["list","add","toggle","clear"] as const),
  text: Type.Optional(Type.String({ description: "Todo text (for add)" })),
});
```

C# needs a declared type plus a source-generated schema:

```csharp
[PiToolParams]
public sealed record TodoParams
{
    [JsonPropertyName("action")] public required TodoAction Action { get; init; }
    [JsonPropertyName("text")] [Description("Todo text (for add)")] public string? Text { get; init; }
}
```

This is the **only structural** (rather than mechanical) translation found across the three ports in
`spikes/d1-acceptance-test.md`. The emitted JSON Schema must be byte-identical to typebox's output for
the same shape — provider payloads depend on it. Golden-test the emitter against typebox before T2.1
closes. See `translation-patterns.md §7`; this is the project's top schema risk.

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

**Amendment B (spike):** `ctx.ui.custom<void>(...)` has no C# equivalent — there is no `Task<void>`.
Provide both forms:

```csharp
ValueTask    CustomAsync(Func<ITui, ITheme, IKeybindings, Action, IComponent> factory, CancellationToken ct = default);
ValueTask<T> CustomAsync<T>(Func<ITui, ITheme, IKeybindings, Action<T>, IComponent> factory, CancellationToken ct = default);
```

**Tier 2 — components (lower risk than first assessed).** Custom components, overlays, widgets,
`OnTerminalInput`, `AutocompleteProviderFactory`, `EditorFactory`, working-indicator control.

An earlier draft claimed these "hand the extension a region of the render tree". The spike in
`spikes/d1-acceptance-test.md` disproved that. The real contract, from `packages/tui/src/tui.ts`, is
four members:

```ts
export interface Component {
  render(width: number): string[];
  handleInput?(data: string): void;
  wantsKeyRelease?: boolean;
  invalidate?(): void;
}
```

Extensions emit **arrays of pre-styled strings**. They never touch the diff algorithm, never hold a
retained widget tree, never do cursor arithmetic. `ctx.ui.setWidget` even accepts a bare `string[]`.
This mirrors to C# without loss:

```csharp
public interface IComponent
{
    string[] Render(int width);
    void HandleInput(string data) { }
    bool WantsKeyRelease => false;
    void Invalidate() { }
}
```

What tier 2 *does* require of `Pi.Tui` is narrower than a compatible widget model: extensions import
`matchesKey`, `Text` and `truncateToWidth` from `pi-tui` directly, and `Theme.Fg` must emit identical
ANSI. See `tui-strategy.md`, which reaches the same conclusion on these narrower grounds.

### Amendment D (round 2) — the editor is a base class, not an interface

Editor-replacing extensions **subclass** rather than implement:

```ts
class ModalEditor extends CustomEditor {
  handleInput(data: string) { /* … */ super.handleInput(data); }
  render(width: number)     { const lines = super.render(width); /* decorate */ return lines; }
}
ctx.ui.setEditorComponent((tui, theme, kb) => new ModalEditor(tui, theme, kb));
```

`CustomEditor` is itself `class CustomEditor extends Editor`, where `Editor` is pi-tui's. Extensions
therefore inherit, transitively, pi-tui's whole editor: text buffer, cursor, autocomplete, undo stack,
kill-ring, word navigation.

**Consequence.** `Pi.Tui.Editor` and `Pi.CodingAgent.CustomEditor` must be **public and non-sealed**,
with `virtual` `HandleInput` / `Render`, and their protected surface is **part of the extension
contract** — versioned and change-controlled like any other public API. A porter cannot choose member
visibility on convenience grounds; the visibility is the contract.

This is mirrorable and both ports were mechanical, but it is by far the largest contract surface D1
commits to. Blast radius is narrow: 3 of 85 bundled extensions (3.5%) replace the editor.

### Amendment E (round 2) — push-style event streams

`streamSimple` returns an `AssistantMessageEventStream` created by
`createAssistantMessageEventStream()` and written from a detached async task. Provide a
`Channel<T>`-backed equivalent exposing `WriteAsync` / `Complete` and consumed as
`IAsyncEnumerable<AssistantMessageEvent>`. Do not rewrite it as `async IAsyncEnumerable` with
`yield` — the upstream writer outlives the factory call, and a pull model changes provider error and
cancellation timing. See `translation-patterns.md §5`.

**Round 2 confirmed** `EditorFactory` (amendment D) and `registerProvider` (amendment E). Still
untested and deferred as low-risk: `AutocompleteProviderFactory`, `registerMarkdownTransformer`, and
`onTerminalInput` standalone. See `spikes/d1-acceptance-test.md §6`.

---

## 6. Explicitly out of scope

- **No `jiti` equivalent.** Extensions are compiled assemblies. There is no runtime TypeScript path,
  and adding one would reintroduce a Node dependency.
- **No sandbox.** Upstream ships no permission system and extensions run fully privileged; we match
  that and inherit the same containerisation guidance. Do not invent a permission model here — that
  is a separate product decision.
- **No source compatibility.** `.ts` extensions will not load. Wave 7 ports them.

---

## 7. Acceptance test

### Round 1 — run 2026-08-29 · **PASS**

Full working in `spikes/d1-acceptance-test.md`.

| Extension | Exercises | Result |
|---|---|---|
| `permission-gate.ts` (34 L) | `tool_call` interception, blocking, `ctx.hasUI`, `ui.select` | clean, no API change |
| `status-line.ts` (32 L) | `setStatus`, `ui.theme`, closure state | clean, no API change |
| `todo.ts` (297 L) | tool registration + schema, `renderCall`/`renderResult`, custom `IComponent` with input handling and render caching, `ui.custom` overlay, state reconstruction from the session branch | clean; forced amendments A, B, C |

The spike also disproved this document's original tier-2 risk claim — see §5.

### Round 2 — run 2026-08-29 · **PASS**

| Extension | Exercises | Result |
|---|---|---|
| `rainbow-editor.ts` (88 L) | `setEditorComponent`, `CustomEditor` subclassing, `tui.requestRender()`, timer animation | ports; forces **D** |
| `modal-editor.ts` (85 L) | `CustomEditor` subclassing, `super.handleInput`, pi-tui helpers | ports; forces **D** |
| `custom-provider-anthropic/` (611 L) | `registerProvider`, OAuth delegates, `streamSimple` | ports; forces **E** |

> **`AutocompleteProviderFactory` is now proven.** `T5.6` (`139fff1`) ported `IAutocompleteProvider`
> with its optional members as default interface implementations, and the test
> `ExtensionProvider_CanWrapBuiltInProvider` demonstrates an extension-style provider wrapping the
> built-in one through the factory. The mirror holds. `registerMarkdownTransformer` and
> `onTerminalInput` standalone remain untested.

**D1 passes both rounds.** Five amendments, no redesign.

Still untested and deferred to implementation as low-risk: `AutocompleteProviderFactory`,
`registerMarkdownTransformer`, and `onTerminalInput` standalone.
