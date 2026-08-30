# `R2` — Port the last two `pi-agent-core` files

**Wave:** remediation  **Delegation fit:** A
**Depends on:** nothing — both are self-contained
**Status:** ⚠️ Implementation delivered in `831da91` (1,212 lines). **Tests were not delivered** — the agent
case count was 234 before and 234 after, none of the 4 upstream cases were ported, and the omission was
not reported. CI passed because the parity gate enforced a floor, which untested implementation stays
above indefinitely. That hole is now closed in `tools/test-parity.sh`. The test half is re-issued as `R3`.

---

## Why this exists as its own packet

These two files are the last of `pi-agent-core`. They were bundled as a "second objective" into
`T5.4`, which delivered its TUI work well and dropped them without mention.

That was my mistake, not the implementer's: an unrelated objective inside a larger packet is
invisible when omitted, because the packet still reads as done. As its own packet it is either
delivered or it is not.

`pi-agent-core` is otherwise at 103% test parity. This closes it out.

## Source (read-only specification)

```
reference/pi/packages/agent/src/proxy.ts                 370 LOC
reference/pi/packages/agent/src/harness/system-prompt.ts  34 LOC
```

Upstream tests, 4 cases:

```
reference/pi/packages/agent/test/proxy.test.ts               1
reference/pi/packages/agent/test/harness/system-prompt.test.ts  3
```

## Target (you may write only these paths)

```
src/Pi.AgentCore/Proxy.cs
src/Pi.AgentCore/Harness/SystemPrompt.cs
tests/Pi.AgentCore.Tests/**
```

**Frozen — do not modify:** anything else under `src/`. `pi-agent-core` is at 103% parity with 234
passing cases; a change elsewhere is a separate decision. If it looks necessary, stop and report.

## Hazards

- **`proxy.ts` is 370 LOC with a single upstream test.** Low coverage upstream is not permission for
  low coverage here — it means the upstream suite under-specifies this file. Port the one case, then
  read the implementation and add cases for the branches it leaves untested. Say in the PR which
  behaviours you covered beyond upstream.

- **`system-prompt.ts` is 34 LOC with 3 cases.** It should be a short port. If it turns out to depend
  on something unported, stop and report rather than pulling extra scope in.

- **Standing rule.** Where a hazard here contradicts the TypeScript source, the source wins — and say
  so in the PR.

## Acceptance criteria

- [ ] All 4 upstream cases ported, names preserved verbatim
- [ ] Additional `proxy.ts` coverage added and described in the PR
- [ ] `bash tools/test-parity.sh` shows `agent` above its floor
- [ ] **`.test-parity` floors raised in the same commit**
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes with **no skipped tests**
- [ ] Anything in this packet not delivered is named in the PR

## Out of scope

- Any TUI work.
- Any NuGet dependency. If you believe one is needed, stop and report it.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.
