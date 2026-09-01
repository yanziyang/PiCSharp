# `R4` — Test the `Pi.Ai` transport and resilience paths

**Wave:** remediation  **Delegation fit:** A
**Depends on:** `Pi.Ai` implementation (wave 2, already shipped)
**Plan reference:** remediation of the wave-2 test gap.
**Status:** ✅ Delivered in `a5632ad` (tests) and `e09ee52` (fixes). Pi.Ai parity 13% → **32% of
portable** (99 → 244 cases). Full suite 1,472 tests, 0 failed, 0 skipped, three runs.

All 134 upstream case names ported with **zero missing**, plus 11 branch-coverage cases.

**The packet's premise was right: `Pi.Ai` had diverged from upstream in nine places**, and
`tests/Pi.Ai.Tests/R4Findings.md` documents each with the upstream expectation against the actual C#
behaviour. They were fixed in a separate commit, and no case was skipped or weakened. Spot-checked
two:

- **`cloudflare-gateway-binding.ts` had no C# equivalent at all.** 176 lines of upstream
  functionality, absent since wave 2, found by a ported test.
- The OpenAI Responses SSE loop never terminated on `[DONE]` or on a terminal response event, so a
  stream whose body stayed open would hang. The fix is nine lines and the upstream case
  (`openai-codex-stream.test.ts:212`) is real.

Hazard 1 was respected: `tests/Pi.Ai.Tests` contains no `SetEnvironmentVariable` call — environment
handling is injected, so no process-global race was introduced.

**One packet defect, correctly caught by the delivery.** The target paths froze everything except
`tests/Pi.Ai.Tests/**`, while the acceptance criteria required raising `.test-parity`. Those
contradict. The delivery declined the edit and **said so in `R4Findings.md`** rather than silently
breaching the freeze — the disclosure behaviour every packet since H1 has asked for. Floors raised
separately here.

---

## Why this exists, and a correction that shapes it

`Pi.Ai` is 14,824 lines with 99 ported test cases. It has been the least-verified package in the
project since wave 2, and it sits directly beneath wave 6.

An earlier reading of the gap said `stream.test.ts` (184 cases), `abort.test.ts` (39) and five other
large files were the risk-concentrated core and should be ported first. **That was wrong, and the
error is worth stating because it changes what this packet is.** Every one of those files is gated on
live provider credentials:

```ts
describe.skipIf(!process.env.GEMINI_API_KEY)("Gemini Provider (gemini-2.5-flash)", () => {
  it.skipIf(!isVertexConfigured)("should handle streaming", { retry: 3 }, async () => { ... });
```

They hit real endpoints with retries. Upstream skips them itself when keys are absent. They are **not
portable as offline tests**, and a packet asking for them would have been impossible to execute.

The real split, now measured:

| | Cases |
|---|---|
| Credential-gated live E2E | **615** |
| Offline-portable | **745** |
| Ported in C# | 99 |

So `Pi.Ai` is at **13% of what is reachable**, not 7% of everything. `tools/test-parity.sh` now
reports these separately — the old denominator had an unreachable ceiling of 55%, which is a good way
to make a number stop being read.

The offline 745 are a long tail: the largest file is 47 cases. There is no single high-value target,
so this packet takes a **coherent theme** instead.

## Scope — 134 cases: what happens between the provider and the wire

```
reference/pi/packages/ai/test/
  openai-codex-stream.test.ts              24   offline streaming, HTTP stubbed
  overflow.test.ts                         17
  error-body.test.ts                       16
  azure-openai-base-url.test.ts            16
  retry.test.ts                            15
  cloudflare-gateway-binding.test.ts       14
  bedrock-endpoint-resolution.test.ts       9
  google-vertex-api-key-resolution.test.ts  9
  provider-retry.test.ts                    5
  provider-error-body-regression.test.ts    5
  google-shared-retry.test.ts               3
  provider-error-body-passthrough.test.ts   1
```

Endpoint resolution, retry classification, error-body handling, context overflow, and the one
streaming suite that runs without credentials. Every provider shares these paths, and a divergence
here is silent — a misclassified retry or a swallowed error body does not throw, it just behaves
differently under load.

## Target (you may write only these paths)

```
tests/Pi.Ai.Tests/**
```

`src/` is **frozen for this packet.** If a ported test fails, that is the finding: report it in the PR
with the upstream expectation and the actual C# behaviour, and fix it in a **separate commit**.

