# PiCSharp

PiCSharp is a native C# / .NET 10 port of the [Pi Coding Agent](https://pi.dev/). It targets
near-complete compatibility with the upstream Pi ecosystem, including its protocol, agent runtime,
AI provider adapters, terminal UI contracts, and—once the port is complete—its extension surface.

This repository is an active port and is not feature-complete. The `pi` executable is currently a
scaffold; the implementation is being delivered in compatibility-focused milestones. The
TypeScript source under [`reference/pi`](reference/pi) is the read-only behavioral specification.

## Upstream reference

- Home page: [pi.dev](https://pi.dev/)
- Source repository: [earendil-works/pi](https://github.com/earendil-works/pi)
- Pinned upstream release: **v0.84.4**
- Pinned commit: [`b79e4cc834970cca69daebffab7df1da7d1e52c4`](reference/PINNED)

The pinned reference is a Git submodule. Do not update it as part of an ordinary port milestone.

## Current implementation status

The following compatibility layers are currently implemented and covered by deterministic tests:

- `Pi.Protocol` — protocol schemas, CBOR framing, and wire-compatible message handling.
- `Pi.Telemetry` — runtime telemetry and memory recording.
- `Pi.Ai.Abstractions` — provider-neutral model, request, response, and event contracts.
- `Pi.Ai.Testing` — deterministic faux provider for tests without API calls or token spend.
- `Pi.Ai` — authentication, model/runtime helpers, HTTP/SSE transport, and adapters for Anthropic,
  OpenAI Chat Completions, OpenAI Responses, Google Generative AI, Mistral, and Amazon Bedrock.
- `Pi.AgentCore` — agent-loop orchestration and stateful runtime behavior.
- `Pi.Client` — protocol client and session leases.
- `Pi.Server` — protocol server, live-session dispatch, ownership, and attachment behavior.
- `Pi.Tui` — layout/container contracts, viewport geometry, bounded terminal output, and the first
  differential-renderer core.

The remaining high-risk work includes the complete terminal adapter, editor and keybinding surface,
grapheme/East-Asian width handling, terminal images, autocomplete/search, the coding-agent tools and
interactive UI, CLI modes, and the redesigned extension host. Compatibility should therefore be
judged against the implemented milestone rather than assumed from the project name.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Pi.Protocol` | Wire protocol and framing |
| `src/Pi.Ai*` | AI contracts, providers, and deterministic test support |
| `src/Pi.AgentCore` | Agent runtime |
| `src/Pi.Client` / `src/Pi.Server` | Protocol client/server surfaces |
| `src/Pi.Tui` | Terminal UI foundations and renderer work |
| `src/Pi.CodingAgent` | Coding-agent integration target |
| `src/Pi.Cli` | `pi` executable target; currently scaffolded |
| `tests` | Ported and conformance-oriented xUnit tests |
| `reference/pi` | Pinned, read-only TypeScript specification |
| `docs` | Architecture, compatibility, dependency, and testing decisions |
| `ÍmplementationKit` | Delegation packets and implementation guidance |

## Requirements

- Windows, macOS, or Linux
- .NET SDK `10.0.303` or a compatible later feature-band SDK
- Git with submodule support

## Build and test

Clone the repository with its upstream specification:

```text
git clone --recurse-submodules https://github.com/yanziyang/PiCSharp.git
cd PiCSharp
```

Build the solution:

```text
dotnet build PiCSharp.slnx
```

Run the complete non-E2E suite:

```text
dotnet test PiCSharp.slnx -- --filter-not-trait "Category=E2E"
```

Run one project:

```text
dotnet test tests/Pi.Tui.Tests
```

Verify formatting:

```text
dotnet format PiCSharp.slnx --verify-no-changes
```

E2E tests that require provider credentials are intentionally excluded from the normal verification
command. The test suite uses the faux provider for agent-loop coverage and never calls real provider
APIs.

## Porting rules

Porting is deliberately behavior-first:

1. Read the matching TypeScript source and upstream tests before changing C# code.
2. Preserve wire names, defaults, ordering, error behavior, and JavaScript-visible semantics.
3. Keep the TypeScript reference read-only and do not introduce Node.js into the shipping product.
4. Keep changes within the packet's target paths and commit each milestone independently.

See [`AGENTS.md`](AGENTS.md), [`docs/translation-patterns.md`](docs/translation-patterns.md), and
the design documents under [`docs`](docs) for the detailed contribution contract.

## License

See [`LICENSE`](LICENSE).
