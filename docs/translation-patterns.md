# TypeScript → C# Translation Patterns

**Status:** Proposed — sign off before wave 2
**Referenced by:** `AGENTS.md`, `extension-api.md`, `session-format.md`, `dependencies.md`

---

## Why this document exists

Forty-plus delegated tasks will each independently decide how to render a TypeScript discriminated
union, how to treat `undefined` versus `null`, and how to model a streamed async iterable. Left
unfixed, the result is forty locally-reasonable answers that do not compose — and inconsistency
across independently-generated code is the dominant quality risk in a delegated port.

Each pattern below has **one** resolution. Applying a different one is a defect, not a preference.
If a pattern genuinely does not fit a site, stop and amend this document — do not improvise locally.

---

## 1. `undefined` vs `null` — the highest-risk pattern

Upstream `tsconfig` sets `strict: true` but **not** `exactOptionalPropertyTypes`. `undefined` and
`null` are distinct on the wire and often semantically different.

Worked example from `packages/ai/src/types.ts`:

```ts
export interface Usage {
  /** Set to a number (possibly 0) by providers that expose a reasoning breakdown;
      left undefined by providers that don't. */
  reasoning?: number;
}
```

`reasoning: 0` means "this provider reports reasoning tokens, and there were none".
`reasoning: undefined` means "this provider does not report reasoning at all". Collapsing them to
`0` destroys the distinction and silently corrupts cost and usage reporting.

**Resolution:**

| TS | C# | JSON behaviour |
|---|---|---|
| `x?: T` (absent means "no information") | `T? X { get; init; }` + `[JsonIgnore(Condition = WhenWritingNull)]` | omitted when null |
| `x: T \| null` (explicit null is meaningful) | `T? X { get; init; }`, **no** ignore condition | writes `null` |
| `x?: T \| null` (both meaningful) | `JsonValue?` or an explicit `Optional<T>` wrapper | preserves all three states |

Never write a property upstream omits. Never omit one upstream writes. Round-trip tests must assert
the **exact key set**, not just the values.

---

## 2. Discriminated unions

