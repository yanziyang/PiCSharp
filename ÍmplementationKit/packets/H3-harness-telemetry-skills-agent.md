# `H3` — Port harness telemetry, skills and the agent harness

**Wave:** harness (completes the core; unblocks wave 6)  **Delegation fit:** B
**Depends on:** `H1` (session, `0bf2b36`) and `H2` (reducer + compaction, `5b766f7`)

---

## Why this is next

H2 took `pi-agent-core` to 95% implementation completeness and 64% test parity, with `Harness/Session`
and the agent core correctly left untouched. What remains of the harness core is three files:

| | LOC | Status |
|---|---|---|
| `telemetry.ts` | 615 | needed by `agent-harness.ts` |
| `skills.ts` | 386 | independent |
| `agent-harness.ts` | 508 | **the top of the stack — wave 6 builds on this** |

`agent-harness.ts` imports compaction, session, result and types (all delivered) plus telemetry, so
telemetry goes in the same packet. Once this lands, `Pi.CodingAgent` has something to build on.

`env/`, `tools/` and `utils/` are held for H4 — they are the environment adapter and the built-in
tool implementations, and nothing in this packet depends on them.

## Source (read-only specification)

```
reference/pi/packages/agent/src/harness/
  telemetry.ts        615   schema-typed span helpers over pi-telemetry
  skills.ts           386   skill discovery, frontmatter, ignore-file handling
  agent-harness.ts    508   the harness entry point
```

Upstream tests, 16 cases:

```
reference/pi/packages/agent/test/harness/telemetry.test.ts                 4
reference/pi/packages/agent/test/harness/skills.test.ts                    6
reference/pi/packages/agent/test/harness/agent-harness-scaffold.test.ts    4
reference/pi/packages/agent/test/harness/events.test.ts                    2
```

## Target (you may write only these paths)

```
src/Pi.AgentCore/Harness/**          excluding Harness/Session/** and Harness/Compaction/**
src/Pi.Telemetry/**                  only for the schema-typing work in hazard 1
tests/Pi.AgentCore.Tests/Harness/**  excluding Harness/Session/**
tests/Pi.Telemetry.Tests/**
Directory.Packages.props             only to add YamlDotNet, see hazard 2
```

**Frozen — do not modify:** `Harness/Session/**`, `Harness/Compaction/**`, `Reducer.cs`, `Agent.cs`,
`AgentLoop.cs`, `AgentTypes.cs`. All are covered by passing tests. If a change looks necessary, stop
and report it.

## Hazards

### 1. Telemetry schema typing has no C# equivalent — this is prescribed, not open

`harness/telemetry.ts` builds on `pi-telemetry`'s compile-time inference: `ExactTelemetryAttributes`,
`SchemaTelemetrySpan`, `TelemetrySchemaSpanStartAttributes`. **None of these exist in the ported
`Pi.Telemetry`** — it sits at 79% precisely because that machinery was skipped.

Do not try to reproduce the inference. Follow `docs/translation-patterns.md §2.2`:

- **Port the schemas as data.** `AI_TELEMETRY_SCHEMA` and `HARNESS_TELEMETRY_SCHEMA` are runtime
  values and port directly.
- **Port the functions with loose signatures.** Check what the TypeScript actually does before
  agonising: `startAiSpan` is `telemetryContext.startSpan({ name, attributes }, cb)` plus a cast. The
  whole generic apparatus is compile-time only, so the runtime port is small.
- **Recover the guarantee at runtime.** Validate attributes against the schema: throw on an unknown
  span name, a missing required attribute, or a wrong value type. **Then test that validation** — an
  unvalidated port silently loses the only guarantee the original provided.

This also closes the `Pi.Telemetry` gap left by T1.1.

### 2. `skills.ts` needs two dependencies — one added, one ported

- **`yaml`** → `YamlDotNet`, approved in `docs/dependencies.md §3` but **not yet in
  `Directory.Packages.props`**. This is the first new dependency since the scaffold. Follow §6:
  confirm nothing in §2 covers it, confirm it is AOT and trim clean, and state in the PR how
  frontmatter round-trip fidelity was verified against upstream.
- **`ignore`** → **port it, do not take a dependency.** `docs/dependencies.md §4` requires this:
  `.gitignore` semantics are subtle (negation, directory-only rules, precedence) and no .NET library
  matches exactly. Target `src/Pi.AgentCore/Harness/GitIgnoreMatcher.cs`. Skills honours
  `.gitignore`, `.ignore` and `.fdignore`, so the matcher must handle all three the same way
  upstream does.

### 3. `agent-harness.ts` is the wave 6 contract

Everything `Pi.CodingAgent` will build on comes through here. A convenient-looking simplification now
becomes a wrong foundation for 61,000 lines later. Port the surface faithfully, including anything
that looks redundant, and note in the PR anything you were tempted to change.

### 4. Standing rules

- **`undefined` is not `null`** — `translation-patterns.md §1`. Assert exact key sets on round-trips.
- **JSON model** — `§2.1`: strict typed model for formats we own; `JsonNode` only for open-ended bags.
- **Where a hazard here contradicts the TypeScript source, the source wins.** H1 carried a wrong
  hazard requiring `[JsonExtensionData]`; the delivered implementation correctly followed upstream
  instead, and the packet was corrected afterwards. Do the same, and say so in the PR.

## Acceptance criteria

- [ ] All 16 upstream cases ported, names preserved verbatim
- [ ] Schema attribute validation implemented **and tested**
- [ ] `GitIgnoreMatcher` handles `.gitignore`, `.ignore`, `.fdignore`, with negation and directory-only rules tested
- [ ] `bash tools/test-parity.sh` shows `agent` above its floor and completeness risen from 95%
- [ ] **`.test-parity` floors raised in the same commit** — H1 and H2 both left them stale
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes with **no skipped tests**
- [ ] If `YamlDotNet` is added, `docs/dependencies.md §6` steps 1–4 appear in the PR description
- [ ] Every ported test that fails is listed in the PR with upstream expectation vs actual

## Out of scope

- `env/`, `tools/`, `utils/` — H4.
- Anything under the frozen paths above.
- Any NuGet dependency other than `YamlDotNet`. If you believe one is needed, stop and report it.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.

## One standing instruction

**The tests ship in the same PR as the code.** H1 and H2 both did this and both landed clean. A
packet that lands an implementation without its upstream suite is not complete, regardless of what
CI says.
