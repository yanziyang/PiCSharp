# Codex Task Packet — Template

One packet = one Codex task = one branch = one PR. Copy this, fill every field, delete nothing.
An unfilled field is how a task goes wrong.

---

## `T<wave>.<n>` — <short imperative title>

**Wave:** <n>  **Delegation fit:** A | B | C  **Depends on:** `T1.2`, `T2.1` (merged and on `main`)

### Objective

One or two sentences. What module is being ported, and to where. No background, no rationale.

### Source (read-only specification)

```
reference/pi/packages/ai/src/api/anthropic-messages.ts        1,391 LOC
reference/pi/packages/ai/src/api/anthropic-messages.lazy.ts
```

Read these files in full before writing any C#. Do not rely on search snippets.

### Target (you may write only these paths)

```
src/Pi.Ai/Api/AnthropicMessagesApi.cs
src/Pi.Ai/Api/AnthropicMessagesLazy.cs
tests/Pi.Ai.Tests/Api/AnthropicMessagesApiTests.cs
```

Writing outside these paths fails the task. If you believe a change is needed elsewhere, stop and
report it in the PR description instead of making it.

### Oracle — how this will be judged

Name at least one. A packet without a real oracle is not ready to delegate.

- **Ported tests:** `reference/pi/packages/ai/test/anthropic-messages.test.ts` →
  `tests/Pi.Ai.Tests/Api/AnthropicMessagesApiTests.cs`. All cases must pass unskipped, or each skip
  must carry a specific reason.
- **Golden fixtures:** replay `tests/fixtures/anthropic/*.sse` through both implementations; the
  normalised event sequence must match exactly.
- **Cross-runtime conformance:** (waves 4+) run against the TypeScript counterpart in CI.

### Acceptance criteria

- [ ] Ported tests pass; no test weakened, narrowed, or deleted
- [ ] `dotnet build` clean, zero warnings
- [ ] `dotnet format --verify-no-changes` passes
- [ ] Golden fixtures match byte-for-byte (or the deviation is documented and justified)
- [ ] No public member without a TS counterpart
- [ ] PR description lists every behaviour that could not be reproduced faithfully

### Known hazards

Call out in advance what you already know will bite. For example:

- The TS uses `partial-json` for incremental tool-argument parsing; there is no direct .NET
  equivalent. Use `Utf8JsonReader` in a resumable loop; do not pull in a new dependency.
- TS `undefined` and `null` are distinct on the wire here. Model as `JsonIgnoreCondition` plus a
  nullable type; do not collapse them.
- Streaming deltas may split a UTF-8 code point across chunk boundaries. The TS handles this in
  `transform-messages.ts`; reproduce it.

### Out of scope

State explicitly what a reasonable agent might otherwise wander into.

- Do not port the provider declarations in `providers/anthropic.ts` — that is `T2.15`.
- Do not touch auth; `T2.2` owns it.
- Do not refactor `Pi.Ai.Abstractions` even if the shape seems wrong. Report it.

### Conventions

Follow the repository root `AGENTS.md`. The TypeScript source is the specification; where it and
the conventions conflict, the source wins and you say so in the PR.

---

## Notes on writing good packets

- **Rate honestly.** A packet you rate **A** but that actually needs judgement will come back as a
  confident, wrong PR. When unsure, rate it **B** and name the hazard.
- **Never batch a C.** Design work goes to a person. Delegate the typing-out only after the
  decision exists in `docs/`.
- **Disjoint targets.** Before dispatching a parallel batch, diff every packet's target paths
  against every other in-flight packet. Overlap is the main cause of wasted cloud runs.
- **Depend on merged work, not in-flight work.** "Depends on T2.1" means T2.1 is on `main`.
- **One oracle minimum.** If you cannot name how the task will be verified, the task is not ready.
- **Cap work in flight.** Review throughput is the bottleneck, not generation. More than about five
  unreviewed PRs and quality collapses — you start rubber-stamping.
