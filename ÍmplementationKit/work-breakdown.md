# PiCSharp — Work Breakdown for Delegated Porting

Upstream: `earendil-works/pi` @ **v0.84.4** (frozen). 123,629 LOC production, 115,921 LOC tests.
All line counts below are measured from the pinned tree, production code only (tests excluded).

**Delegation fit** ratings mean:
- **A** — mechanical translation with a strong test oracle. Delegate freely, review normally.
- **B** — translation with semantic risk. Delegate with a differential test in the packet; review closely.
- **C** — design or judgement required. Do not delegate the decision; delegate only the typing-out afterwards.

---

## Wave −1 · Decisions that gate everything (do not delegate)

| id | Decision | Why it blocks | Output |
|----|----------|---------------|--------|
| D1 | **Shape of the C# extension API** | ~56k LOC of `pi-coding-agent` is shaped by it, and it determines whether the 85 bundled extensions can be ported mechanically | `docs/extension-api.md` |
| D2 | Solution and project graph | Every task packet names target paths | `docs/solution-layout.md` |
| D3 | Differential-testing strategy | It is the only oracle for waves 2–6 | `docs/differential-testing.md` |
| D4 | Session file format: byte-compatible with Pi, or new? | Decides whether users can open existing sessions | `docs/session-format.md` |
| D5 | Terminal UI: port `pi-tui`, or build on Spectre.Console? | 17,000 LOC either way; changes wave 5 entirely | `docs/tui-strategy.md` |

**On D1 — the highest-leverage decision in the project.** Mirror Pi's `ExtensionAPI` as closely as
C# allows: same event names, same registration verbs (`On`, `RegisterTool`, `RegisterCommand`,
`RegisterShortcut`, `RegisterFlag`, `RegisterProvider`), same context surface. You cannot run the
TypeScript extensions regardless, but a near-1:1 mirror turns extension porting into a *mechanical,
delegatable* task (wave 7) instead of 85 bespoke rewrites. An idiomatic-C# redesign forfeits that,
and with it most of what is recoverable from the ecosystem.

Where C# cannot follow, decide the pattern once and write it down:
in-place mutation of `event.input` → a mutable `ToolCallEventArgs` with settable properties;
callback-taking methods (`NewSession({ setup, withSession })`) → `Func<>`/`Action<>` parameters;
`AbortSignal` → `CancellationToken`.

---

## Wave 0 · Scaffold and oracles (human-led, 2 tasks)

| id | Task | Fit | Notes |
|----|------|-----|-------|
| T0.1 | Solution scaffold: projects, CI, analyzers, `Directory.Build.props`, `reference/pi` pinned at v0.84.4 | C | Do this by hand. Every later packet depends on the paths being right. |
| T0.2 | Differential harness: HTTP record/replay fixtures, golden-buffer capture from the TS build | C | Build **before** wave 2. Without it, waves 2–6 have no oracle. |

---

## Wave 1 · Foundations (fully parallel)

| id | Source | Target | LOC | Fit |
|----|--------|--------|-----|-----|
| T1.2 | `packages/protocol/src` | `Pi.Protocol` | 1,236 | A |
| T1.3 | `packages/ai/src/providers/faux.ts` | `Pi.Ai.Testing` | 708 | A |
| T1.1 | `packages/telemetry/src` | `Pi.Telemetry` | 935 | **C** — see below |

**T1.1 was mis-rated A and is actually C.** `telemetry/src/index.ts` is 29 type-only declarations
against 2 runtime functions, with 11 uses of conditional and mapped types
(`InferStartAttributes<T>`, `InferRequiredAndOptionalAttributes<T>`). Those derive attribute types
from a schema value at compile time. C# generics cannot express that, so someone must first *decide*
how schema-derived attribute typing is represented — source generator, analyzer, or runtime
validation with weaker static guarantees. That decision is a design task, not a translation.