## Hazards

### 1. Environment mutation would import a bug this project has already had twice

`azure-openai-base-url.test.ts` has 19 references to `process.env`, `bedrock-endpoint-resolution.test.ts`
has 18, `google-vertex-api-key-resolution.test.ts` has 5. Endpoint resolution is environment-driven by
design.

`tests/Pi.Ai.Tests` today mutates **no** environment variables and has **no** parallelization policy,
so xunit runs its classes in parallel. Porting these naively — set the variable, run, restore —
introduces a process-global race across parallel classes. That exact bug hit `Pi.Tui` twice and cost
two debugging cycles; `TestAssembly.cs` there carries the explanation.

**Prefer injection.** This project already has the pattern: `ProcessTerminal` takes
`environment: IDictionary<string, string?>` and `ResolveEscapeTimeoutMs` takes it as a parameter, so
its tests never touch the process environment. Do the same here if `Pi.Ai`'s resolution paths can
accept it — and note that this may require a `src/` change, which is the one exception to the freeze
above: make it in a separate commit and say so.

If the source genuinely reads process globals and injection is not faithful, then serialise the
assembly as `Pi.Tui.Tests` does — **and write the reason next to the attribute.** Do not add an
unexplained `ParallelMode.None`.

### 2. Retry classification is string matching, and the strings are the specification

`retry.test.ts` is a pure unit test — no HTTP. It asserts `isRetryableAssistantError` against literal
provider error messages: OpenAI's "You can retry your request…", Bedrock's JSON body, NVIDIA NIM's
"ResourceExhausted: Worker local total request limit reached", a Bun socket-closed message, a wrapped
DNS `ENOTFOUND`.

**Port those strings verbatim.** They are not examples, they are the contract — each one is a real
provider behaviour someone hit. A paraphrase that still passes your C# is worthless.

### 3. HTTP stubbing has a precedent in this project

`Pi.Ai.Tests` already fakes transport with `HttpMessageHandler` in six places. Use that, not a live
socket or a local server. `openai-codex-stream.test.ts` is the one streaming suite here, and it
exercises `Transport/SseReader.cs` — the SSE parser — which is worth real attention: a stream that
mis-splits an event boundary produces subtly wrong content, not an exception.

### 4. Do not test the implementation back to itself

`R3`'s caveat, and it applies harder here because `src/` is frozen. Derive every expectation from the
TypeScript. A test written by reading `ProviderHttpClient.cs` and asserting what it currently does
certifies whatever bug is in it. Where the TypeScript is genuinely ambiguous, say so in the PR rather
than guessing.

### 5. Standing rules

- **`undefined` is not `null`** — `translation-patterns.md §1`. Error bodies and endpoint overrides
  both have meaningful absent states.
- Where a test must observe asynchronous work, wait on the **condition**, not a fixed delay.
- **Where a hazard here contradicts the TypeScript source, the source wins** — and **say so in the PR**.

## Acceptance criteria

- [ ] All 134 cases ported and passing, names preserved verbatim
- [ ] Environment handling is injected, or serialised with a written reason — not raced
- [ ] Retry classification strings ported verbatim from upstream
- [ ] `Transport/SseReader.cs` exercised by the `openai-codex-stream` cases
- [ ] Any failing ported test reported as a finding, and fixed in a separate commit
- [ ] `bash tools/test-parity.sh` shows `ai` risen from 13% of portable
- [ ] **`.test-parity` floors raised in the same commit**
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes; **run it at least three times**
- [ ] `.skip-budget` stays **0**
- [ ] Anything not delivered, or any instruction overridden, is named in the PR

## Out of scope

- **The 615 credential-gated cases.** They are not portable offline. If `Pi.Ai` ever gets a live
  conformance run, that is `T0.2`'s differential harness, not this.
- The rest of the offline 745 — provider-specific edge cases, later packets (`R5`, `R6`).
- Any TUI work; `T5.7` and `T5.8` are running separately.
- Any NuGet dependency.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.

## One standing instruction

**The tests ship in the same PR as the code**, and **anything not delivered must be named in the PR.**
This packet freezes `src/`, so the temptation is the opposite of the usual one: when a ported test
fails, the cheap move is to soften the assertion until it passes. Don't. A failing ported test is the
most valuable thing this packet can produce — it is the first evidence in the project that `Pi.Ai`
diverges from upstream, and `Pi.Ai` is what wave 6 will be built on.
