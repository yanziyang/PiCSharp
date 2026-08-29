# `H1` — Port the agent-harness session subsystem

**Wave:** harness (unblocks wave 6)  **Delegation fit:** B  **Depends on:** `Pi.AgentCore` (on `main`)

---

## Why this is next

`pi-agent-core`'s `harness/` subsystem is 10,065 LOC and **none of it is ported**. `Pi.CodingAgent`
cannot begin until it exists: there is no session store, no compaction, no skills layer to build on.
`tools/test-parity.sh` reports the package at 18% implementation completeness.

Session is the foundation of that subsystem — `agent-harness.ts` and `reducer.ts` both depend on it —
so it goes first. It is also the best-oracled piece of work available: upstream ships a **1,016-line
conformance suite** specifically so that a session implementation can be verified against it.

## Objective

Port the harness session subsystem, **including its conformance suite**, to C#.

## Source (read-only specification)

```
reference/pi/packages/agent/src/harness/session/
  types.ts              393    core session types
  state.ts              344    in-memory state projection
  session.ts            299    session object and lifecycle
  jsonl/storage.ts      277    append-only file storage
  jsonl/repo.ts         247    repository over a session directory
  jsonl/codec.ts        240    line encode/decode
  jsonl/types.ts         57    JsonlV4Header and entry types
  jsonl/errors.ts        27
  memory.ts             192    in-memory backend
  context.ts            100    context projection
  index.ts, jsonl.ts     22    public surface
  testing/conformance.ts 1016  the conformance suite — port this too
  testing/types.ts        16
```

Upstream tests, 46 cases total:

```
reference/pi/packages/agent/test/harness/session/jsonl.test.ts          23
reference/pi/packages/agent/test/harness/session/jsonl-codec.test.ts     9
reference/pi/packages/agent/test/harness/session/jsonl-storage.test.ts   5
reference/pi/packages/agent/test/harness/session/search.test.ts          4
reference/pi/packages/agent/test/harness/session/context.test.ts         3
reference/pi/packages/agent/test/harness/session/memory.test.ts          2
```

## Target (you may write only these paths)

```
src/Pi.AgentCore/Harness/Session/**
src/Pi.AgentCore/Harness/Session/Testing/**      the ported conformance suite
tests/Pi.AgentCore.Tests/Harness/Session/**
```

Do not modify existing files under `src/Pi.AgentCore/` outside `Harness/`. If a change to
`Agent.cs`, `AgentLoop.cs` or `AgentTypes.cs` seems necessary, **stop and report it** — those are
covered by 40 passing tests and a change there is a separate decision.

`src/Pi.SessionBackends.Sqlite/` is empty and **stays empty** in this packet.

## Oracles

1. **Port `testing/conformance.ts` as a reusable suite**, not as a set of one-off tests. Upstream's
   design intent is that any backend implementation can be run against it; preserve that. Then run
   both the JSONL backend and the in-memory backend through it. This is the same pattern
   `pi-telemetry` uses, and telemetry is the best-verified package in this repository at 157% parity.

2. **Port all 46 upstream cases**, names preserved verbatim so a failure maps back to the upstream
   expectation.

3. **Round-trip byte-identity.** A session file written by the C# implementation must be byte-identical
   to the same session written by the TypeScript implementation. Commit the fixtures.

## Acceptance criteria

- [ ] `bash tools/test-parity.sh` shows `agent` above its current floor, and the implementation ratio risen from 18%
- [ ] `.test-parity` floor raised in the same commit
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes with **no skipped tests** (`.skip-budget` is 0)
- [ ] The conformance suite is public and runs against both backends
- [ ] Every ported test that fails is listed in the PR with upstream expectation vs actual behaviour

## Known hazards

- **There are two unrelated session formats. Do not conflate them.**

  | | harness (this packet) | coding-agent (not this packet) |
  |---|---|---|
  | header | `kind: "header"` | `type: "session"` |
  | version | `version: 4` | `version?` — absent means v1 |
  | time | `createdAt: number` | `timestamp: string` |
  | parent | `parentSessionId` | `parentSession` |

  `docs/session-format.md` documents the **coding-agent** format and does **not** apply here. Its
  scope note says so. Read `jsonl/types.ts` for this one.

- **Preserve unknown fields.** A file written by a newer Pi may carry fields we do not model. Capture
  them with `[JsonExtensionData]` and write them back untouched, or opening an existing session
  silently destroys data.

- **Append-only means append-only.** Match upstream's flush and durability behaviour in
  `jsonl/storage.ts`. A crash must not leave a truncated entry.

- **Line order carries meaning.** Never sort. JSONL order is the session's history.

- **`undefined` is not `null`.** `docs/translation-patterns.md §1`. Round-trip tests must assert the
  exact key set, not just values.

- **JSON model.** Follow `docs/translation-patterns.md §2.1`: a strict typed model for a format we
  own and validate, not `JsonNode`. `metadata` is a `Record<string, JsonValue>` bag and is the one
  place a document model is appropriate.

## Out of scope

- `compaction/`, `reducer.ts`, `skills.ts`, `agent-harness.ts`, `env/`, `tools/`, `utils/` — later packets.
- The coding-agent session manager (`packages/coding-agent/src/core/session-manager.ts`).
- Any NuGet dependency. If you believe one is needed, stop and report it.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.

## One standing instruction

Waves 2–5 ported implementations without their tests, which left five waves resting on an oracle that
did not exist. `tools/test-parity.sh` now enforces a ratchet, but the rule is simpler than the tool:
**the tests ship in the same PR as the code.** A packet that lands an implementation without its
upstream suite is not complete, regardless of what CI says.
