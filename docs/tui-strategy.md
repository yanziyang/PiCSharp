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

It does not, for one decisive reason.

**Pi's extension API hands extensions a region of the render tree.** `registerMessageRenderer`,
`registerEntryRenderer`, `registerMarkdownTransformer`, custom components, overlays, widget
placement, `EditorFactory` and `AutocompleteProviderFactory` are all written against *pi-tui's*
component and layout contracts. Adopting Terminal.Gui's `View` hierarchy means those contracts change
shape — and then:

- Tier 2 of `extension-api.md` cannot be mirrored.
- The 41% of extensions that render into the UI become bespoke rewrites rather than mechanical ports.
- **D1's central justification collapses**, and with it the case for wave 7.

The saving on wave 5 is paid for several times over in wave 7, in the currency of work that does not
delegate. If the TUI strategy changes, D1 must be revisited in the same decision — they are one
decision wearing two hats.

Secondary concerns, less decisive but real: Terminal.Gui's rendering model would have to reproduce
upstream's exact output bytes to satisfy the golden-buffer oracle, which is harder through a foreign
abstraction than in a direct port; and Terminal.Gui carries environment assumptions Pi does not.

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

- The acceptance test in `extension-api.md §7` shows `status-line.ts` cannot be ported — the tier-2
  mirror is already failing and the main argument for a faithful port is void.
- Product scope drops the interactive TUI entirely (headless service only). Then wave 5 is not
  needed at all, wave 6's T6.8 shrinks by 18,302 LOC, and this document is moot — which would be a
  far larger scope change than a TUI library choice, and should be taken deliberately.
