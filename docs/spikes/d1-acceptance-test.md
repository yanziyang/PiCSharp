# Spike — D1 Acceptance Test

**Run:** 2026-08-29 · **Against:** `extension-api.md` draft, upstream v0.84.4
**Test defined in:** `extension-api.md §7` — hand-port three extensions; the design passes if each ports
without forcing an API change.

## Verdict

**PASS — both rounds.** Round 1 (§2–§4) and round 2 (§6) together forced five amendments (A–E) and
no redesign. Two documents required correction (§5). D1 is safer than the draft claimed; the one
contract materially larger than expected is the editor base class (amendment D). **Proceed to
sign-off once A–E are folded in.**

---

## 1. The finding that changes things

`extension-api.md §5` split the UI surface into "tier 1 — dialogs, portable" and "tier 2 — component
ownership, high risk", and asserted that tier 2 means an extension "owns a region of the render tree".

**That is wrong.** The actual contract, from `packages/tui/src/tui.ts`:

```ts
export interface Component {
  render(width: number): string[];
  handleInput?(data: string): void;
  wantsKeyRelease?: boolean;
  invalidate?(): void;
}
```

Four members, and the substance is `render(width: number): string[]`. Extensions emit **arrays of
pre-styled strings**. They never touch the diff algorithm, never hold a retained widget tree, never do
cursor arithmetic. The renderer consumes lines.

`ctx.ui.setWidget` even accepts a bare `string[]` as an alternative to a component factory.

This maps to C# with no loss:

```csharp
public interface IComponent
{
    string[] Render(int width);
    void HandleInput(string data) { }
    bool WantsKeyRelease => false;
    void Invalidate() { }
}
```

Tier 2 is not a risk tier. Downgrade it.

---

## 2. Port 1 — `permission-gate.ts` (34 lines)

Exercises: `tool_call` interception, blocking, `ctx.hasUI`, `ctx.ui.select`.

```csharp
public sealed class PermissionGate : IPiExtension
{
    private static readonly Regex[] Dangerous =
    [
        new(@"\brm\s+(-rf?|--recursive)", RegexOptions.IgnoreCase),
        new(@"\bsudo\b",                  RegexOptions.IgnoreCase),
        new(@"\b(chmod|chown)\b.*777",    RegexOptions.IgnoreCase),
    ];

    public ValueTask ConfigureAsync(IExtensionApi pi, CancellationToken ct)
    {
        pi.On(PiEvents.ToolCall, async (e, ctx, ct) =>
        {
            if (e is not BashToolCallEvent bash) return null;

            var command = bash.Input.Command;
            if (!Dangerous.Any(p => p.IsMatch(command))) return null;

            if (!ctx.HasUI)
                return new ToolCallEventResult
                {
                    Block = true,
                    Reason = "Dangerous command blocked (no UI for confirmation)"
                };

            var choice = await ctx.Ui.SelectAsync(
                $"⚠️ Dangerous command:\n\n  {command}\n\nAllow?", ["Yes", "No"], ct: ct);

            return choice != "Yes"
                ? new ToolCallEventResult { Block = true, Reason = "Blocked by user" }
                : null;
        });
        return ValueTask.CompletedTask;
    }
}
```

**Result: clean port, no API change.** One shape difference worth documenting: TS tests
`event.toolName !== "bash"` then casts `event.input.command as string`; C# pattern-matches the union
member and gets typed access. That is strictly better, but it is a transform a porter must know.

---

## 3. Port 2 — `status-line.ts` (32 lines)

Exercises: `ctx.ui.setStatus`, `ctx.ui.theme`, closure state across handlers.

```csharp
public sealed class StatusLine : IPiExtension
{
    private int _turnCount;

    public ValueTask ConfigureAsync(IExtensionApi pi, CancellationToken ct)
    {
        pi.On(PiEvents.SessionStart, (e, ctx, ct) =>
            ctx.Ui.SetStatus("status-demo", ctx.Ui.Theme.Fg(ThemeColor.Dim, "Ready")));

        pi.On(PiEvents.TurnStart, (e, ctx, ct) =>
        {
            _turnCount++;
            var th = ctx.Ui.Theme;
            ctx.Ui.SetStatus("status-demo",
                th.Fg(ThemeColor.Accent, "●") + th.Fg(ThemeColor.Dim, $" Turn {_turnCount}..."));
        });

        pi.On(PiEvents.TurnEnd, (e, ctx, ct) =>
        {
            var th = ctx.Ui.Theme;
            ctx.Ui.SetStatus("status-demo",
                th.Fg(ThemeColor.Success, "✓") + th.Fg(ThemeColor.Dim, $" Turn {_turnCount} complete"));
        });

        return ValueTask.CompletedTask;
    }
}
```