TS uses a literal `type` field. C# uses polymorphic serialisation with source generation.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BashToolCallEvent), "bash")]
[JsonDerivedType(typeof(ReadToolCallEvent), "read")]
public abstract record ToolCallEvent { ... }
```

Rules:
- Discriminator strings match the TS literal **exactly**, including underscores.
- Never reflection-based. `IsAotCompatible` is on everywhere, so reflection-driven polymorphic
  serialisation is out. Use source generation where a fixed shape is being serialised, and the
  document model (§2.1) where the shape is dynamic.

### 2.1 Which JSON model — recorded after review of the wave 1-5 port

The port settled on a split that this document originally did not describe. It is the right split,
so it is recorded here rather than corrected in the code.

| Layer | Model | Why |
|---|---|---|
| `Pi.Protocol` wire types | Hand-rolled strict value model (`IReadOnlyDictionary<string, object?>`) with explicit `EnsureKeys` validation | `StrictObject` means unknown fields are *rejected*. `System.Text.Json` ignores them by default, which is a wire-compatibility bug. The explicit model makes rejection the default. |
| Provider request/response payloads (`Pi.Ai`) | `JsonNode` / `JsonObject` | Provider payloads are dynamic and differ per provider. `JsonNode` mirrors the TypeScript object model structurally, preserves `undefined` versus `null` naturally, and avoids schema drift between C# records and upstream shapes. |
| Fixed internal DTOs | Source-generated `JsonSerializerContext` | Where a shape is genuinely fixed and ours. |

**Known cost, accepted.** `JsonNode` sits on the streaming hot path: one object tree is allocated per
SSE delta. That matches upstream, which calls `JSON.parse` per delta, so the cost model is faithful
rather than a regression. Do not "optimise" it into a typed parser without a benchmark showing a real
problem — the fidelity is worth more than the allocations.

**What this rules out.** Do not model provider payloads as C# records. Provider shapes change upstream
without notice, and a record set would silently drop fields that `JsonNode` carries through.
- Unions of primitives (`string | (TextContent | ImageContent)[]`) get a custom converter, not
  `object`. Model as a small struct with an explicit discriminator.
- Closed string unions (`StopReason = "pending" | "stop" | ...`) become an `enum` with
  `[JsonStringEnumConverter]` and **explicit** `[JsonPropertyName]` per member — the C# names are
  PascalCase, the wire names are not.

---

## 2.2 Type-level inference has no C# equivalent — validate at runtime instead

Upstream uses conditional and mapped types to derive one type from another at compile time.
`pi-telemetry` is the clearest case: `InferStartAttributes<T>`, `ExactTelemetryAttributes<Schema, Name>`
and `TelemetrySchemaSpanStartAttributes<Schema, Name>` derive an exact attribute shape from a schema
*value*. C# generics cannot express this, and no amount of cleverness will make them.

**Resolution, applied consistently:**

1. **Port the schema as data.** The schema definitions are runtime values and port directly.
2. **Port the functions with loose signatures.** Take the span name and an attribute dictionary.
   Check what the TypeScript actually does at runtime before agonising: `startAiSpan` is a
   passthrough to `startSpan` plus a cast. The entire generic apparatus is compile-time only, so the
   runtime port is trivial.
3. **Recover the guarantee at runtime.** Validate attributes against the schema definition and throw
   on an unknown name, a missing required attribute or a wrong value type. Then test that validation.

This trades a compile-time guarantee for a runtime one. That is a real loss, and it is the honest
cost of the language difference — do not paper over it by loosening the schema instead. The
validation must be strict enough that a wrong attribute fails a test rather than reaching a provider.

**Do not** attempt source generators for this. A generator that reproduces the inference would be far
more machinery than the guarantee is worth, and it would have to be maintained against upstream
schema changes.

## 3. Naming and the wire

C# members are PascalCase; the wire is camelCase. The wire always wins.

```csharp
[JsonPropertyName("cacheWrite1h")]
public int? CacheWrite1h { get; init; }
```

Set a global naming policy **and** annotate individual properties where the transformation is not a
plain camelCase↔PascalCase swap (`cacheWrite1h`, `toolCallId`, `firstKeptEntryId`). Do not rely on
the policy alone for anything with digits or acronyms.

---

## 4. Cancellation

`AbortSignal` → `CancellationToken`, everywhere, no exceptions.

- `signal: AbortSignal | undefined` → `CancellationToken?`
- `signal.aborted` → `token.IsCancellationRequested`
- `abort()` → `Abort()` over an internal `CancellationTokenSource`
- Every public async API takes a `CancellationToken`, last parameter, defaulted.
- Upstream aborts surface as a `StopReason.Aborted`, not an exception. **Catch
  `OperationCanceledException` at the agent-loop boundary and map it** — do not let it propagate
  where TS would have returned a value.

---

## 5. Streaming

`AsyncIterable<T>` → `IAsyncEnumerable<T>` with `[EnumeratorCancellation]`.

```csharp
public async IAsyncEnumerable<StreamEvent> StreamAsync(
    Request request,
    [EnumeratorCancellation] CancellationToken ct = default) { ... }
