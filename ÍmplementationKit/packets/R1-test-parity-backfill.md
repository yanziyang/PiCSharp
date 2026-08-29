# `R1` — Backfill the upstream test suites

**Wave:** remediation (blocks further feature waves)  **Delegation fit:** A
**Depends on:** the ported implementations, which are already on `main`

---

## Why this exists

`docs/differential-testing.md` makes the upstream suite the specification and says it is "ported
**with** the implementation, in the same PR. Never deferred." Waves 2–5 ported the implementation but
not the suite. Measured by `tools/test-parity.sh`:

| Package | Upstream cases | Ported | Coverage |
|---|---|---|---|
| `protocol` | 33 | 51 | 154% |
| `telemetry` | 7 | 11 | 157% |
| `client` | 36 | 7 | 19% |
| `server` | 50 | 9 | 18% |
| `ai` | 1,360 | 99 | **7%** |
| `agent` | 226 | 8 | **3%** |
| `tui` | 838 | 19 | **2%** |

Protocol and telemetry show the standard is achievable — they exceed upstream. The rest is the debt.

This is not a style complaint. The implementation currently has **no oracle**. Every claim that the
port is faithful rests on tests that were never written, and the CI green tick has been measuring
almost nothing for five waves.

## Objective

Raise each package to **at least 90% of its upstream case count**, by porting the upstream tests —
not by writing new ones that happen to pass against the current implementation.

## Order

Do these as separate packets, in this order. Each is independently mergeable.

| Packet | Package | Target | Notes |
|---|---|---|---|
| R1.1 | `agent` | 226 cases | Smallest absolute gap of the three big ones; unblocks confidence in the agent loop |
| R1.2 | `client` + `server` | 86 cases | Small, and turns on the cross-runtime oracle (see below) |
| R1.3 | `tui` | 838 cases | Includes the regression tests listed below |
| R1.4 | `ai` | 1,360 cases | Largest; split by provider adapter, one packet each |

## Source

For each package, every file matching:

```
reference/pi/packages/<package>/test/**/*.test.ts
```

## Target

```
tests/Pi.<Project>.Tests/**
```

Implementation files are **out of scope**. If a ported test fails, that is the point of the exercise:
report the failure in the PR with the upstream expectation and the actual C# behaviour. **Do not edit
the implementation to make a test pass, and do not adjust the test to match the implementation.** A
failing ported test is a finding, not a blocker.

## The tests that matter most

`tui` upstream carries regression tests that encode bugs already found and fixed. Porting these is the
whole point — without them those bugs return silently:

```
regression-overlay-cjk-boundary.test.ts
regression-regional-indicator-width.test.ts
bug-regression-isimageline-startswith-bug.test.ts
overlay-non-capturing.test.ts
overlay-short-content.test.ts
```

## Acceptance criteria

- [ ] `bash tools/test-parity.sh` shows the package at or above 90%
- [ ] `.test-parity` floor raised to the new count in the same commit
- [ ] Build clean, format clean, no skipped tests
- [ ] Test names preserved verbatim from upstream so failures map back
- [ ] Every ported test that **fails** is listed in the PR with upstream expectation vs actual

## Known hazards

- **Do not rewrite tests to pass.** The value is entirely in faithfulness. A test suite that agrees
  with a wrong implementation is worse than no suite, because it certifies the defect.
- **`vitest` and `xunit` differ on async assertion semantics.** `expect(...).rejects` maps to
  `await Assert.ThrowsAsync<T>`; do not swallow the exception type.
- **Table-driven upstream tests** (`it.each`, arrays of cases) map to `[Theory]` with `[MemberData]`.
  Keep one C# case per upstream case so the counts are comparable.
- **Golden/snapshot tests** need `Verify.Xunit`, which is approved in `docs/dependencies.md §3` but
  not yet in `Directory.Packages.props`. Add the `PackageVersion` in the packet that first needs it,
  and say so in the PR.

## Out of scope

- Do not modify any file under `src/`.
- Do not add dependencies beyond `Verify.Xunit` as noted above.
- Do not raise a `.test-parity` floor for a package you did not work on.