**Result: clean port.** Closure `let turnCount` becomes an instance field — mechanical. `ThemeColor` is
a closed TS string union, so it becomes a C# enum with `[JsonPropertyName]` per member.

Note this extension was chosen in `§7` as the tier-2 representative. It is not — it only calls
`setStatus`, which takes a plain string. See §5 for the replacement.

---

## 4. Port 3 — `todo.ts` (297 lines) — the real test

Exercises: tool registration with a schema, `renderCall`/`renderResult` returning components, a custom
`IComponent` with input handling and render caching, `ctx.ui.custom` overlay, state reconstruction from
the session branch, and direct imports from `pi-tui` (`matchesKey`, `Text`, `truncateToWidth`).

The component ports essentially line-for-line:

```csharp
internal sealed class TodoListComponent(
    IReadOnlyList<Todo> todos, ITheme theme, Action onClose) : IComponent
{
    private int? _cachedWidth;
    private string[]? _cachedLines;

    public void HandleInput(string data)
    {
        if (Keys.Matches(data, "escape") || Keys.Matches(data, "ctrl+c")) onClose();
    }

    public string[] Render(int width)
    {
        if (_cachedLines is not null && _cachedWidth == width) return _cachedLines;

        var lines = new List<string> { "" };
        var title = theme.Fg(ThemeColor.Accent, " Todos ");
        lines.Add(Text.TruncateToWidth(
            theme.Fg(ThemeColor.BorderMuted, new string('─', 3)) + title +
            theme.Fg(ThemeColor.BorderMuted, new string('─', Math.Max(0, width - 10))), width));
        lines.Add("");

        if (todos.Count == 0)
        {
            lines.Add(Text.TruncateToWidth(
                $"  {theme.Fg(ThemeColor.Dim, "No todos yet. Ask the agent to add some!")}", width));
        }
        else
        {
            var done = todos.Count(t => t.Done);
            lines.Add(Text.TruncateToWidth(
                $"  {theme.Fg(ThemeColor.Muted, $"{done}/{todos.Count} completed")}", width));
            lines.Add("");
            foreach (var todo in todos)
            {
                var check = todo.Done ? theme.Fg(ThemeColor.Success, "✓")
                                      : theme.Fg(ThemeColor.Dim, "○");
                var id   = theme.Fg(ThemeColor.Accent, $"#{todo.Id}");
                var text = todo.Done ? theme.Fg(ThemeColor.Dim, todo.Text)
                                     : theme.Fg(ThemeColor.Text, todo.Text);
                lines.Add(Text.TruncateToWidth($"  {check} {id} {text}", width));
            }
        }

        lines.Add("");
        lines.Add(Text.TruncateToWidth($"  {theme.Fg(ThemeColor.Dim, "Press Escape to close")}", width));
        lines.Add("");

        _cachedWidth = width;
        _cachedLines = [.. lines];
        return _cachedLines;
    }

    public void Invalidate() { _cachedWidth = null; _cachedLines = null; }
}
```

**Result: ports cleanly, but surfaces three required amendments.**

### Amendment A — sync handler overloads

Both ports above are littered with `return ValueTask.CompletedTask;`. Most handlers are synchronous.
Add non-async overloads:

```csharp
void On<TEvent>(EventDescriptor<TEvent> ev, Action<TEvent, IExtensionContext, CancellationToken> handler);
```

Cosmetic, but it affects every one of the 85 ports. Worth doing.

### Amendment B — non-generic `CustomAsync`

`todo.ts` calls `ctx.ui.custom<void>(...)`. C# has no `Task<void>`. Add:

