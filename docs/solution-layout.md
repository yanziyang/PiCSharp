# D2 — Solution Layout and Project Graph

**Status:** Proposed — sign off before T0.1
**Gates:** every task packet, because packets cite exact target paths

---

## Principle

One .NET project per upstream package, named by the same identity. A reviewer holding a TS path must
be able to derive the C# path without asking, and vice versa.

```
@earendil-works/pi-ai  →  src/Pi.Ai/  →  namespace Pi.Ai
```

---

## Repository layout

```
PiCSharp/
├── AGENTS.md                      # copied from codex/AGENTS.md at scaffold
├── PiCSharp.slnx                   # .NET 10 defaults to the XML solution format
├── global.json                     # pins the SDK and opts into the MTP test runner
├── Directory.Build.props          # shared compiler settings — see §4
├── Directory.Packages.props       # central package management
├── .editorconfig                  # analyser + formatting rules
├── reference/
│   └── pi/                        # git submodule, pinned to v0.84.4. READ ONLY.
├── docs/                          # this directory
├── codex/                         # delegation kit: task template, work breakdown
├── src/
│   ├── Pi.Telemetry/
│   ├── Pi.Protocol/
│   ├── Pi.Ai.Abstractions/        # see §2 — a split with no upstream counterpart
│   ├── Pi.Ai/
│   ├── Pi.Ai.Testing/             # faux provider (T1.3)
│   ├── Pi.AgentCore/
│   ├── Pi.Client/
│   ├── Pi.Server/
│   ├── Pi.SessionBackends.Sqlite/
│   ├── Pi.Tui/
│   ├── Pi.CodingAgent/            # library: core, tools, modes, extension host
│   └── Pi.Cli/                    # executable: `pi`. AOT-published.
├── tests/
│   ├── Pi.Telemetry.Tests/
│   ├── Pi.Protocol.Tests/
│   ├── Pi.Ai.Tests/
│   ├── Pi.AgentCore.Tests/
│   ├── Pi.Client.Tests/
│   ├── Pi.Server.Tests/
│   ├── Pi.Tui.Tests/
│   ├── Pi.CodingAgent.Tests/
│   ├── Pi.Conformance.Tests/      # cross-runtime: C# ↔ TypeScript (§5)
│   └── fixtures/                  # recorded HTTP, golden buffers, session files
└── tools/
    ├── record-fixtures/           # drives the TS build to capture fixtures
    └── generate-models/           # port of Pi's model-catalogue generator
```

---

## Project graph

Dependency order, which is also the port order. Upstream's own build order is
`tui → telemetry → ai → agent → sqlite → protocol → client → server → coding-agent`; we reorder only
to front-load the cheap, well-tested foundations.

```
Pi.Telemetry            (no deps)
Pi.Protocol             (no deps)
Pi.Tui                  (no deps)                      ← independent stream, start early
Pi.Ai.Abstractions      → Telemetry
Pi.Ai                   → Ai.Abstractions, Telemetry
Pi.Ai.Testing           → Ai.Abstractions
Pi.AgentCore            → Ai, Telemetry
Pi.SessionBackends.Sqlite → AgentCore
Pi.Client               → Protocol
Pi.Server               → Protocol, Ai
Pi.CodingAgent          → AgentCore, Ai, Client, Protocol, Tui
Pi.Cli                  → CodingAgent
```

**Enforce this graph in CI.** A `ProjectReference` outside it is a build failure, not a review
comment. Parallel Codex tasks will otherwise reach for whatever compiles.

---

## 2. The one deliberate structural deviation

Upstream has a single `pi-ai` package. We split out **`Pi.Ai.Abstractions`** holding the provider
contracts, message model, and streaming types.

Rationale: wave 2 dispatches 11 protocol ports in parallel (T2.4–T2.13). Without a stable
abstractions project they all contend on the same files, and `AGENTS.md` forbids a downstream task
from editing a foundation project. The split makes those eleven tasks genuinely disjoint.

`Pi.Ai.Abstractions` is frozen after T2.1. Changes to it require a dedicated task, never a
drive-by edit.