**Do T1.2 first.** Protocol is 1,236 LOC with 53 typebox schema definitions, zero conditional or
mapped types, and real upstream tests. It is byte-verifiable, and merging it turns on the
cross-runtime conformance oracle (`differential-testing.md` §4), which is the cheapest defect
detection available on the whole project.

Run these three first regardless of schedule pressure: they are small, independent, and they
calibrate how much review each Codex PR actually needs before you commit to the large waves.

**T1.3 is disproportionately valuable.** The faux provider is Pi's deterministic test double; the
upstream coding-agent suite is built on it. Porting it early gives every later agent-loop task a
real oracle.

---

## Wave 2 · `Pi.Ai` — 23,668 LOC

The 47 providers are **not** 47 integrations. They are thin declarations over ~11 shared wire
protocols. `cerebras.ts` is 16 lines. Slice by *protocol*, not by provider.

| id | Source | LOC | Fit | Notes |
|----|--------|-----|-----|-------|
| T2.1 | `src/models.ts`, registry, root types | ~4,000 | B | Sets the pattern for the whole package. Review hardest. |
| T2.2 | `src/auth/` | 3,589 | B | OAuth device flows, credential chains, per-provider quirks. Highest defect risk in the package. |
| T2.3 | `src/utils/` | 1,899 | A | |
| T2.4 | `api/openai-completions.ts` | 1,707 | B | Most-used path. Golden-file the SSE stream. |
| T2.5 | `api/openai-codex-responses.ts` | 1,650 | B | |
| T2.6 | `api/anthropic-messages.ts` | 1,391 | B | |
| T2.7 | `api/bedrock-converse-stream.ts` | 1,325 | B | AWS SigV4; use the AWS SDK for .NET. |
| T2.8 | `api/openai-responses{,-shared}.ts` | 1,168 | B | |
| T2.9 | `api/google-generative-ai.ts` + `google-shared.ts` | 978 | B | |
| T2.10 | `api/mistral-conversations.ts` | 936 | B | |
| T2.11 | `api/google-vertex.ts` | 598 | B | |
| T2.12 | `api/pi-messages.ts` | 433 | B | |
| T2.13 | `api/azure-openai-responses.ts`, `openrouter-images.ts`, `cloudflare*.ts` | ~800 | B | |
| T2.14 | `api/lazy.ts`, `transform-messages.ts`, `constrained-sampling.ts`, `simple-options.ts` | ~750 | A | |
| T2.15 | **All 47 `providers/*.ts` declarations** | 2,376 | A | One task. Each is ~15–40 lines of config once T2.4–T2.13 exist. |
| T2.16 | `models.generated.ts` catalogue | — | A | Port the *generator*, not the output. See Pi's `AGENTS.md`. |

T2.4–T2.13 are mutually independent and write to disjoint files — the best parallel batch in the project.

---

## Wave 3 · `Pi.AgentCore` — 12,640 LOC

Depends on wave 2. Semantic risk is high: streaming, tool-call batching and compaction semantics
must match exactly or ported extensions misbehave in ways unit tests miss.

| id | Scope | Fit |
|----|-------|-----|
| T3.1 | Agent loop and turn orchestration | B |
| T3.2 | Tool calling: schema, dispatch, result normalisation | B |
| T3.3 | State management, message model, usage accounting | B |
| T3.4 | Compaction | B |

Every packet in this wave must require a differential test against the TS implementation driven by
the faux provider (T1.3).

---

## Wave 4 · Protocol surface — 3,524 LOC

| id | Source | Target | LOC | Fit |
|----|--------|--------|-----|-----|
| T4.1 | `packages/client/src` | `Pi.Client` | 1,225 | A |
| T4.2 | `packages/server/src` | `Pi.Server` | 2,299 | B |

**Free conformance oracle:** because `pi-protocol` is a real wire protocol, `Pi.Client` can be tested
against the *unmodified TypeScript* `pi-server`, and `Pi.Server` against the TypeScript `pi-client`,
before either side is finished. Do this in CI. It catches wire-level drift that unit tests cannot.