```

- Preserve event ordering exactly. Never buffer-and-reorder.
- Never parallelise a stream pipeline. Ordering is semantic.
- Use `System.Threading.Channels` for fan-out; do not hand-roll producer/consumer.
- **UTF-8 boundaries:** a code point may split across chunks. Upstream handles this in
  `api/transform-messages.ts`. Decode with a stateful `Decoder`, never `Encoding.UTF8.GetString`
  per chunk. This is a real defect that only fixture replay catches.

---

## 6. Partial JSON parsing

Tool arguments stream as incomplete JSON. Upstream's `packages/ai/src/utils/json-parse.ts` uses
`partial-json` **plus a `repairJson` fallback** for malformed provider output.

**Port `json-parse.ts` in full, including the repair path.** A resumable `Utf8JsonReader` loop
reproduces the happy path only, and the repair path exists precisely because some providers emit
JSON that needs it. Do not add a NuGet dependency for this; do not simplify it away.

Verify against recorded fixtures with genuinely malformed streams — see `differential-testing.md §3`.

---

## 7. JSON Schema emission

Tool parameter schemas cross the wire to providers. Upstream generates them from typebox.

**The emitted JSON must be byte-identical to typebox's output for the same shape** — key order,
`additionalProperties` placement, how optionality is expressed. Providers behave differently on
cosmetic differences, and it is not obvious from reading either side.

Golden-test the emitter against typebox output for every tool schema in the repo before T2.1 closes.
This is the highest-risk item in `dependencies.md §4`.

---

## 8. Numbers

JavaScript numbers are IEEE-754 doubles. TypeScript's `number` does not distinguish integers.

- Token counts, costs, indices, timestamps → choose `int`/`long`/`decimal` deliberately per site and
  **state the choice in the PR**.
- Money and cost: `decimal`. Never `double`.
- Anything compared for equality across the boundary: match upstream's precision, and normalise
  before comparison (`differential-testing.md §Normalisation`).

---

## 9. Errors

TS throws arbitrary values and frequently returns error *results* rather than throwing.

- Where upstream **returns** an error shape, return it. Do not convert to an exception.
- Where upstream **throws**, throw a typed exception from a `PiException` hierarchy.
- Preserve error **messages verbatim** where tests or users depend on them.
- Never swallow. Never replace a specific error with a generic one.

---

## 10. Objects, maps and ordering

- TS `Record<string, T>` → `Dictionary<string, T>`, but **insertion order is observable** in
  JavaScript for string keys. Where upstream iterates a record and order affects output, use an
  order-preserving structure and say so at the declaration.
- TS `Map` → `Dictionary`; TS `Set` → `HashSet`.
- Never sort a collection that upstream does not sort. Session entries, SSE events and rendered
  output all carry order semantics.

---

## 11. Mutable event payloads

The first place we deliberately choose classes over records: extension event payloads whose contract is
in-place mutation (`event.input`). See `extension-api.md §4.1`. Mark them clearly:

```csharp
/// <summary>Mutable by contract: handlers rewrite tool arguments in place.</summary>
public sealed class BashToolCallEvent : ToolCallEvent
{
    public BashToolInput Input { get; set; } = default!;
}
```

The second, added 2026-09-13, is marked tokens (`src/Pi.Tui/Marked/`). marked's lexer edits tokens after
creating them: it merges paragraph text, fills child token lists from its inline queue, sets `loose` and
inserts checkboxes. `markdown.ts` edits `token.text` as well. In-place mutation is their contract too.

Everything else is a `record` with `init` accessors.

---

## 12. Strings and text

JavaScript strings are UTF-16 and index by code unit — as does C#, so most indexing ports directly.
But:

- Anything measuring *display width* must use grapheme clusters, not `string.Length`. Use
  `StringInfo` / `Rune` plus the East-Asian width table (`tui-strategy.md §What .NET makes easier`).
- `String.Length` in TS and `string.Length` in C# agree; `[...str].length` in TS is a **code point**
  count and maps to `Rune` enumeration, not `Length`.
- Timestamps are round-tripped as **strings**, never reformatted (`session-format.md`).

---

## 13. Callbacks and closures

TS options objects containing functions → explicit `Func<>` / `Action<>` parameters, not builders and
not interfaces. Preserves the call shape and keeps extension ports mechanical.

```csharp
ValueTask<SessionResult> NewSessionAsync(
    string? parentSession = null,
    Func<ISessionManager, CancellationToken, ValueTask>? setup = null,
    CancellationToken ct = default);
