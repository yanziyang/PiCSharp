# `R3` — Test the ported `Proxy` and `SystemPrompt`

**Wave:** remediation  **Delegation fit:** A
**Depends on:** `R2` implementation, delivered in `831da91`
**Status:** ✅ Delivered in `162f717`. 670 lines of tests, `src/` untouched as required. 14 cases against
upstream's 4 — 3 for `SystemPrompt` and 11 for `Proxy`, the extra 10 being the branch coverage this packet
asked for. Agent parity 103% → 109%. `pi-agent-core` is complete.

---

## Why this exists

`R2` delivered `Proxy.cs` (1,128 lines) and `Harness/SystemPrompt.cs` (84 lines) — and **no tests**.
The agent test count was 234 before that commit and 234 after. None of the 4 upstream cases were
ported, and the commit message did not mention the omission.

CI passed, which is the part worth understanding: the test-parity gate enforced a *floor*, and
shipping implementation with no tests stays above a floor indefinitely. That hole is now closed —
`tools/test-parity.sh` fails when implementation grows by more than 200 lines with no new tests — but
the gap it let through is still here, and this packet closes it.

**Do not modify the implementation** unless a ported test proves it wrong. This is a test packet.

## Source (read-only specification)

```
reference/pi/packages/agent/src/proxy.ts                  370 LOC
reference/pi/packages/agent/src/harness/system-prompt.ts   34 LOC
```

Upstream tests, 4 cases:

```
reference/pi/packages/agent/test/proxy.test.ts                  1
reference/pi/packages/agent/test/harness/system-prompt.test.ts  3
```

## Target (you may write only these paths)

```
tests/Pi.AgentCore.Tests/**
```

`src/**` is **frozen for this packet**. If a ported test fails, that is the finding — report it in
the PR with the upstream expectation and the actual C# behaviour, and only then fix the
implementation, in a clearly separate commit.

## Objective

1. Port all 4 upstream cases, names preserved verbatim.
2. **Then add coverage for what upstream leaves untested.** `proxy.ts` is 370 lines with a single
   upstream case, which means the upstream suite under-specifies it rather than that it is simple.
   Read `Proxy.cs`, identify its branches, and cover them. Streaming, error propagation and
   cancellation paths are the ones that matter.
3. State in the PR which behaviours you covered beyond upstream, and which you judged not worth
   covering and why.

## Acceptance criteria

- [ ] All 4 upstream cases ported, names preserved verbatim
- [ ] `Proxy.cs` branch coverage added beyond upstream, and described in the PR
- [ ] `bash tools/test-parity.sh` passes, and the `agent` floor is raised in the same commit
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes; no new skips without a row in `.skip-budget.md`
- [ ] Any failing ported test is reported rather than worked around
- [ ] Anything in this packet not delivered is named in the PR

## Hazards

- **Do not test the implementation back to itself.** The value is in the upstream cases and in
  branches you can reason about from `proxy.ts`. A test written by reading `Proxy.cs` and asserting
  what it currently does certifies whatever bug is there.

- **Timing.** If any test needs to wait for asynchronous work, wait on the condition, not on a fixed
  interval. A sleep-based wait in `StdinBufferTests` failed intermittently under load and had to be
  converted to condition polling; see `WaitUntilAsync` there for the pattern.

- **Standing rule.** Where a hazard here contradicts the TypeScript source, the source wins — and say
  so in the PR.

## Out of scope

- Any change under `src/`, unless a ported test proves a defect, and then in a separate commit.
- Any TUI work.
- Any NuGet dependency.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