---

## Wave 5 · `Pi.Tui` — 17,000 LOC (independent stream)

`pi-tui` has no internal dependencies — it is built first upstream. Start it in parallel with wave 2
under a dedicated owner; it is the least predictable work in the project.

| id | Scope | LOC | Fit |
|----|-------|-----|-----|
| T5.1 | Layout engine, `layout-node`, container model | ~3,000 | B |
| T5.2 | Differential renderer and terminal writer | ~3,500 | C |
| T5.3 | Editor component, keybindings, kill-ring, undo stack, word navigation | ~4,000 | B |
| T5.4 | Grapheme segmentation, East-Asian width, ANSI segment extraction | ~2,000 | B |
| T5.5 | Terminal images: Kitty and iTerm protocols, sizing, GIF/JPEG/PNG probing | ~2,500 | C |
| T5.6 | Autocomplete, fuzzy matching, alt-screen search | ~2,000 | A |

Gate this wave on golden-buffer tests: capture the TS renderer's exact output bytes for a fixed
scenario corpus, then assert byte-identical output from C#. Nothing else will catch cursor and
wrapping defects.

---

## Wave 6 · `Pi.CodingAgent` — 60,960 LOC

The largest package, and the one containing the redesign.

| id | Scope | LOC | Fit |
|----|-------|-----|-----|
| T6.1 | `core/tools/` — bash, powershell, read, write, edit, grep, find, ls | 4,293 | A |
| T6.2 | `core/*.ts` root: session manager, model registry, settings, system prompt, skills, prompt templates, trust | 18,774 | B |
| T6.3 | `core/compaction/` | 1,557 | B |
| T6.4 | `core/export-html/` | 746 | A |
| T6.5 | `utils/` | 3,647 | A |
| T6.6 | `cli/` — argument parsing, entry point | 1,852 | A |
| T6.7 | `modes/` — print, json-event, rpc | ~930 | A |
| T6.8 | `modes/interactive/` — 43 UI components | 18,302 | B |
| T6.9 | **Extension host — new design per D1** | 4,121 ref | C |
| T6.10 | `client/`, `server/` glue | 697 | A |

T6.2 and T6.8 are too large for single packets. Split each into 6–10 sub-tasks along file
boundaries once T0.1 fixes the target layout.

---

## Wave 7 · Extension ecosystem port

Only viable if D1 chose a mirrored API.

| id | Scope | Count | Fit |
|----|-------|-------|-----|
| T7.1–T7.n | `packages/coding-agent/examples/extensions/*` | 85 files | A–B |

Batch 2–4 extensions per packet. The 41% that render TUI components depend on wave 5 and should be
scheduled last. Treat this wave as the measure of whether the mirrored-API bet paid off: if a
typical extension does not port in a single packet without redesign, D1 was wrong and the remaining
extensions are not worth porting individually.

---

## Sequencing summary

```
Wave −1  decisions          ── gates everything, human only
Wave  0  scaffold + oracles ── human led
Wave  1  foundations        ── 3 parallel tasks, calibration
Wave  2  Pi.Ai              ──┐ 13 tasks, mostly parallel
Wave  5  Pi.Tui             ──┘ independent stream, runs alongside
Wave  3  Pi.AgentCore       ── after wave 2
Wave  4  protocol surface   ── after wave 1; unlocks cross-runtime conformance
Wave  6  Pi.CodingAgent     ── after 3, 4, 5
Wave  7  extension port     ── after 6
```

## Standing rules for every packet

1. Name exact source paths under `reference/pi/` and exact target paths. No prose scoping.
2. Name the oracle: which ported test file, which golden fixture, which differential run.
3. Ensure target paths are disjoint from every other in-flight packet.
4. Never batch a **C**-rated item with anything else.
5. Cap review debt: do not start a new wave while more than ~5 PRs sit unreviewed. Review throughput,
   not generation throughput, is the binding constraint on this project.
