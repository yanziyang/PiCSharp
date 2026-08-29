# Spike — D1 Acceptance Test

**Run:** 2026-08-29 · **Against:** `extension-api.md` draft, upstream v0.84.4
**Test defined in:** `extension-api.md §7` — hand-port three extensions; the design passes if each ports
without forcing an API change.

## Verdict

**PASS, with three small amendments to the draft API** (§4) and **two documents requiring correction**
(§5). D1 is safer than the draft claimed. Proceed to sign-off once the amendments are folded in.

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

## 6. Residual risk not covered by this test

These three extensions do **not** exercise:

- `setEditorComponent` / `EditorFactory` — replacing the input editor wholesale
- `AutocompleteProviderFactory`
- `onTerminalInput` raw input interception
- `registerProvider` — custom model providers
- `registerMarkdownTransformer`

`modal-editor.ts`, `rainbow-editor.ts` and `custom-provider-anthropic/` cover these. **Recommend a
second acceptance round against those three before wave 6 opens** — they are the remaining places
where the mirror could still fail, and they are cheap to test now.
