# Dependency Policy and npm → NuGet Mapping

**Status:** Proposed — sign off before T0.1 (it configures `Directory.Packages.props`)
**Referenced by:** `AGENTS.md` — "Do not add a NuGet dependency without an approved entry here."

---

## Policy

1. **Approved list only.** A package not in §2 or §3 requires a decision recorded here before use.
   A Codex task that wants a new dependency stops and reports; it does not add one.
2. **Exact versions, centrally managed.** `Directory.Packages.props` with
   `ManagePackageVersionsCentrally`. No floating ranges, no per-project versions. This mirrors
   upstream's own pinning discipline.
3. **AOT and trim compatible, without exception.** `IsAotCompatible` is set on every library. A
   dependency that emits trim warnings is rejected, not suppressed.
4. **Prefer the base class library.** .NET ships equivalents for much of what Pi takes from npm.
   Every avoided dependency is avoided supply-chain surface.
5. **Prefer porting small, well-tested utilities** over adopting a large dependency with a different
   behavioural contract. Behavioural difference in a port is a defect; 200 lines of ported utility is
   cheaper to verify than a library that is *nearly* the same.
6. **Licence review is mandatory** before adding. Record the licence in §3.

---

## 2. Replaced by the base class library — add nothing

| Upstream npm | Purpose | .NET |
|---|---|---|
| `undici` | HTTP client | `System.Net.Http.HttpClient` |
| `http-proxy-agent`, `https-proxy-agent` | Proxy support | `HttpClientHandler.Proxy` / `WebProxy` |
| `cross-spawn` | Process spawning | `System.Diagnostics.Process` |
| `partial-json` | Incremental JSON parsing of streamed tool arguments | `Utf8JsonReader` in a resumable loop. **Do not add a dependency for this** — see `translation-patterns.md`. |
| `minimatch` | Glob matching | `Microsoft.Extensions.FileSystemGlobbing` |
| `chalk` | ANSI colour | `Pi.Tui` provides styling directly |
| `typebox` | Schema definition and validation | `System.Text.Json` source generation, plus our JSON Schema emitter (§4) |

---

## 3. Approved NuGet packages

| Package | Replaces | Used by | Licence | Note |
|---|---|---|---|---|
| `AWSSDK.BedrockRuntime` | `@aws-sdk/client-bedrock-runtime`, `@smithy/node-http-handler` | `Pi.Ai` (T2.7) | Apache-2.0 | Adopted specifically for SigV4 signing and credential resolution. Hand-rolling SigV4 is a defect factory. |
| `YamlDotNet` | `yaml` | `Pi.AgentCore`, `Pi.CodingAgent` | MIT | Verify round-trip fidelity against upstream for frontmatter in skills and prompt templates. |
| `DiffPlex` | `diff` | `Pi.AgentCore`, `Pi.CodingAgent` (edit tool) | Apache-2.0 | **Must produce upstream-identical hunks** — the edit tool's behaviour depends on it. Verify early; if it diverges, port `diff` instead. |
| `Markdig` | `marked` | `Pi.Tui`, `Pi.CodingAgent` | BSD-2-Clause | Must support the transformer hook `registerMarkdownTransformer` needs. |
| `NuGet.Versioning` | `semver` | `Pi.CodingAgent` (package manager) | Apache-2.0 | npm and SemVer 2.0 range syntax differ. Verify against upstream's range tests, or port `semver`. |
| `SkiaSharp` | `@silvia-odwyer/photon-node` | `Pi.CodingAgent` (image handling) | MIT | See §5 — deliberately chosen over ImageSharp. **Not needed for T5.5**: terminal images shipped in `f2a045f` with no dependency at all, since Kitty/iTerm2 encoding is escape-sequence building and dimension probing is magic-byte header reading. Wave 6 image handling only. |
| `TextMateSharp` | `highlight.js` | `Pi.CodingAgent` (code rendering) | MIT | Grammar-based; output will not match `highlight.js` token-for-token. Golden tests must be regenerated, not ported. |
| `xunit.v3` | `vitest` | all test projects | Apache-2.0 | Runs on Microsoft.Testing.Platform, which carries its own runner: `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are **not** required. See `solution-layout.md §7`. |
| `Verify.Xunit` | snapshot assertions | test projects | MIT | For golden buffers and normalised event sequences. |

---

## 4. Ported rather than depended on

Small, behaviour-critical, or without a faithful .NET equivalent. Each becomes a task.

| Upstream | LOC | Target | Why port |
|---|---|---|---|
| `ignore` | small | `Pi.CodingAgent/Utils/GitIgnoreMatcher.cs` | `.gitignore` semantics are subtle (negation, directory-only, precedence). No .NET library matches exactly, and the grep/find tools depend on it. |
| `get-east-asian-width` | data table | `Pi.Tui/Text/EastAsianWidth.cs` | Pure Unicode data. Port the table; correctness is testable against the same data. |
| `proper-lockfile` | small | `Pi.CodingAgent/Core/SessionLock.cs` | Must match the **on-disk protocol**, not just the intent — upstream Pi and PiCSharp may share a session directory. See `session-format.md`. |
| `hosted-git-info` | small | `Pi.CodingAgent/Packages/GitUrlParser.cs` | Narrow, well-specified parsing. |
| typebox JSON Schema emission | — | `Pi.Ai/Schema/` | Tool parameter schemas go to providers on the wire. The emitted JSON must be byte-identical to typebox's output for the same shape, or provider behaviour changes. Highest-risk item in this table. |

---

## 5. Notable decisions

**`SkiaSharp` over `ImageSharp`.** ImageSharp moved to the Six Labors Split License, which requires a
commercial licence for many organisations. SkiaSharp (MIT wrapper over BSD Skia) avoids the question.
If image handling turns out to need only dimension probing — which is most of what
`terminal-image.ts` does — reconsider dropping the dependency entirely and porting the header
parsers for PNG, JPEG and GIF. **Confirm actual usage during T5.5 before committing to either.**

**No mermaid equivalent.** Upstream uses `grok-mermaid` for diagram rendering in the transcript.
There is no comparable .NET package. Decide during T6.8: either render mermaid blocks as plain
fenced code (acceptable degradation, no dependency), or shell out. Do **not** add a JavaScript
runtime to render diagrams — that reintroduces the dependency the whole port exists to remove.

**No `jiti` equivalent, by design.** Extensions are compiled .NET assemblies. There is no runtime
TypeScript path. See `extension-api.md §6`.

---

## 6. Adding a dependency

1. Confirm nothing in §2 already covers it.
2. Confirm it is AOT and trim clean — build a sample with `PublishAot` and check for warnings.
3. Record licence, purpose, and consuming project in §3.
4. If it replaces upstream behaviour, state how equivalence will be verified.
5. Get sign-off, then add to `Directory.Packages.props`.

Steps 1–4 in a PR description; a dependency added without them is reverted rather than debated.
