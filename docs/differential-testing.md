# D3 — Differential Testing Strategy

**Status:** Proposed — sign off before wave 2
**Gates:** waves 2–6. Without this, generated C# cannot be verified at the rate it is produced.

---

## The problem

The port will generate on the order of 120,000 lines of C#, much of it from delegated tasks. Nobody
can establish behavioural equivalence by reading it. Review catches *design* errors; it does not
catch a stream parser that drops a delta on a chunk boundary, or a renderer that emits one cursor
move too few.

Equivalence has to be **machine-checked against the TypeScript implementation**. Five oracles, in
descending order of cost-effectiveness.

---

## Oracle 1 — Ported upstream tests (primary)

Upstream ships 115,921 lines of tests across 472 files. This is the specification, and porting it is
a deliverable, not overhead.

- Ported **with** the implementation, in the same PR. Never deferred.
- `reference/pi/packages/<pkg>/test/*.test.ts` → `tests/Pi.<Pkg>.Tests/`.
- Test names preserved verbatim so a failure maps to an upstream case.
- Never weaken, narrow, or delete. Unportable cases are `[Fact(Skip = "specific reason")]` and
  listed in the PR.

**Track skip count as a project metric.** A rising skip count is the earliest signal that packets
are being marked done while the hard half is quietly dropped.

---

## Oracle 2 — Faux provider (deterministic agent-loop testing)

`reference/pi/packages/ai/src/providers/faux.ts` (708 LOC) is Pi's scripted provider double; the
upstream coding-agent suite is built on it. Ported as `Pi.Ai.Testing` in **T1.3**, before anything
that depends on it.

Enables deterministic tests for turn orchestration, tool-call batching, compaction triggers,
streaming assembly, and cancellation — with no network, no keys, and no token spend.

`AGENTS.md` forbids real provider calls in tests. This is what makes that rule practical.

---

## Oracle 3 — HTTP record/replay (the `Pi.Ai` wave)

The eleven protocol implementations in `api/` are where wire-level defects hide.

**Record once, from TypeScript.** `tools/record-fixtures/` drives the upstream build against each
real provider and captures raw exchanges — request bodies, headers (redacted), and the complete SSE
byte stream — into `tests/fixtures/<provider>/*.http`.

**Replay against both.** Each fixture is fed to the TS implementation and the C# one; the
**normalised event sequence** must match exactly: content deltas, tool-call assembly, thinking
blocks, usage accounting, stop reasons, and error mapping.

Fixtures must include the ugly cases, which is where ports break:

- A UTF-8 code point split across two SSE chunks
- Tool arguments streamed as partial JSON across many deltas (upstream uses `partial-json`)
- Interleaved thinking and text blocks
- Mid-stream provider errors, and truncation without a terminal event
- Cache-hit and cache-write usage variants

Record fixtures **once** and commit them. Re-recording against live providers on every CI run
reintroduces nondeterminism and cost.

---

## Oracle 4 — Cross-runtime protocol conformance (free, and start early)

`pi-protocol` is a real versioned wire format, so both sides are independently testable against the
TypeScript counterpart **before either C# side is complete**:

- `Pi.Client` ⇄ TypeScript `pi-server`
- TypeScript `pi-client` ⇄ `Pi.Server`

This is the highest defect-per-effort oracle in the project: it needs no fixture authoring, and it
catches CBOR framing, field-name, and null-handling drift that unit tests on either side will pass.

Runs in `tests/Pi.Conformance.Tests` with Node on the CI runner. Node never ships in the product.

Wire it up as soon as `Pi.Protocol` (T1.2) merges — do not wait for wave 4.

---

## Oracle 5 — Golden terminal buffers (the `Pi.Tui` wave)

`Pi.Tui` produces bytes. Assert on the bytes.

Capture the TypeScript renderer's exact output for a fixed scenario corpus into
`tests/fixtures/tui/*.golden`, then assert byte-identical output from C#.

Corpus must cover: wrapping at width boundaries; CJK and emoji width; combining marks and ZWJ
sequences; ANSI style runs spanning wraps; incremental redraw after a single-cell change; scroll
regions; alternate-screen enter/exit; Kitty and iTerm image placement.

Byte-identity is the point. A "looks right" screenshot test will pass while the diff renderer emits
a redundant full repaint on every keystroke.

---

## Normalisation rules

Differential comparison needs a canonical form or it drowns in false positives. Normalise before
comparing, and normalise **identically** on both sides:

- Timestamps → fixed sentinel
- Generated ids (session, entry, tool-call) → deterministic counter
- Absolute paths → repo-relative
- JSON key order → sorted, **except** where wire order is semantically significant (SSE event
  ordering, session JSONL entry order — never sort those)
- Floating point → round to a documented precision

Normalisers live in `tests/Pi.TestKit/` and are shared. Two normalisers that disagree produce
failures nobody can diagnose.

---

## CI wiring

| Stage | Runs | Blocking |
|---|---|---|
| Build | `dotnet build`, zero warnings | yes |
| Format | `dotnet format --verify-no-changes` | yes |
| Unit | `dotnet test`, excluding `Category=E2E` | yes |
| Conformance | `Pi.Conformance.Tests` (needs Node) | yes, from T1.2 |
| Fixtures | replay all recorded HTTP fixtures | yes, from T2.4 |
| Golden | TUI buffer comparison | yes, from T5.2 |
| Skip audit | report `[Fact(Skip)]` count, fail on increase without a waiver | yes |
| E2E | live provider calls | nightly, non-blocking |

---

## Sequencing

1. **T0.2 builds the harness** — normalisers, fixture recorder, replay runner, golden capture —
   before wave 2 opens.
2. **T1.3 ports the faux provider** early.
3. **Conformance goes live** as soon as `Pi.Protocol` merges.
4. Every wave-2+ packet names its oracle. A packet without one is not ready to dispatch.

The harness is roughly two weeks of work for one engineer and it is the difference between a
reviewable port and an unfalsifiable one. Do not defer it to "once we have something to test".
