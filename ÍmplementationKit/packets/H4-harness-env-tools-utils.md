# `H4` — Port the harness environment, tools and utilities

**Wave:** harness (completes wave 3)  **Delegation fit:** B
**Depends on:** `H1` (`0bf2b36`), `H2` (`5b766f7`), `H3` (`430801d`)

---

## Why this is last, and why it still matters

H3 landed `agent-harness.ts`, so wave 6 is unblocked in principle. But
`coding-agent/src/server/create-harness.ts` takes `env: ExecutionEnv` — H2 delivered the *interface*,
and nothing yet implements it. Until this packet lands, the harness is an abstraction with nothing
behind it and no built-in tools.

This is the final `H`-series packet. After it, wave 3 is complete and work resumes on the numbered
plan in `work-breakdown.md` — wave 5 (`Pi.Tui`, 10% ported) is next.

## Source (read-only specification)

```
reference/pi/packages/agent/src/harness/
  env/nodejs.ts               701   ExecutionEnv implementation over process + filesystem
  tools/edit-diff.ts          500   line-ending handling, fuzzy matching, unified patch
  tools/bash.ts               161
  tools/read.ts               144
  tools/edit.ts               140
  tools/image.ts              104
  tools/file-mutation-queue.ts 56
  tools/write.ts               39
  tools/path-utils.ts          30
  tools/index.ts               23
  tools/tool-context.ts         6
  utils/truncate.ts           350   output truncation with exact thresholds
  utils/shell-output.ts       195   binary output sanitisation
```

Upstream tests, 60 cases:

```
reference/pi/packages/agent/test/harness/nodejs-env.test.ts           26
reference/pi/packages/agent/test/harness/tools.test.ts                23
reference/pi/packages/agent/test/harness/truncate.test.ts              9
reference/pi/packages/agent/test/harness/resource-formatting.test.ts   2
```

## Target (you may write only these paths)

```
src/Pi.AgentCore/Harness/Env/**
src/Pi.AgentCore/Harness/Tools/**
src/Pi.AgentCore/Harness/Utils/**
tests/Pi.AgentCore.Tests/Harness/**
```

**Frozen — do not modify:** `Harness/Session/**`, `Harness/Compaction/**`, `Reducer.cs`,
`Telemetry.cs`, `Skills.cs`, `AgentHarness.cs`, `Types.cs`, `GitIgnoreMatcher.cs`, `Agent.cs`,
`AgentLoop.cs`, `AgentTypes.cs`. All are covered by passing tests. If a change looks necessary, stop
and report it.

## Hazards

### 1. `NodeExecutionEnv` is renamed — deliberately

Upstream exports `NodeExecutionEnv` from `agent/src/node.ts`. `AGENTS.md` says to mirror upstream
names, but there is no Node here and the name would be actively misleading in a .NET codebase.

**Port it as `SystemExecutionEnv` in `Harness/Env/SystemExecutionEnv.cs`.** Record the rename in the
PR. This is the only sanctioned naming deviation in the packet; everything else mirrors upstream.

It implements `ExecutionEnv`, which is `FileSystem` + `Shell` from `Types.cs` (delivered in H2). Note
that the whole contract is **`Result<T, FileError>`-returning, not exception-throwing** — match that.
A port that throws where upstream returns an error result changes control flow for every caller.

`spawn` maps to `System.Diagnostics.Process`; `node:fs/promises` to `System.IO`. The 26 upstream
cases cover timeouts, abort signals, environment inheritance and spawn failures — port all of them.

### 2. The diff dependency — decide with evidence, do not assume

`tools/edit-diff.ts` imports the `diff` npm package but uses exactly **two** things:

- `Diff.diffLines` — line-level diff
- `Diff.createTwoFilesPatch` with `FILE_HEADERS_ONLY` — unified patch formatting

`docs/dependencies.md §3` approves `DiffPlex` with the standing warning that it *must produce
upstream-identical hunks*. That warning applies here, and the two halves carry different risk:

- **The line diff is the safe half.** If `DiffPlex` produces the same line-level result, use it.
- **The patch formatting is the risky half.** Unified-patch output is user-visible — it goes into
  tool results the model reads — and `DiffPlex`'s formatter is not `createTwoFilesPatch`'s.
  Hand-writing that formatting against the well-specified unified-diff format is likely easier than
  bending a library's formatter to match byte-for-byte.

**Whichever you choose, prove it with a golden test** comparing output against the TypeScript
implementation for a fixture set that includes: no-change, pure insertion, pure deletion, adjacent
hunks that merge at `context = 4`, and a file with no trailing newline. Add the `PackageVersion` only
if you actually use it, and state the §6 steps in the PR.

### 3. Truncation thresholds are exact, not approximate

`utils/truncate.ts` carries `DEFAULT_MAX_LINES = 2000`, `DEFAULT_MAX_BYTES = 50 * 1024` and
`GREP_MAX_LINE_LENGTH = 500`. These determine what the model sees. Port the values and the
head/tail/line algorithms exactly — an off-by-one in the truncation boundary changes model input on
every large tool result, and nothing downstream will flag it.

### 4. Line endings and BOM

`edit-diff.ts` has `detectLineEnding`, `normalizeToLF`, `restoreLineEndings` and `stripBom` for a
reason: the edit tool must write files back in the encoding it found them. This matters more on
Windows than it did upstream. Port the round-trip faithfully and test CRLF and BOM cases explicitly.

### 5. Standing rules

- **`undefined` is not `null`** — `translation-patterns.md §1`.
- **JSON model** — `§2.1`: strict typed model for formats we own; `JsonNode` only for open-ended bags.
- **Where a hazard here contradicts the TypeScript source, the source wins.** This has now happened
  twice: H1's `[JsonExtensionData]` requirement and H3's `YamlDotNet` instruction were both wrong,
  and both were correctly overridden. **Both times the deviation went unreported and was only found
  by diffing.** If you override something in this packet, say so in the PR — that is what
  `AGENTS.md` requires, and it is how the packets get corrected.

## Acceptance criteria

- [ ] All 60 upstream cases ported, names preserved verbatim
- [ ] `SystemExecutionEnv` implements the full `ExecutionEnv` contract, returning results rather than throwing
- [ ] Unified-patch output golden-tested against the TypeScript implementation
- [ ] CRLF and BOM round-trips tested explicitly
- [ ] `bash tools/test-parity.sh` shows `agent` above its floor
- [ ] **`.test-parity` floors raised in the same commit** — H1, H2 and H3 all left them stale
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes with **no skipped tests**
- [ ] Any dependency added or declined is explained in the PR per `dependencies.md §6`
- [ ] Any packet instruction overridden is stated in the PR

## Out of scope

- Anything under the frozen paths above.
- `coding-agent/src/core/tools/` — that is wave 6's T6.1 and is a **separate, parallel** tool set with
  its own implementations. Do not merge the two.
- Any NuGet dependency beyond a diff library, if hazard 2 concludes one is needed.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.

## One standing instruction

**The tests ship in the same PR as the code.** H1, H2 and H3 all did this and all landed clean. A
packet that lands an implementation without its upstream suite is not complete, regardless of what
CI says.
