# Spike — `marked` → Markdig divergence

**Status:** Preliminary, run 2026-08-31. 15 probe inputs of a planned full sweep.
**Question:** `docs/dependencies.md §3` approves Markdig to replace `marked`. How far apart are they
on the constructs `components/markdown.ts` actually inspects?
**Verdict so far:** **Viable.** Four divergences found, all enumerable and mechanical. No construct
where Markdig cannot represent what `marked` represents.

---

## Why this spike exists

Markdig was approved on paper, with one condition: *"must support the transformer hook
`registerMarkdownTransformer` needs."* That condition is **satisfied and no longer a risk** —

```ts
export type MarkdownTransformer = (markdown: string, context: MarkdownTransformContext) => string;
```

the hook takes source text and returns source text, so it sits upstream of parsing and is
parser-agnostic. Extensions never see a token.

The real risk was never the hook. It is that `markdown.ts` inspects **19 token types** from `marked`'s
vocabulary, and a different parser produces a different tree. That risk is measurable, so it should be
measured before 1,015 LOC of renderer is built on top of it. `dependencies.md` already applies this
pattern to `DiffPlex` — *"Must produce upstream-identical hunks. Verify early; if it diverges, port
`diff` instead."* This is the same move for Markdig.

## Method

`marked@18.0.5` (the pinned upstream version) and `Markdig 0.44.0`, each given the same 15 inputs,
each walked depth-first emitting node type names. Harness in `tools/spikes/markdig/`:

```
marked-dump.mjs      writes inputs.json + marked.json
MarkdigDump.cs       reads inputs.json, writes markdig.json
```

Markdig pipeline: `UsePipeTables().UseEmphasisExtras().UseAutoLinks()` — `marked` is GFM by default,
so the equivalents must be opted into explicitly.

## Result

Ten of fifteen constructs map cleanly:

| `marked` | Markdig |
|---|---|
| `paragraph` | `ParagraphBlock` |
| `text` | `LiteralInline` |
| `codespan` | `CodeInline` |
| `code` | `FencedCodeBlock` |
| `html` | `HtmlBlock` |
| `heading` | `HeadingBlock` |
| `hr` | `ThematicBreakBlock` |
| `br` | `LineBreakInline` |
| `list`, `list_item` | `ListBlock`, `ListItemBlock` (+ a `ParagraphBlock` wrapper per item) |
| `table` | `Table` / `TableRow` / `TableCell` (Markdig gives more structure than `marked`'s header/rows arrays) |

### The four divergences

**1. `del` is not a distinct node in Markdig.**

```
~~gone~~ and ~single~
  marked : paragraph del text text del text
  markdig: ParagraphBlock EmphasisInline LiteralInline LiteralInline EmphasisInline LiteralInline
```

Markdig models strikethrough as `EmphasisInline` distinguished by its delimiter character and count.
The renderer's `case "del"` becomes a property check, not a type check.

Note both parsers emit **two** strikethroughs here — both treat `~single~` as del. That is precisely
why upstream overrides `Tokenizer.del` with `STRICT_STRIKETHROUGH_REGEX`, and the same override is
needed on the Markdig side. The requirement transfers; only its expression changes.

**2. `escape` is erased.**

```
\*not em\*
  marked : paragraph escape text escape
  markdig: ParagraphBlock LiteralInline LiteralInline
```

`marked` emits explicit `escape` tokens; Markdig resolves them into literal text. `markdown.ts` has a
`case "escape"`. **Needs confirming**: if the renderer only emits the escaped character, the resolved
literal is equivalent and this is free. If it treats escapes specially, the adapter must recover them.

**3. `autolink` takes a different path.**

```
<http://e.com>
  marked : paragraph link text
  markdig: ParagraphBlock AutolinkInline
```

`marked` reuses the `link` token, which `markdown.ts` already handles. Markdig emits a distinct
`AutolinkInline` with no child literal, so it would fall through unhandled. The adapter must map it
onto the link path and synthesise the label.

**4. Soft line breaks inside blockquotes are preserved, not joined.**

```
> q1
> q2
  marked : blockquote paragraph text
  markdig: QuoteBlock ParagraphBlock LiteralInline LineBreakInline LiteralInline
```

`marked` joins the two lines into one text token; Markdig keeps the break as a node. This is the one
divergence with direct visual consequences — the terminal renderer wraps text to a width, and whether
a soft break survives changes where lines land.

## Conclusion so far

Markdig can represent everything `marked` represents for these constructs. The work is an **adapter
layer** that normalises Markdig's AST into the token vocabulary `markdown.ts`'s renderer already
expects, plus the strict-strikethrough override and the LaTeX inline/block tokens.

No finding so far argues for porting `marked` or hand-writing a parser.

## What remains

This covered 15 hand-picked constructs. It has **not** covered the inputs the 81 upstream cases
actually use, which is where the long tail of CommonMark edge cases lives. See
`ÍmplementationKit/packets/T5.8-markdig-spike.md`.
