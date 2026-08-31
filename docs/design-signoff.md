# Design sign-off — evidence for D1 and D5

**Prepared 2026-08-31. This is evidence and a recommendation; the decision is the project owner's.**

All seven documents in `docs/` are still marked **Proposed**. Two of them gate work that is either
underway or imminent, and both now have evidence that did not exist when they were written:

- **D5** (`tui-strategy.md`) says *"sign off before wave 5"*. Wave 5 is **83% test parity, 94%
  completeness**. That gate was passed without the sign-off happening.
- **D1** (`extension-api.md`) says *"requires sign-off before wave 6 begins"*. Wave 6 is next.

---

## D1 — Extension API. **Recommend: Accept.**

D1 commits to mirroring upstream's `ExtensionAPI` — 157 types, 37 events — so that 85 bundled
extensions port rather than being rewritten. It gates T6.9 and all of wave 7.

### What has been proven since it was written

| Claim | Evidence |
|---|---|
| The contract survives real extensions | Two acceptance rounds, six extensions, both **PASS**. Five amendments (A–E) absorbed **without redesign**. |
| `AutocompleteProviderFactory` mirrors cleanly | `ExtensionProvider_CanWrapBuiltInProvider`, T5.6 (`139fff1`) — an extension-style provider wrapping the built-in one through the factory. |
| **Amendment D — extensions subclass `Editor`** | `Extension_subclass_overrides_editor_hooks_and_calls_base_implementations`, T5.3b (`cc8aad0`). A subclass overrides `Render`, `HandleInput` and `Invalidate`, calls `base.` on each, and decorates output as `border-status-editor.ts` does. |

Amendment D is the one that mattered. C# gives no diagnostic for a missing `virtual` — the extension
simply cannot be written — so it was the single commitment a reading could not settle. It is now a
passing test.

The document has also survived being **wrong once and corrected**: its original claim that extensions
own a region of the render tree was disproved by the D1 spike, and the conclusion moved with the
evidence. A design doc that has absorbed a correction is better evidence than one that never met a test.

### What remains unproven

- `registerMarkdownTransformer` and standalone `onTerminalInput` are still untested in C#. The
  markdown one is **lower risk than when that caveat was written**: its signature is
  `(markdown: string, context) => string`, so it operates on source text and is parser-agnostic —
  established while investigating the Markdig question.
- **No extension has been ported and executed end-to-end in C#.** All evidence is contract-level.
  Wave 7 is the real test, and no amount of further design work substitutes for it.

Accepting D1 does not claim wave 7 is risk-free. It claims the contract has survived every test
available short of running it, and that further deferral buys nothing.

---

## D5 — TUI strategy. **Recommend: Accept, with the verification gap recorded.**

D5 decides to port `pi-tui` to C# rather than adopt Terminal.Gui. It gates wave 5 and T6.8.

### What has been proven

**The decision has been executed, not merely argued.** `Pi.Tui` is 16,076 lines against upstream's
17,000, at 83% test parity with **1,292 tests and zero skips**.

Its central argument — that extensions inherit from pi-tui's `Editor`, so a foreign editor breaks them
outright — is the same claim Amendment D now proves by test. D5's load-bearing reason is no longer an
assertion.

Its delegation ratings were also tested and found conservative: three tasks rated **C — not delegable**
were re-rated **B** after measuring the files, and all three shipped clean (T5.2b, T5.2c, T5.5). The
ratings had been assigned without measurement.

### The gap, stated plainly

D5 specifies its own verification:

> Gate the entire wave on **byte-identical golden buffers** captured from the TypeScript renderer —
> see `differential-testing.md §Oracle 5`.

**That harness was never built.** `Pi.Conformance.Tests` contains `ProjectReferenceTests.cs` — three
facts asserting the project reference chain resolves. `T0.2` was never done. The parity ratchet has
substituted for it: it counts ported cases against upstream, which is a real signal but a weaker one
than comparing emitted bytes against the reference implementation.

The second commitment fared better. D5 requires *"a single-character edit must not produce a full
repaint — assert on emitted byte count, not wall-clock time."* `DifferentialRendererTests` does assert
the property, via `Assert.False(result.FullRedraw)` and `FullRedrawCount`, though on a flag rather than
on byte count as specified. The behaviour is covered; the stated form is not.

**Recommendation:** accept D5 as a decision — it has been executed and its reasoning has held — while
recording that its verification gate was not met, and that `T0.2` remains genuinely outstanding rather
than quietly dropped.

---

## The other five

`dependencies.md`, `differential-testing.md`, `session-format.md`, `solution-layout.md` and
`translation-patterns.md` are also still Proposed. Three of them have been in continuous use for
eight packets — `translation-patterns.md` is cited in every one, `solution-layout.md` describes the
solution as built, and `dependencies.md` has been correct enough that its Markdig entry pre-answered a
question I had wrongly reopened.

They are working documents that have proven themselves in use. Marking them Accepted is
record-keeping rather than a decision, and can be done in one commit whenever you want it.

---

## Recommended action

1. **Accept D1** before wave 6 starts. Its remaining risk is wave-7 execution risk, which no further
   design work reduces.
2. **Accept D5**, with `T0.2` recorded as outstanding — not as a condition of acceptance, but so the
   gap stays visible.
3. Mark the other five Accepted as record-keeping.
4. Treat **`T0.2` (the differential harness)** as a real backlog item. It is the largest unbuilt piece
   of the verification strategy, and `Pi.Ai`'s 615 credential-gated cases are exactly the population
   it was designed to reach.
