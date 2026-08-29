# Test fixtures

Recorded inputs used by the differential oracles in `docs/differential-testing.md`.

| Directory | Oracle | Produced by |
|---|---|---|
| `http/<provider>/` | Oracle 3 - HTTP record/replay | `tools/record-fixtures`, run once against live providers |
| `tui/` | Oracle 5 - golden terminal buffers | captured from the TypeScript renderer |
| `sessions/` | session round-trip corpus | captured from real upstream use |

Fixtures are committed. Do not re-record on every CI run: that reintroduces
nondeterminism and cost. Re-record deliberately, in its own commit, when upstream
behaviour changes.