No other package may be split without amending this document.

---

## 3. Path mapping for task packets

| Upstream | Target |
|---|---|
| `packages/telemetry/src/**` | `src/Pi.Telemetry/**` |
| `packages/protocol/src/**` | `src/Pi.Protocol/**` |
| `packages/ai/src/api/*.ts` | `src/Pi.Ai/Api/*.cs` |
| `packages/ai/src/auth/*.ts` | `src/Pi.Ai/Auth/*.cs` |
| `packages/ai/src/providers/*.ts` | `src/Pi.Ai/Providers/*.cs` |
| `packages/agent/src/**` | `src/Pi.AgentCore/**` |
| `packages/tui/src/**` | `src/Pi.Tui/**` |
| `packages/coding-agent/src/core/tools/*.ts` | `src/Pi.CodingAgent/Tools/*.cs` |
| `packages/coding-agent/src/core/extensions/**` | `src/Pi.CodingAgent/Extensions/**` (redesign — see `extension-api.md`) |
| `packages/coding-agent/src/core/*.ts` | `src/Pi.CodingAgent/Core/*.cs` |
| `packages/coding-agent/src/modes/interactive/**` | `src/Pi.CodingAgent/Interactive/**` |
| `packages/coding-agent/src/cli/**` | `src/Pi.Cli/**` |
| `packages/<pkg>/test/*.test.ts` | `tests/Pi.<Pkg>.Tests/**` |

File naming: `anthropic-messages.ts` → `AnthropicMessagesApi.cs`. Keep 1:1 correspondence; a packet
that merges or splits files must say so in the PR.

---

## 4. `Directory.Build.props`

Non-negotiable settings. These are what make "zero warnings" in `AGENTS.md` meaningful.

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors></WarningsNotAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <IsAotCompatible>true</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`IsAotCompatible` on every library from day one. Retrofitting AOT compatibility after 120k LOC of
reflection-based JSON has been generated is far more expensive than the analyser noise now. It is set
in `src/Directory.Build.props`, alongside the trim, AOT and single-file analysers; `tests/` opts out.

**`latest-recommended`, not `latest-all`.** An earlier draft specified `latest-all`. That was wrong
for this project: it enables rules that actively fight a faithful port (CA1002 on exposed lists,
CA1707 on underscored test names, CA2007 on `ConfigureAwait`, CA1848 on logging delegates), and with
`TreatWarningsAsErrors` every one becomes a build break on style rather than substance — which in a
delegated port means Codex packets failing for the wrong reasons. The rules deliberately relaxed on
top of `latest-recommended` are listed in `.editorconfig` with a justification each; do not silence
others without adding a line there.

`InvariantGlobalization` stays **false**: `Pi.Tui` needs real grapheme and East-Asian-width data.

---

## 5. `Pi.Conformance.Tests`

Has no upstream counterpart. It runs the C# implementation against the **TypeScript** one across the
wire protocol:

- `Pi.Client` ⇄ TypeScript `pi-server`
- TypeScript `pi-client` ⇄ `Pi.Server`

It needs Node on the CI runner (never in the shipping product). See `docs/differential-testing.md §4`.

## 7. Test platform

`xunit.v3` runs on **Microsoft.Testing.Platform**, not VSTest. Three consequences the scaffold had to
solve, recorded so nobody re-solves them:

- `global.json` must contain `"test": { "runner": "Microsoft.Testing.Platform" }`. Without it the
  .NET 10 SDK refuses to run the tests at all. An MSBuild property does not work.
- Test projects must set `<OutputType>Exe</OutputType>` — they host the runner.
- Trait filtering is `-- --filter-not-trait "Category=E2E"`, not VSTest's `--filter`.

`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are **not** needed; `xunit.v3` carries its
own runner.

---

## 6. Rules for packet authors

1. Target paths come from §3. Do not invent one.
2. Verify target paths are disjoint from every in-flight packet before dispatch.
3. A packet that needs a new project is a **C**-rated task and amends this document first.
4. Never let a packet add a `ProjectReference` that violates §1.