```csharp
ValueTask CustomAsync(Func<ITui, ITheme, IKeybindings, Action, IComponent> factory, CancellationToken ct = default);
ValueTask<T> CustomAsync<T>(Func<ITui, ITheme, IKeybindings, Action<T>, IComponent> factory, CancellationToken ct = default);
```

### Amendment C — tool parameter schemas

`todo.ts` builds params with typebox at runtime:

```ts
const TodoParams = Type.Object({
  action: StringEnum(["list","add","toggle","clear"] as const),
  text: Type.Optional(Type.String({ description: "Todo text (for add)" })),
  id: Type.Optional(Type.Number({ description: "Todo ID (for toggle)" })),
});
```

C# needs a declared type plus a source-generated schema:

```csharp
[PiToolParams]
public sealed record TodoParams
{
    [JsonPropertyName("action")] public required TodoAction Action { get; init; }
    [JsonPropertyName("text")] [Description("Todo text (for add)")] public string? Text { get; init; }
    [JsonPropertyName("id")]   [Description("Todo ID (for toggle)")] public int? Id { get; init; }
}
```

This is the **only** place in three ports where the translation is structural rather than mechanical,
and it is exactly the risk flagged in `translation-patterns.md §7`: the emitted JSON Schema must be
byte-identical to typebox's output or provider behaviour shifts. **Confirmed as the project's top
schema risk.** Golden-test the emitter against typebox output before T2.1 closes.

---

## 5. Corrections required elsewhere

| Document | Correction |
|---|---|
| `extension-api.md §5` | Tier 2 is mischaracterised. The component contract is `render(width) => string[]`, not render-tree ownership. Downgrade the risk and replace `status-line.ts` with `todo.ts` as the tier-2 exemplar in §7. |
| `tui-strategy.md` | The primary argument — "a foreign widget model breaks the extension mirror" — is **weaker than stated**. Extensions never see a widget model. The real constraints are narrower: extensions import `matchesKey`, `Text`, `truncateToWidth` from `pi-tui` directly; `Theme.Fg` must produce identical ANSI; and upstream's own 43 interactive components are written against pi-tui internals. The conclusion (port `pi-tui`) still holds, but on those grounds, not the stated one. |

Both are corrected in the same change as this spike.

---

## 6. Round 2 — run 2026-08-29 · **PASS, with amendments D and E**

Round 1 left the high-variance surfaces untested. Round 2 covers editor replacement and custom
providers.

| Extension | LOC | Exercises | Result |
|---|---|---|---|
| `rainbow-editor.ts` | 88 | `setEditorComponent`, `CustomEditor` subclassing, `tui.requestRender()`, timer-driven animation | ports; forces **D** |
| `modal-editor.ts` | 85 | `CustomEditor` subclassing, `super.handleInput`, pi-tui helpers | ports; forces **D** |
| `custom-provider-anthropic/` | 611 | `registerProvider`, OAuth delegates, `streamSimple` | ports; forces **E** |

### 6.1 The finding — editors are subclassed, not implemented

Both editor extensions do this:

```ts
class ModalEditor extends CustomEditor {
  handleInput(data: string): void {
    if (matchesKey(data, "escape")) { /* … */ return; }
    super.handleInput(data);
  }
  render(width: number): string[] {
    const lines = super.render(width);
    /* decorate */
    return lines;
  }
}
pi.on("session_start", (_e, ctx) =>
  ctx.ui.setEditorComponent((tui, theme, kb) => new ModalEditor(tui, theme, kb)));
```

And `CustomEditor` is itself `class CustomEditor extends Editor`, where `Editor` comes from pi-tui.

**This is a materially larger contract than `IComponent`.** The extension inherits, transitively, the
whole of pi-tui's `Editor`: text buffer, cursor, autocomplete, undo stack, kill-ring, word navigation.
Members reached by these two extensions alone: `super.handleInput`, `super.render`, `this.getText()`,
`this.tui.requestRender()`, plus `onAction`, `actionHandlers` and `onExtensionShortcut` on
`CustomEditor`.

**Still mirrorable.** C# has inheritance, `virtual` dispatch and `protected` visibility, and both
ports are mechanical:

