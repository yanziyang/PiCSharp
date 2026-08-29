# R1.1 — Agent test backfill: findings

**Run:** 2026-08-29 · **Scope:** `agent-loop.test.ts` and `agent.test.ts`
**Result:** `Pi.AgentCore.Tests` 10 → 40 cases, all passing, no skips. Suite total 294 → 324.

---

## 1. The headline finding is not about tests

Sizing R1.1 revealed that `pi-agent-core` is **not fully ported**, which the wave 1–5 review did not
catch because it assessed code *quality* rather than *completeness*.

| | Upstream LOC | Ported LOC | |
|---|---|---|---|
| `packages/agent/src` (non-harness) | ~2,575 | 2,334 | ported |
| `packages/agent/src/harness` | **10,065** | **0** | **not ported** |

The harness is the missing 80%: session storage and JSONL codec, compaction and branch
summarisation, the reducer, skills, prompt templates, system prompt assembly, truncation, the
Node environment adapter, and the search subsystem.

**This reframes R1.** Of the 226 upstream agent cases, roughly 180 target the harness and cannot be
ported because the code does not exist. The achievable target today is the ~46 cases covering
`agent-loop.ts`, `agent.ts` and `proxy.ts`.

Because C# is typically 1.2–1.5× more verbose than TypeScript, LOC parity at 100% would itself
indicate under-porting. On that basis the completeness picture across the project is:

| Package | Upstream | Ported | Ratio | Read |
|---|---|---|---|---|
| protocol | 1,236 | 3,493 | 282% | complete |
| client | 1,225 | 1,533 | 125% | complete |
| server | 2,299 | 2,086 | 90% | close to complete |
| telemetry | 935 | 741 | 79% | mostly complete |
| ai | 23,668 | 16,989 | 71% | partial |
| **agent** | 12,640 | 2,334 | **18%** | **core only, no harness** |
| **tui** | 17,000 | 1,723 | **10%** | **layout + renderer only** |

`tools/test-parity.sh` now reports this alongside test counts, so it cannot go unnoticed again.

## 2. Three ported tests failed. All three were my porting errors.

Worth recording, because the packet rule is "a failing ported test is a finding" — and the first
discipline that rule requires is checking the port before blaming the implementation.

| Upstream case | Why it failed | Verdict |
|---|---|---|
| `should throw when context has no messages` | Targets `agentLoopContinue`; I called `AgentLoop.RunAsync` | port error → **corrected** to `StartContinuation`, now passes |
| `should continue from existing context without emitting user message events` | Same: targets `agentLoopContinue` | port error → **corrected** to `RunContinuationAsync`, now passes |
| `should inject queued messages after all tool calls complete` | I asserted `getSteeringMessages` is not *invoked* before tools finish. Upstream asserts both tools *execute* before steering is injected — a weaker and different claim. | port error → **corrected**, now passes |

## 3. The "missing API" finding was wrong — corrected

An earlier revision of this document claimed `agentLoopContinue` was not ported, and two tests were
skipped against that claim. **That was incorrect.** The C# port maps all four upstream entry points
faithfully:

| Upstream (`agent-loop.ts`) | C# (`AgentLoop`) |
|---|---|
| `agentLoop` | `Start` |
| `agentLoopContinue` | `StartContinuation` |
| `runAgentLoop` | `RunAsync` |
| `runAgentLoopContinue` | `RunContinuationAsync` |

Both guards are present with upstream's exact messages: `"Cannot continue: no messages in context"`
and `"Cannot continue from message role: assistant"`.

The error came from grepping for `RunAsync|ContinueAsync` and concluding from the absence of
`ContinueAsync` that the API was missing. `RunContinuationAsync` does not match that pattern. This is
the second time in this review that a naive grep produced a false finding — the first being
`.Result` matches that turned out to be domain record properties rather than blocking calls.

**Both cases are now un-skipped and pass**, plus one added case covering the assistant-tail guard,
which upstream implements but does not test. `.skip-budget` is back to 0.

**Process note.** Grep is adequate for locating candidates and inadequate for concluding absence.
Confirm any "X is missing" claim by listing the actual public surface before recording it.

## 4. What passes

40 cases now cover the loop and the Agent wrapper. The behaviours confirmed faithful are the ones
most likely to break silently:

**Loop scheduling and termination**
- Per-tool `ExecutionMode.Sequential` forces serial scheduling even under a parallel config, and a
  single sequential tool serialises a mixed batch
- Parallel batches genuinely overlap (asserted by a barrier that deadlocks under serialisation)
- `ShouldStopAfterTurn` beats a non-empty follow-up queue
- Termination requires *every* tool result to set `Terminate`; mixed batches continue
- A blocked-and-terminating call stops the loop, but only when it is the whole batch
- `TransformContext` runs before `ConvertToLlm`; `PrepareNextTurn` applies to the following turn
- Both continuation guards throw upstream's exact messages

**Agent lifecycle**
- Async subscribers are awaited before `PromptAsync` resolves, and by `WaitForIdleAsync`
- `Reset` during a run throws and leaves both the streaming flag and transcript intact
- `PromptAsync` and `ContinueAsync` reject re-entry while streaming
- `ContinueAsync` drains the follow-up queue
- `SessionId` reaches the stream options from both `AgentLoopConfig` and `AgentOptions`

## 4b. A second round of porting errors

Four more ported tests failed on first run, and again all four were mine, not the implementation's:

| Cause | Fix |
|---|---|
| Four cases constructed a bare `new Agent()`. Upstream always passes `streamFn: unusedStreamFunction`; its constructor resolves `streamFn ?? getDefaultStreamFn()` and throws when neither exists. C# behaves identically. | Pass a stream function, as upstream does |
| `Should_handle_abort_controller` invented an elaborate abort-mid-run scenario with a race that passed in Debug and failed in Release. Upstream asserts only that `abort()` does not throw when idle. | Replaced with the faithful one-line assertion |

Running total for this backfill: **seven ported tests failed, seven were porting errors, zero were
implementation defects.** That is a meaningful result in itself — the ported agent core is holding up
well under a faithful suite.

## 5. Next

1. ~~Port `agentLoopContinue`~~ — already ported; the two cases are un-skipped and pass.
2. ~~Port `agent.test.ts`~~ — done. State, subscribers, queues, reset and concurrency guards ported.
3. Decide whether the agent harness (10,065 LOC) is in scope. Until it is, the agent package cannot
   exceed roughly 20% test parity, and `Pi.CodingAgent` has no session, compaction or skills layer
   to build on.
