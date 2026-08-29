# R1.1 — Agent test backfill: findings

**Run:** 2026-08-29 · **Scope:** `reference/pi/packages/agent/test/agent-loop.test.ts`
**Result:** `Pi.AgentCore.Tests` 10 → 24 cases. 22 pass, 2 skipped against a missing API.

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
| `should throw when context has no messages` | Targets `agentLoopContinue`; I called `AgentLoop.RunAsync` | port error → **skipped**, API missing |
| `should continue from existing context without emitting user message events` | Same: targets `agentLoopContinue` | port error → **skipped**, API missing |
| `should inject queued messages after all tool calls complete` | I asserted `getSteeringMessages` is not *invoked* before tools finish. Upstream asserts both tools *execute* before steering is injected — a weaker and different claim. | port error → **corrected**, now passes |

## 3. Genuine API gap: `agentLoopContinue`

Upstream `agent-loop.ts` exports two loop entry points:

```ts
export function agentLoop(...)          // ported as AgentLoop.RunAsync
export function agentLoopContinue(...)  // NOT ported
```

`agentLoopContinue` resumes from an existing context without re-emitting user-message events, and
throws `"Cannot continue: no messages in context"` when the transcript is empty.

`Agent.ContinueAsync` exists but is the higher-level `Agent` wrapper, not the loop entry point, and
does not satisfy these tests. Two cases are skipped against this gap (`.skip-budget` raised to 2).

**Recommended:** port `agentLoopContinue` as `AgentLoop.ContinueAsync` and un-skip both cases. Small
and well-bounded — it is the same loop with a different entry condition.

## 4. What passes

The 22 passing cases confirm the ported loop is behaviourally faithful on the semantics most likely
to break silently:

- Per-tool `ExecutionMode.Sequential` forces serial scheduling even under a parallel config, and a
  single sequential tool serialises a mixed batch
- Parallel batches genuinely overlap (asserted by a barrier that deadlocks under serialisation)
- `ShouldStopAfterTurn` beats a non-empty follow-up queue
- Termination requires *every* tool result to set `Terminate`; mixed batches continue
- A blocked-and-terminating call stops the loop, but only when it is the whole batch
- `TransformContext` runs before `ConvertToLlm`
- `PrepareNextTurn` model replacement applies to the following turn, not the current one
- `SessionId` reaches the stream function options

## 5. Next

1. Port `agentLoopContinue`, un-skip the two cases, return `.skip-budget` to 0.
2. Port `agent.test.ts` (22 cases) — state, subscribers, queues, reset and concurrency guards.
3. Decide whether the agent harness (10,065 LOC) is in scope. Until it is, the agent package cannot
   exceed roughly 20% test parity, and `Pi.CodingAgent` has no session, compaction or skills layer
   to build on.