```csharp
internal sealed class ModalEditor(ITui tui, IEditorTheme theme, IKeybindings kb)
    : CustomEditor(tui, theme, kb)
{
    private EditMode _mode = EditMode.Insert;

    public override void HandleInput(string data)
    {
        if (Keys.Matches(data, "escape"))
        {
            if (_mode == EditMode.Insert) _mode = EditMode.Normal;
            else base.HandleInput(data);
            return;
        }
        if (_mode == EditMode.Insert) { base.HandleInput(data); return; }
        // … normal-mode mapping
    }

    public override string[] Render(int width)
    {
        var lines = base.Render(width);
        if (lines.Length == 0) return lines;
        var label = _mode == EditMode.Normal ? " NORMAL " : " INSERT ";
        var last = lines.Length - 1;
        if (Text.VisibleWidth(lines[last]) >= label.Length)
            lines[last] = Text.TruncateToWidth(lines[last], width - label.Length, "") + label;
        return lines;
    }
}
```

### Amendment D — `Editor` and `CustomEditor` are public contract

`Pi.Tui.Editor` and `Pi.CodingAgent.CustomEditor` must be **public, non-sealed**, with `virtual`
`HandleInput` / `Render` and their protected surface treated as part of the extension API — versioned
and change-controlled like any other public contract. A porter cannot decide `Editor`'s member
visibility on convenience grounds; the visibility *is* the contract.

Consequence for `tui-strategy.md`: this is the concrete form of reason (1). Extensions do not merely
consume pi-tui, three of them **inherit from it**.

### 6.2 Custom providers port cleanly

`registerProvider` is a declarative object plus four delegates — nothing structural:

```csharp
pi.RegisterProvider("custom-anthropic", new ProviderOptions
{
    BaseUrl = "https://api.anthropic.com",
    ApiKey  = "$CUSTOM_ANTHROPIC_API_KEY",
    Api     = "custom-anthropic-api",
    Models  = [ /* declarative model metadata */ ],
    OAuth   = new OAuthOptions
    {
        Name = "Custom Anthropic (Claude Pro/Max)",
        Login = LoginAnthropicAsync,
        RefreshToken = RefreshAnthropicTokenAsync,
        GetApiKey = cred => cred.Access,
    },
    StreamSimple = StreamCustomAnthropic,
});
```

### Amendment E — push-style event streams

`streamSimple` returns an `AssistantMessageEventStream` built by `createAssistantMessageEventStream()`
— a *push* stream written from a detached async task. C# needs the equivalent:

```csharp
public static AssistantMessageEventStream Create();   // Channel<T>-backed
// exposes: ValueTask WriteAsync(AssistantMessageEvent e); void Complete(Exception? error = null);
// consumed as: IAsyncEnumerable<AssistantMessageEvent>
```

Use `System.Threading.Channels`, per `translation-patterns.md §5`. Do not model this as
`async IAsyncEnumerable` with `yield` — the upstream shape writes from a task that outlives the
factory call, and rewriting it to a pull model changes provider error and cancellation timing.

### 6.3 Population check

Only **3 of 85** bundled extensions replace the editor (`modal-editor.ts`, `rainbow-editor.ts`,
`border-status-editor.ts`) — **3.5%**, not the 41% that touch UI generally. Amendment D is
structurally significant but narrow in blast radius.

---

## 7. Combined verdict

**D1 passes both rounds.** Five amendments (A–E), no redesign. Every extension tested ported
mechanically once the amendment was in place.

Still untested, and low-risk enough to defer to implementation: `AutocompleteProviderFactory`,
`registerMarkdownTransformer`, `onTerminalInput` standalone (round 2 reached it only via the editor).

### Consequence for Option B in the feasibility report

Report rev 2 says editors "degrade over IPC". For editor-replacing extensions that is **too
generous**: `super.HandleInput()` and `super.Render()` are base-class calls into the host, so they do
not degrade across a process boundary — they do not work at all without proxying every base method
per keystroke. `rainbow-editor.ts` compounds this with a 60 ms animation timer calling
`requestRender()`, roughly 17 IPC round trips per second.

The report's ~90% figure survives (the affected population is 3.5%), but the word "degrade" should be
"break" for this subset. Recorded here; a rev 3 is not warranted for a 3.5% nuance unless the report
is being revised for another reason.
