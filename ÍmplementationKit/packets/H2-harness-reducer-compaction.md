# `H2` — Port the harness core types, reducer and compaction

**Wave:** harness (unblocks wave 6)  **Delegation fit:** B
**Depends on:** `H1` (harness session subsystem, delivered in `0bf2b36`)

---

## Why this is next

H1 landed the session subsystem and took `pi-agent-core` from 18% to 65% implementation completeness
and 16% to 42% test parity. The remaining harness is roughly 4,400 upstream LOC.

This packet takes the pieces that sit directly on session and are required by `agent-harness.ts`,
which is the last thing standing between here and wave 6:

- **`types.ts` / `events.ts` / `messages.ts` / `result.ts`** — harness-level shared contracts. Every
  remaining harness file imports them, so they must land first.
- **`reducer.ts`** — projects session state from the mutation log. Depends only on `session/types`,
  which H1 delivered.
- **`compaction/`** — context-window management and branch summarisation. Depends on `session/`,
  harness `types.ts` and `messages.ts`.

`skills.ts` and `agent-harness.ts` are deliberately held back for H3: `agent-harness.ts` imports
compaction, telemetry and types, so it wants all of this in place first.

## Source (read-only specification)

```
reference/pi/packages/agent/src/harness/
  types.ts                          315   harness-level shared contracts
  messages.ts                       168
  events.ts                         102
  result.ts                          63
  reducer.ts                        667   mutation log -> state projection
  compaction/compaction.ts          848
  compaction/branch-summarization.ts 280
  compaction/utils.ts               132
```

Upstream tests, 38 cases total:

```
reference/pi/packages/agent/test/harness/reducer.test.ts               16
reference/pi/packages/agent/test/harness/compaction.test.ts            22
reference/pi/packages/agent/test/harness/branch-summarization.test.ts   2
```

## Target (you may write only these paths)

```
src/Pi.AgentCore/Harness/**            excluding Harness/Session/**
tests/Pi.AgentCore.Tests/Harness/**    excluding Harness/Session/**
```

`Harness/Session/**` is **frozen**. It is covered by 59 passing tests and a conformance suite. If a
change there seems necessary, **stop and report it** rather than making it — that is a separate
decision, exactly as `Pi.Ai.Abstractions` was frozen after T2.1.

Also do not modify `Agent.cs`, `AgentLoop.cs` or `AgentTypes.cs`.

## Oracles

1. **Port all 38 upstream cases**, names preserved verbatim so a failure maps back to the upstream
   expectation.

2. **Reducer determinism.** The same mutation sequence must project the same state every time, and
   replaying a prefix then the remainder must equal replaying the whole. Assert this directly — it is
   the property the reducer exists to guarantee and upstream's cases only sample it.

3. **Compaction invariants.** Compaction rewrites history; a wrong port loses messages silently.
   Assert that compaction preserves the first-kept entry boundary, that token accounting before and
   after is consistent, and that a compacted transcript still round-trips through the session codec
   delivered in H1.

## Acceptance criteria

- [ ] `bash tools/test-parity.sh` shows `agent` above its floor and the completeness ratio risen from 65%
- [ ] `.test-parity` floor raised in the same commit — H1 delivered 97 cases but left the floor at 38
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes with **no skipped tests** (`.skip-budget` is 0)
- [ ] Every ported test that fails is listed in the PR with upstream expectation vs actual behaviour

## Known hazards

- **Compaction is lossy by design; that is what makes it dangerous.** It drops and summarises
  messages. An off-by-one in the kept-entry boundary silently deletes conversation and no test will
  notice unless you assert the boundary explicitly. Read `compaction.ts` carefully around
  `firstKeptEntryId`.

- **Branch summarisation writes back into the session.** It produces entries that H1's codec must be
  able to encode and decode. Verify the round-trip rather than assuming it.

- **The reducer is a fold, not a mutator.** Upstream projects state by folding the mutation log.
  Do not reimplement it as in-place mutation of a shared object even where that looks simpler in C# —
  the fold is what makes replay and branching correct.

- **`undefined` is not `null`.** `docs/translation-patterns.md §1`. Assert the exact key set on any
  round-trip, not just values.

- **JSON model.** `docs/translation-patterns.md §2.1`: strict typed model for formats we own and
  validate; `JsonNode` only for genuinely open-ended bags.

- **Where a hazard in this packet contradicts the TypeScript source, the source wins.** H1 contained a
  hazard requiring `[JsonExtensionData]` unknown-field preservation. Upstream parses strictly and
  keeps no passthrough bag, and the delivered implementation correctly followed upstream over the
  packet. Do the same here, and say so in the PR so the packet gets corrected.

## Out of scope

- `skills.ts`, `agent-harness.ts`, `telemetry.ts`, `env/`, `tools/`, `utils/` — H3.
- `Harness/Session/**` — frozen, see Target.
- Any NuGet dependency. If you believe one is needed, stop and report it.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.

## One standing instruction

**The tests ship in the same PR as the code.** H1 did this and the result was the best-verified packet
on the project. A packet that lands an implementation without its upstream suite is not complete,
regardless of what CI says.
