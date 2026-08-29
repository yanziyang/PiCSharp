# D4 — Session File Format Compatibility

**Status:** Proposed — sign off before T6.2
**Upstream reference:** `reference/pi/packages/coding-agent/src/core/session-manager.ts` (1,716 LOC)

---

## Decision

**Stay byte-compatible with Pi's session format.** PiCSharp reads and writes the same JSONL files as
upstream, with no schema changes and no added fields.

### Why

- The format is small, stable and fully specified in one file — compatibility is nearly free.
- It preserves the user's existing history. Sessions are long-lived work artefacts; silently
  orphaning them is a serious regression, not a migration detail.
- It keeps `pi-share`, `/share`, HTML export and any third-party session tooling working.
- It permits running PiCSharp and upstream Pi against the same session directory during the port —
  which is itself a strong differential oracle for T6.2.

The cost is that we inherit upstream's quirks, including the optional `version` field described
below. That is a good trade.

---

## The format

One session is one file: `~/.pi/agent/sessions/<project>/{timestamp}_{sessionId}.jsonl`.

- Newline-delimited JSON, **append-only** in normal operation.
- **Line 1** is a `SessionHeader`. **Every subsequent line** is a `SessionEntry`.
- Entries form a **tree**, not a list — each carries `parentId`, and `null` marks a root. Branching
  is how `/tree`, forking and navigation work.
- Rewrite-in-place happens only on structural operations (fork, tree navigation); see
  `_rewriteFile()` upstream. Normal turns append.

### Header

```ts
{ type: "session", version?: number, id, timestamp, cwd, parentSession? }
```

`version` is **absent in v1 sessions**. Absent means v1 — do not default it to 0, and do not write it
where upstream does not. This is the single most likely place to break compatibility by accident.

### Entry base

```ts
{ type: string, id: string, parentId: string | null, timestamp: string }
```

### The nine entry types

| `type` | Payload | Notes |
|---|---|---|
| `message` | `message: AgentMessage` | the conversation itself |
| `thinking_level_change` | `thinkingLevel: string` | |
| `model_change` | `provider`, `modelId` | |
| `compaction` | `summary`, `firstKeptEntryId`, `tokensBefore`, `details?`, `usage?`, `fromHook?` | `fromHook` absent means Pi-generated |
| `branch_summary` | `fromId`, `summary`, `details?`, `usage?`, `fromHook?` | |
| `custom` | `customType`, `data?` | extension state; **excluded** from LLM context |
| `custom_message` | `customType`, `content`, `details?`, `display` | **included** in LLM context |
| `label` | `targetId`, `label: string \| undefined` | `undefined` clears the label |
| `session_info` | `name?` | display name |

`custom` versus `custom_message` is a semantic distinction that extensions depend on: the first
persists state invisibly, the second injects content into the model's context. Preserve both exactly.

---

## C# modelling

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionMessageEntry), "message")]
[JsonDerivedType(typeof(ThinkingLevelChangeEntry), "thinking_level_change")]
[JsonDerivedType(typeof(ModelChangeEntry), "model_change")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
[JsonDerivedType(typeof(BranchSummaryEntry), "branch_summary")]
[JsonDerivedType(typeof(CustomEntry), "custom")]
[JsonDerivedType(typeof(CustomMessageEntry), "custom_message")]
[JsonDerivedType(typeof(LabelEntry), "label")]
[JsonDerivedType(typeof(SessionInfoEntry), "session_info")]
public abstract record SessionEntry
{
    public required string Id { get; init; }
    public required string? ParentId { get; init; }
    public required string Timestamp { get; init; }
}
```

Discriminator strings must match the table exactly. Use a source-generated
`JsonSerializerContext`; reflection-based polymorphism is not AOT-safe.

### Rules

- **`type` must serialise first.** `System.Text.Json` emits the discriminator first for polymorphic
  types; verify it in a round-trip test rather than assuming.
- **`undefined` ≠ `null`.** `label: undefined` clears; a written `null` is different. Use
  `JsonIgnoreCondition.WhenWritingNull` for optional fields, and never write a field upstream omits.
  See `docs/translation-patterns.md`.
- **Preserve unknown fields.** A session written by a newer Pi may carry fields we do not model.
  Capture them with `[JsonExtensionData]` and write them back untouched. Without this, opening an
  existing session in PiCSharp silently destroys data.
- **`timestamp` is a string**, in upstream's exact format. Do not model as `DateTimeOffset` and
  re-serialise — round-trip the string.
- **Preserve line order.** JSONL order carries meaning. Never sort.
- **Append with the same durability semantics.** Upstream uses `appendFileSync`. Match the flush
  behaviour; a crash must not truncate a partially-written entry.

---

## File locking

Upstream uses `proper-lockfile` to coordinate concurrent sessions in one working directory. Match its
**on-disk protocol** — the lock directory name and staleness rules — not just the intent, or an
upstream Pi and a PiCSharp running side by side will corrupt a session. Read
`node_modules/proper-lockfile` in the reference tree for the exact semantics before implementing.

---

## Verification

T6.2 is not complete until all four pass:

1. **Round-trip corpus.** A set of real session files — v1 (no `version`), branched, compacted,
   containing custom entries — read and rewritten by PiCSharp are **byte-identical** to the input.
2. **Cross-read.** A session written by PiCSharp opens correctly in upstream Pi, and vice versa,
   including `/tree` navigation and fork.
3. **Unknown-field survival.** A file with injected unknown fields round-trips with them intact.
4. **Concurrent access.** Upstream Pi and PiCSharp appending to one session directory neither
   corrupt nor deadlock.

Build the corpus for (1) during T0.2 by capturing sessions from real upstream use. Synthetic files
will miss the quirks that matter.
