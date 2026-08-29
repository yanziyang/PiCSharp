# D5 — Terminal UI Strategy

**Status:** Proposed — sign off before wave 5
**Gates:** wave 5 (17,000 LOC), T6.8 (43 interactive components), and tier-2 of `extension-api.md`

---

## Decision

**Port `pi-tui` to C#, preserving its component and layout contracts.** Do not adopt Terminal.Gui's
widget model as the foundation. Spectre.Console may be used for isolated non-interactive rendering
(tables in `/help`-style output), never as the interactive engine.

This is the most expensive decision in the project and the one most likely to be challenged, so the
reasoning is set out in full.

---

## What `pi-tui` actually is

17,000 LOC across 40 files. Not a widget toolkit — a differential terminal renderer with:

- A layout engine (`layout.ts`, `layout-node.ts`) and container model
- A **differential renderer**: computes the minimal escape-sequence delta between frames. This is
  what makes Pi feel responsive while streaming tokens.
- Main-screen and alternate-screen modes (`tui-main-screen.ts`, `tui-alt-screen.ts`)
- A full editor component: keybindings, kill-ring, undo stack, word navigation
- Grapheme segmentation and East-Asian width (`get-east-asian-width`) for correct CJK and emoji width
- Inline terminal images: Kitty and iTerm protocols, with GIF/JPEG/PNG dimension probing
- Capability detection, ANSI segment extraction, fuzzy matching, autocomplete, alt-screen search
- Small native modules: `win32-console-mode.node`, `darwin-modifiers.node`

---

## Why not Terminal.Gui

Terminal.Gui is a capable, mature full-screen TUI toolkit and it does use double-buffered
differential rendering. On the surface it looks like it removes most of wave 5.

It does not — but for narrower reasons than an earlier draft of this document claimed.

**Correction.** The earlier draft argued that Pi "hands extensions a region of the render tree", so a
foreign widget model would break the extension mirror and force the 41% of UI-coupled extensions into
bespoke rewrites. The spike in `spikes/d1-acceptance-test.md` disproved that. The extension-facing
contract is four members:

```ts
export interface Component {
  render(width: number): string[];
  handleInput?(data: string): void;
  wantsKeyRelease?: boolean;
  invalidate?(): void;
}
```

Extensions emit arrays of pre-styled strings. That contract could be implemented on top of almost any
renderer, Terminal.Gui included. **The extension mirror does not, by itself, decide this question.**

The reasons that do stand, in descending weight:

1. **Upstream's own 43 interactive components** (`modes/interactive/`, 18,302 LOC in T6.8) are written
   against pi-tui's *internals*, not the four-member public contract. Those are ours to port either
   way, and porting them onto a foreign layout and focus model is materially harder than onto a direct
   port of the engine they were written for.
2. **Extensions import pi-tui helpers directly** — `matchesKey`, `Text`, `truncateToWidth` — and
   `Theme.Fg` must emit byte-identical ANSI. We owe faithful versions of these regardless of what sits
   underneath, and they are entangled with the renderer's width and styling model.
3. **Golden-buffer byte-identity** (`differential-testing.md §Oracle 5`) is the wave's gate. Producing
   an exact byte match through a foreign abstraction that has its own opinions about clearing,
   cursor movement and repaint is harder than in a direct port.
4. Terminal.Gui carries environment assumptions Pi does not.

This is now a **cost-and-risk argument, not an impossibility argument.** If someone produces a
credible plan to satisfy (1)–(3) on top of Terminal.Gui, it deserves a hearing — the earlier draft
foreclosed that debate on a false premise.

## Why not Spectre.Console as the engine

Spectre.Console is excellent at what it does — rich, scrolling, non-interactive output — and it is
explicitly complementary to full-screen TUI frameworks rather than a substitute. It has no
full-screen differential renderer, no component tree for extensions to render into, and no editor.
It is the wrong shape for the interactive transcript.

Use it where it fits: static tables and formatted blocks in print mode. Never on the interactive path.

---

## What .NET makes easier

Two parts of `pi-tui` get *cheaper* in C#, and they should be scheduled to bank the win early:

1. **The native modules disappear.** Upstream ships prebuilt `.node` binaries for Windows console
   mode and macOS keyboard modifiers. In .NET this is `P/Invoke` to `kernel32!SetConsoleMode` and the
   macOS equivalent — no native build step, no prebuild matrix, no per-arch artefacts.
2. **Text segmentation is in the box.** `System.Globalization.StringInfo` and
   `System.Text.Rune` cover grapheme clusters natively. East-Asian width still needs a data table
   (upstream uses `get-east-asian-width`), but the hard part is provided.

Requires `InvariantGlobalization=false` — see `docs/solution-layout.md §4`.

---

## Sequencing

`pi-tui` has **no internal dependencies** — upstream builds it first. Start wave 5 in parallel with
wave 2, under a dedicated owner. It is the least predictable work in the project and the longest
pole; discovering that in month nine is avoidable.

| Task | Scope | LOC | Fit |
|---|---|---|---|
| T5.1 | Layout engine, layout-node, container model | ~3,000 | B |
| T5.2 | Differential renderer and terminal writer | ~3,500 | C |
| T5.3 | Editor, keybindings, kill-ring, undo, word navigation | ~4,000 | B |
| T5.4 | Grapheme segmentation, East-Asian width, ANSI extraction | ~2,000 | B |
| T5.5 | Terminal images: Kitty, iTerm, dimension probing | ~2,500 | C |
| T5.6 | Autocomplete, fuzzy matching, alt-screen search | ~2,000 | A |

**T5.2 is rated C and must not be delegated as a single packet.** The diff renderer is the component
where a plausible-looking implementation passes unit tests and still repaints the whole screen on
every keystroke. Have an engineer own the algorithm; delegate the surrounding code.

---

## Verification

Gate the entire wave on **byte-identical golden buffers** captured from the TypeScript renderer —
see `docs/differential-testing.md §Oracle 5`. Corpus must include width boundaries, CJK, emoji with
ZWJ sequences, combining marks, style runs across wraps, single-cell incremental redraw, scroll
regions, alternate-screen transitions, and image placement.

Add one performance assertion: **a single-character edit must not produce a full repaint.** Assert on
emitted byte count, not on wall-clock time. This is the regression that unit tests never catch and
users notice immediately.

---

## Reconsider this decision if

- **Round 2 of the acceptance test fails** (`extension-api.md §7`). `modal-editor.ts` and
  `rainbow-editor.ts` exercise `EditorFactory` and `onTerminalInput` — the surfaces round 1 did not
  reach. If those cannot be mirrored, the editor is more entangled with the renderer than the
  four-member `Component` contract suggests, and reasons (1) and (2) above get stronger, not weaker.
- Someone produces a costed plan satisfying reasons (1)–(3) on top of Terminal.Gui. Since the
  impossibility argument is withdrawn, this is now a legitimate proposal rather than a non-starter.
- Product scope drops the interactive TUI entirely (headless service only). Then wave 5 is not
  needed at all, wave 6's T6.8 shrinks by 18,302 LOC, and this document is moot — which would be a
  far larger scope change than a TUI library choice, and should be taken deliberately.
