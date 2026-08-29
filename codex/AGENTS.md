# Development Rules — PiCSharp

PiCSharp is a C# / .NET 10 port of the Pi coding agent (`earendil-works/pi`), pinned to upstream **v0.84.4**.

> Copy this file to the repository root as `AGENTS.md` when you scaffold. Codex reads it from the root
> and from any directory it is working in. Add per-directory `AGENTS.md` files for subsystem rules
> (for example `src/Pi.Tui/AGENTS.md`) rather than growing this one indefinitely.

## The prime rule

**The TypeScript source in `reference/pi/` is the specification.** It is read-only.

- Never modify anything under `reference/`.
- Never invent an API shape, an error message, or a default value. Read the TS source.
- If this document and the TS source disagree, the TS source wins — and say so in the PR.
- Do not "improve" behaviour while porting. Port it faithfully. If you find a bug, port the bug,
  then note it in the PR description. Fixes are separate changes.
- If the TS relies on JavaScript semantics that C# does not share (prototype mutation, `undefined`
  vs `null`, integer coercion, iteration order), call it out explicitly in the PR rather than
  silently choosing a C# behaviour.

## Scope discipline

Multiple Codex tasks run against this repository in parallel. Conflicts are expensive.

- One task edits one module. Never write outside the target paths listed in your task packet.
- Never modify a foundation project (`Pi.Protocol`, `Pi.Ai.Abstractions`, `Pi.Telemetry`) from a
  downstream task. If you need a change there, stop and report it — do not make it.
- Never edit another task's files to make your tests pass. Report the blockage instead.
- If your task packet appears wrong or incomplete, say so and stop. Do not expand scope to compensate.

## Definition of done

A task is complete when all of the following hold:

1. The ported xUnit tests for the target module pass.
2. `dotnet build` succeeds with **zero warnings** (`TreatWarningsAsErrors` is enabled).
3. `dotnet format --verify-no-changes` passes.
4. Every public type and member has a counterpart in the TS source, or the PR explains why not.
5. The PR description lists any behaviour you could not reproduce faithfully.

Do not open a PR that does not meet these. An honest "blocked, here is why" is worth more than a
green build that skipped the hard part.

## Tests are the specification

The upstream test suite is 115,921 lines across 472 files. It is the only reliable oracle for a port.

- Port the upstream tests **with** the implementation, in the same PR:
  `reference/pi/packages/<pkg>/test/*.test.ts` → `tests/Pi.<Pkg>.Tests/`.
- Never weaken, delete, or narrow a test to make it pass. If a test cannot be ported faithfully,
  mark it `[Fact(Skip = "reason")]` with a specific reason and flag it in the PR.
- Never call real provider APIs and never spend tokens. Use the ported faux provider
  (`Pi.Ai.Testing`, from `reference/pi/packages/ai/src/providers/faux.ts`) for agent-loop tests.
- For anything with observable output — wire bytes, rendered terminal buffers, streamed deltas —
  prefer a golden-file test captured from the TypeScript implementation over a hand-written
  assertion. See `docs/differential-testing.md`.

## C# conventions

- .NET 10, C# 14. `Nullable` enabled. Warnings as errors.
- `async`/`await` throughout. Stream with `IAsyncEnumerable<T>`. Every async public API takes a
  `CancellationToken`.
- `System.Text.Json` with source-generated serialiser contexts. No Newtonsoft.
- Records for wire and data types; classes for services. Prefer immutability.
- Native AOT is a shipping target: no reflection-based magic on hot paths, no runtime code emit.
- Public API carries XML doc comments. Port the TS doc comment where one exists.
- Prefer `System.Threading.Channels` over hand-rolled producer/consumer plumbing.

## Naming and file mapping

- Package `@earendil-works/pi-ai` → project `Pi.Ai`, namespace `Pi.Ai`.
- TS `camelCase` members → C# `PascalCase`. Preserve the wire name with `[JsonPropertyName]`.
  Wire compatibility is not negotiable.
- Keep a 1:1 TS-file → C#-file correspondence where practical. State every deviation in the PR.
- TS discriminated unions → C# abstract record hierarchies with a `[JsonPolymorphic]` discriminator
  matching the TS `type` field exactly.

## Commands

- Build: `dotnet build PiCSharp.slnx`
- One project's tests: `dotnet test tests/Pi.Ai.Tests`
- Full suite: `dotnet test PiCSharp.slnx`
- Excluding E2E: `dotnet test PiCSharp.slnx -- --filter-not-trait "Category=E2E"`
- Format: `dotnet format PiCSharp.slnx`
- Tests run on Microsoft.Testing.Platform (xunit.v3), so trait filters use
  `-- --filter-not-trait`, not VSTest's `--filter`.
- Never run tests tagged `[Trait("Category","E2E")]`; they require provider credentials.

## Git

- One task = one branch = one PR.
- Stage explicit paths (`git add <path>`). Never `git add -A` or `git add .`.
- No emojis in commits, PR titles, or PR bodies. Technical prose only.
- Commit message: what changed and why, not a narration of the process.
- Never commit unless the task packet says to.

## Do not

- **Do not port `reference/pi/packages/coding-agent/src/core/extensions/` mechanically.**
  The extension host is a redesign, not a translation. It is governed by `docs/extension-api.md`.
  If that document does not yet cover your case, stop and report.
- Do not change the wire protocol. `Pi.Protocol` must stay byte-compatible with upstream
  `PROTOCOL_VERSION = 1`, including CBOR framing.
- Do not add a NuGet dependency without an approved entry in `docs/dependencies.md`.
- Do not introduce a Node.js dependency anywhere in the shipping product.
- Do not upgrade the pinned upstream reference. That is a deliberate, separate decision.
  The pin is recorded in `reference/PINNED` and enforced in CI by commit SHA. Changing it
  means updating the submodule pointer and that file in the same commit.
