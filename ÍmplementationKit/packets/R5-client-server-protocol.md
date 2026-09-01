# `R5` — Test the client/server protocol boundary

**Wave:** remediation  **Delegation fit:** A
**Depends on:** `Pi.Client` and `Pi.Server` implementations (wave 1, already shipped)
**Plan reference:** remediation, and the boundary wave 6 sits on.

---

## Why this is next

`R4` measured something worth acting on: **nine real divergences in 134 ported cases**, roughly one
per fifteen — including `cloudflare-gateway-binding.ts`, 176 lines of upstream functionality that had
simply never been ported and that nobody knew was missing.

`Pi.Client` and `Pi.Server` are the two least-verified packages left, and they are small:

| Package | Upstream | Ported |
|---|---|---|
| `Pi.Client` | 36 | 7 |
| `Pi.Server` | 50 | 9 |

They are also the contract `Pi.CodingAgent` talks through — 60,960 LOC of wave 6 sits directly on
this boundary. Verifying it costs less than any other remediation available.

## A transport gap sets this packet's scope

Upstream ships exactly one transport, `server/src/transports/unix` (475 LOC), plus a testing library
`server/src/testing` (466 LOC) providing `TestServerService`, `ProtocolTestClient` and
`connectUnixTestClient`. **Neither has a C# equivalent.** `Pi.Server` is transport-agnostic — it has a
clean `IPiServerListener` contract with `Address`, `StartAsync`, `CloseAsync` — but no listener
implements it over a Unix domain socket.

So the 86 upstream cases split:

```
42  transport-independent  <- THIS PACKET
44  need the Unix transport and the testing library  -> R5b
```

This packet takes the 42 that can be ported against what exists today. `R5b` is the follow-on and is
an implementation packet, not a test packet.

## Scope — 42 cases

```
reference/pi/packages/client/test/
  connection.test.ts   14
  sessions.test.ts      7
  state.test.ts         4
  requests.test.ts      3
  disposal.test.ts      3

reference/pi/packages/server/test/
  protocol.test.ts      9
  listener.test.ts      2
```

Connection lifecycle, request/response correlation, session state, disposal, and protocol framing.
None of these construct a Unix server.

## Target (you may write only these paths)

```
tests/Pi.Client.Tests/**
tests/Pi.Server.Tests/**
.test-parity
```

`src/` is **frozen for this packet.** If a ported test fails, that is the finding: report it with the
upstream expectation and the actual C# behaviour, then fix it in a **separate commit**, exactly as
`R4` did.

**`.test-parity` is in the list deliberately.** `R4`'s target paths froze it while its criteria
required raising it — a contradiction the delivery caught and correctly declined to resolve silently.
That was a defect in the packet, not the delivery. Fixed here.

## Hazards

### 1. Existing cases may already cover some of these — reconcile, do not duplicate

`Pi.Client.Tests` has `ClientTests.cs` (7 cases) and `Pi.Server.Tests` has `ServerTests.cs` (9). Some
may already assert what an upstream case asserts, under a different name.

Where that happens, **rename the existing case to upstream's name and keep one test** — do not leave
two tests asserting the same thing. `T5.2c` had the same situation with `TestTui` and the rule that
worked was: one implementation, one test, upstream's name. A duplicate pair is worse than either
alone, because the next person cannot tell which is authoritative.

### 2. If a case needs `TestServerService`, it belongs to `R5b` — say so

The scoping above is my reading of which cases are transport-independent. If one of the 42 turns out
to need the testing library or a live listener, **do not build a partial version of it to get the
test passing.** Report it, leave it for `R5b`, and note it in the PR. A half-ported test harness is
harder to finish than an absent one.

### 3. Connection and disposal are lifecycle tests — wait on conditions

`connection.test.ts` (14) and `disposal.test.ts` (3) exercise async lifecycle: connect, close,
dispose, reconnect. Wait on the **condition**, never a fixed delay. Two flakes have already shipped
into this repository from sleep-based waits, and both took a debugging cycle to find.

A delay that is itself the behaviour under test is the one exception, and it should carry a comment
saying so — `EditorAutocompleteTests` has the pattern.

### 4. Do not test the implementation back to itself

`R3`'s caveat, and `R4` proved its value: nine divergences surfaced precisely because expectations
came from the TypeScript rather than from reading the C#. A test written by reading `PiClient.cs` and
asserting what it currently does certifies whatever bug is there.

Where the TypeScript is genuinely ambiguous, say so in the PR rather than guessing.

### 5. Standing rules

- **`undefined` is not `null`** — `translation-patterns.md §1`. Protocol payloads have meaningful
  absent fields, and a missing field is not a null one.
- **Where a hazard here contradicts the TypeScript source, the source wins** — and **say so in the PR**.

## Acceptance criteria

- [ ] All 42 cases ported and passing, names preserved verbatim
- [ ] Pre-existing overlapping cases reconciled to upstream names, not duplicated
- [ ] Any case that turns out to need the Unix transport reported and deferred to `R5b`, not
      half-built
- [ ] Any failing ported test reported as a finding and fixed in a **separate commit**
- [ ] `bash tools/test-parity.sh` shows `client` risen from 19% and `server` from 18% of portable
- [ ] **`.test-parity` floors raised in the same commit** — it is in the target paths this time
- [ ] `.skip-budget` stays **0**
- [ ] `dotnet build PiCSharp.slnx -c Release` clean, zero warnings
- [ ] `dotnet format PiCSharp.slnx --verify-no-changes` passes
- [ ] `dotnet test PiCSharp.slnx` passes; **run it at least three times**
- [ ] Anything not delivered, or any instruction overridden, is named in the PR

## Out of scope

- **`R5b`** — the Unix transport (`transports/unix`, 475 LOC), the testing library
  (`src/testing`, 466 LOC), and the 44 cases that need them: `server/conformance` (14),
  `server/sessions` (12), `server/server` (7), `server/unix` (5), `client/unix` (5),
  `server/unix-connection` (1). That is an implementation packet.
- `T0.2`, the differential harness. `server/test/conformance.test.ts` is its natural first tenant,
  but that decision has not been taken.
- Any `Pi.Ai` work — `R6` covers the remaining 501 portable cases.
- Any TUI work; `T5.8` is running separately.
- Any NuGet dependency.

## Conventions

`AGENTS.md` at the repository root, and `docs/translation-patterns.md` for every TS→C# construct.
Where the conventions and the TypeScript source conflict, **the source wins** — and say so in the PR.

## One standing instruction

**Anything not delivered, or any instruction overridden, must be named in the PR.** `R4` did this
without being chased — it declined to edit a frozen file its criteria demanded, and wrote down why in
`R4Findings.md`. That disclosure is what let the packet's own contradiction be found and fixed rather
than repeated here. Keep doing it: a findings file alongside the tests is now the expected shape of a
remediation packet, not an extra.
