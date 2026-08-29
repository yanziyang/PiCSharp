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
- Always source-generated (`JsonSerializerContext`). Reflection-based polymorphism is not AOT-safe
  and `IsAotCompatible` is on everywhere.
- Unions of primitives (`string | (TextContent | ImageContent)[]`) get a custom converter, not
  `object`. Model as a small struct with an explicit discriminator.
- Closed string unions (`StopReason = "pending" | "stop" | ...`) become an `enum` with
  `[JsonStringEnumConverter]` and **explicit** `[JsonPropertyName]` per member — the C# names are
  PascalCase, the wire names are not.

---

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

The one place we deliberately choose classes over records: extension event payloads whose contract is
in-place mutation (`event.input`). See `extension-api.md §4.1`. Mark them clearly:

```csharp
/// <summary>Mutable by contract: handlers rewrite tool arguments in place.</summary>
public sealed class BashToolCallEvent : ToolCallEvent
{
    public BashToolInput Input { get; set; } = default!;
}
```

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

## Amending this document

If a site does not fit, that is a signal the pattern is wrong or incomplete — not licence to
improvise. Stop, report it in the PR, and amend here. A pattern applied inconsistently across forty
tasks is worse than a pattern that is slightly wrong everywhere, because only the second one can be
fixed in a single change.