```

---

## 14. Async return types

- Public API returning a value: `ValueTask<T>` where the synchronous path is common, `Task<T>`
  otherwise. Be consistent within a file.
- Fire-and-forget in TS (`void` returning, un-awaited): model explicitly and document it. **Never
  silently make an awaited call fire-and-forget** — it changes ordering on the interception path,
  which is exactly where extension semantics live.

---

## 15. Regular expressions

Added 2026-09-13 for the marked port (`src/Pi.Tui/Marked/`), the first regex-heavy translation in this
repository. JavaScript and .NET regular expressions look alike and differ in exactly the places a lexer
depends on, so **a pattern copied verbatim is a defect until shown otherwise.**

**Anchors and character classes.**

- `$` without the `m` flag matches only at the end of input in JavaScript. In .NET, `$` also matches
  before a final `\n`. Translate to `\z`.
- With the `m` flag, JavaScript treats `\n`, `\r`, U+2028 and U+2029 as line terminators for `^` and
  `$`. .NET `RegexOptions.Multiline` treats only `\n` that way.
- `.` without the `s` flag excludes `\n`, `\r`, U+2028 and U+2029 in JavaScript; in .NET it excludes
  only `\n`. Translate to `[^\n\r\u2028\u2029]`.
- `\d`, `\w` and `\b` are ASCII in JavaScript and Unicode-aware in .NET. Translate to `[0-9]`,
  `[A-Za-z0-9_]`, and lookarounds over that class.
- `\s` differs at two code points: JavaScript includes U+FEFF and excludes U+0085, and .NET is the
  reverse. Translate to `[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]`.
- Do not reach for `RegexOptions.ECMAScript`. Its `\s` is ASCII-only while JavaScript's includes
  Unicode spaces, so it reproduces neither. Translate classes explicitly.

**Unicode.**

- `\p{…}` under the `u` or `v` flag matches whole code points in JavaScript. .NET regex matches UTF-16
  code units, so a property class never matches an astral character — and most emoji are `\p{So}`.
  Where a pattern classifies the character beside a delimiter, classify the code point in code
  (`Rune.GetUnicodeCategory`) or match surrogate pairs explicitly, and test it with emoji.
- V8 and .NET can ship different Unicode data. Recorded fixtures catch the difference; do not paper
  over it.

**Flags, state and composition.**

- `i` becomes `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`. Never culture-sensitive.
- A JavaScript regex with `g` or `y` carries state in `lastIndex`. .NET `Regex` is stateless: use
  `Match(input, startat)` and `NextMatch()`, and `\G` for sticky matching. Never keep match state on a
  shared object.
- In replacement strings, JavaScript `$&` is .NET `$0`; `$1` and `$$` are the same.
- Both support lookbehind. Where the source feature-detects it, port the branch V8 takes.
- When the source composes patterns at runtime, translate the **final** pattern rather than its
  fragments. For marked those are recorded in `reference/marked/composed-rules.gfm.json`.

**Performance and Native AOT.**

- Prefer `[GeneratedRegex]` for fixed patterns: it is source-generated, fast, and safe under Native
  AOT. `RegexOptions.Compiled` needs dynamic code and runs interpreted under Native AOT.
- Do not use `RegexOptions.NonBacktracking` to defend against slow patterns. On .NET 10 it throws
  `NotSupportedException` for lookahead, lookbehind and backreferences, which JavaScript patterns use
  freely.
- **Never port a `src = src.substring(n)` loop literally.** V8 slices strings in constant time; .NET
  `Substring` copies. A tokenizer that re-slices its remaining input after every token is quadratic in
  C#. Measured on 2026-09-13 in Release with 100-character tokens, the copies alone cost 46 ms at
  100 KB and 8,081 ms at 1 MB, including 418 gen-2 collections: ten times the input, 175 times the time.
  Track an offset instead.
- `Regex.Match(input, beginning, length)` matches a range as if it were its own string: `^` matches at
  `beginning`, lookbehind cannot see before it, and `$` and `\z` match at the end of the range.
  `Match.Index` stays relative to the whole input. `Regex.Match(input, startat)` is **not** equivalent:
  `^` does not match at `startat`, and lookbehind sees the text before it. That suits `lastIndex`
  iteration and is the wrong tool for substring semantics. Both verified on .NET 10, 2026-09-13.

The recorded oracle, not this list, is the arbiter. When a port proves a rule missing here, add it.

---

## Amending this document

If a site does not fit, that is a signal the pattern is wrong or incomplete — not licence to
improvise. Stop, report it in the PR, and amend here. A pattern applied inconsistently across forty
tasks is worse than a pattern that is slightly wrong everywhere, because only the second one can be
fixed in a single change.
