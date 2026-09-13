# Markdig divergence spike

**Status:** complete, 2026-09-13
**Recommendation:** **GO, with an explicit adapter and five small compatibility pieces.**

> **Superseded 2026-09-13 — not acted on.** Scoping the port, a follow-up measurement against marked
> 18.0.5 found further renderer-visible divergences that the 109-case sweep below did not reach. None
> of them occurs in any of the 81 upstream cases, so a Markdig port built to this report would pass
> every upstream test and still mangle ordinary output. The project decided to **port marked's lexer
> instead** — specification in `reference/marked/`, port in `src/Pi.Tui/Marked/`, packet `T5.9`.
> Evidence, reproducible: `tools/spikes/markdig/probes-2026-09-13/`.
>
> Measured with Markdig 0.44.0 against marked 18.0.5, using the production strict-strikethrough
> tokenizer where it applies:
>
> - **Default emphasis extras corrupt plain text.** The pipeline in this report called
>   `UseEmphasisExtras()` with default options, which also enables superscript, subscript, inserted and
>   marked. `x^2 + y^2` becomes a superscript, `a==b==c` a highlight, `++i++` an insertion, `H~2~O` a
>   subscript; marked keeps all of them as text. None of the 109 inputs contained `==` or `++`, and the
>   only carets outside math sat in LaTeX rows set aside as the extension boundary.
> - **Autolinks.** Markdig autolinks `tel:+15551234`; marked leaves it as text. In
>   `write mailto:a@b.com`, Markdig swallows `mailto:` into the link, while marked leaves it visible and
>   links only the address. `AutoLinkOptions` has no scheme control.
> - **Strikethrough pairing.** marked's strict rule pairs from the first opener, so `~~a ~~b~~` strikes
>   `a ~~b`; Markdig strikes only `b`. marked rejects `~~a~~~` and `~~**a**~~~`, and Markdig strikes
>   both. Four of six pairing probes differ, and no post-parse check can repair a pairing already chosen.
> - **Tables.** marked splits the raw row before inline parsing, so a pipe inside a code span splits the
>   cell; Markdig keeps the code span whole. Markdig also reports a trailing column definition that is
>   not a column, keeps overflow cells unless `UseHeaderForColumnCount` is set, and leaves
>   `PipeTableDelimiterInline` nodes in paragraphs that do not become tables.
>
> Markdig passes trim and Native AOT analysis cleanly; that was never the problem. The problem is that a
> faithful adapter would re-implement marked's `url`, `del`, `table` and `splitCells` rules, plus
> upstream's LaTeX tokenizers, inside Markdig's extension model — at which point most GFM parsing is
> ported marked code anyway, without marked's verification path. Porting marked's lexer (~2,100
> TypeScript lines on pi's option path, MIT) makes every token difference a defect against a real
> oracle. The report below is kept as the record of what the first sweep found.

This spike answers whether the existing `markdown.ts` renderer can be fed by Markdig. It does not
port `markdown.ts`, and it does not add Markdig to Pi.Tui. The next packet should implement the
adapter only after carrying forward the required behaviors below.

## Scope and method

The source corpus is `reference/pi/packages/tui/test/markdown.test.ts`, which contains 81 `it`
blocks. The harvester found 94 upstream Markdown source inputs (including the `setText` state
transitions), including the three relevant
option contexts (`preserveOrderedListMarkers`, `renderLatex`, and `preserveBackslashEscapes`). The
15 original hand-picked probes were retained, so the run contains 109 cases in total.

The comparison is:

- `marked@18.0.5`, with the same strict-strikethrough override used by the production parser;
- `Markdig 0.44.0`, with `UsePipeTables`, `UseEmphasisExtras`, `UseAutoLinks`, `UseTaskLists`,
  `UsePreciseSourceLocation`, and the strict-strikethrough extension in the spike;
- depth-first type streams plus the source-sensitive properties needed by the adapter.

The Marked dump intentionally does **not** duplicate the production LaTeX tokenizer. That keeps this
packet a spike rather than a second `markdown.ts` port. Consequently, the LaTeX rows below identify
the known extension boundary: the recorded Marked stream is the core lexer stream, while the
production parser adds `latex`/`latexBlock` tokens. The Markdig side is proved separately in the
extension proof section.

The exact input, both recorded streams, options, and renderer-impact classification for every one of
the 109 cases are in `tools/spikes/markdig/divergences.json`. The raw parser outputs are also kept in
`inputs.json`, `marked.json`, and `markdig.json`.

## Result at a glance

The raw arrays differ for all cases because the vocabularies differ (`paragraph` versus
`ParagraphBlock`) and Markdig exposes container/wrapper nodes that Marked hides. That is expected and
is not itself a finding. The exhaustive ledger classifies the differences after applying the adapter
rules:

| Ledger classification | Cases | Meaning |
|---|---:|---|
| `no` | 91 | Structural or segmentation difference; renderer output is unchanged after mapping. |
| `yes` | 16 | A missing adapter/extension rule changes output for an upstream case or retained quote probe. |
| `conditional` | 1 | The upstream test runs with hyperlinks off; a hyperlink-capable terminal additionally needs bare-email autolinking. |
| `yes-if-unmapped` | 1 | Retained angle-autolink probe; the node must be routed to the renderer's link path. |

The `yes` rows are not reasons to reject Markdig. They are the concrete compatibility work that must
be part of the port.

## Renderer-visible findings

### 1. Blank lines at list boundaries and source list markers

`markdown.ts`'s list renderer does not add automatic spacing after a list. Marked emits a `space`
token for a blank line between list blocks; Markdig has no whitespace node. The adapter must use
source spans to synthesize the required top-level gap. The same source-span path is needed when
`preserveOrderedListMarkers` is true: Markdig exposes `Order` and `OrderedDelimiter`, but
`SourceBullet` is empty unless trivia tracking is enabled.

#### `test-06-01` — preserve source list markers

Input (JSON string):

```json
"  4. forth\n  3. third\n\n10) ten\n7) seven\n\n+ plus\n* star\n- minus\n+"
```

Marked:

```text
list → list_item → text → text → list_item → text → text → space → list → list_item → text → text → list_item → text → text → space → list → list_item → text → text → list → list_item → text → text → list → list_item → text → text → list → list_item
```

Markdig:

```text
ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListItemBlock → ParagraphBlock → LiteralInline → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListItemBlock → ParagraphBlock → LiteralInline → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListBlock → ListItemBlock
```

Options: `preserveOrderedListMarkers: true`. **Renderer impact: yes.** Without gap recovery the two
blank lines disappear; without marker recovery the output normalizes `4.`, `3.`, `10)`, `7)`, `+`,
`*`, `-`, `+`.

#### `test-10-01` — list/code/list boundaries

Input (JSON string):

````json
"1. First item\n\n```typescript\n// code block\n```\n\n2. Second item\n\n```typescript\n// another code block\n```\n\n3. Third item"
````

Marked:

```text
list → list_item → text → text → space → code → space → list → list_item → text → text → space → code → space → list → list_item → text → text
```

Markdig:

```text
ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → FencedCodeBlock → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → FencedCodeBlock → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline
```

**Renderer impact: yes.** The list renderer has no automatic trailing blank line, so the adapter
must preserve the source gaps before the code blocks and following lists.

#### `test-33-01` — list/table boundary

Input (JSON string):

```json
"# Test Document\n\n- Item 1\n  - Nested item\n- Item 2\n\n| Col1 | Col2 |\n| --- | --- |\n| A | B |"
```

Marked:

```text
heading → text → space → list → list_item → text → text → list → list_item → text → text → list_item → text → text → space → table
```

Markdig:

```text
HeadingBlock → LiteralInline → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ListItemBlock → ParagraphBlock → LiteralInline → Table → TableRow → TableCell → ParagraphBlock → LiteralInline → TableCell → ParagraphBlock → LiteralInline → TableRow → TableCell → ParagraphBlock → LiteralInline → TableCell → ParagraphBlock → LiteralInline
```

**Renderer impact: yes.** Heading spacing is recoverable by the renderer's existing heading rule;
the list-to-table gap is not, because neither list rendering nor table rendering inserts a leading
blank line.

#### `test-39-01` — list/table boundary plus math

Input (JSON string):

```json
"- Formula: $F_1 = u^2$\n\n| Value |\n| --- |\n| $\\mathbb{C}^3$ |"
```

Marked (core sweep):

```text
list → list_item → text → text → space → table
```

Markdig (core pipeline):

```text
ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → LiteralInline → LiteralInline → Table → TableRow → TableCell → ParagraphBlock → LiteralInline → TableRow → TableCell → ParagraphBlock → LiteralInline → LiteralInline → LiteralInline
```

**Renderer impact: yes.** The adapter needs both the list-to-table source gap and the LaTeX inline
extension so the list contains `F₁ = u²` and the table contains `ℂ³`.

### 2. Blockquote soft breaks

The two upstream multiline blockquote tests require **two quote-border lines**, including for the
lazy continuation form. Marked carries the newline inside one `text` token; Markdig emits a
`LineBreakInline` with `IsHard == false`. The adapter must concatenate it as `"\n"` before the
blockquote renderer wraps the content. It must not discard the node or join the lines.

#### `test-60-01` — lazy continuation

Input: `">Foo\nbar"`

Marked: `blockquote → paragraph → text`
Markdig: `QuoteBlock → ParagraphBlock → LiteralInline → LineBreakInline → LiteralInline`

**Renderer impact: yes.** Upstream asserts two `│ ` lines, both with quote italic styling. The
required mapped text is `Foo\nbar`.

#### `test-61-01` — explicit multiline quote

Input: `">Foo\n>bar"`

Marked: `blockquote → paragraph → text`
Markdig: `QuoteBlock → ParagraphBlock → LiteralInline → LineBreakInline → LiteralInline`

**Renderer impact: yes.** Same two-line requirement and the same `IsHard == false` mapping.

The retained `probe-blockquote` (`"> q1\n> q2"`) produces the same stream shape and independently
confirms the rule.

### 3. Backslash escapes

#### `test-46-01` — default normalization

Input (JSON string): `"\"\\\""` (the three source characters are quote, backslash, quote).
Marked: `paragraph → text → escape`
Markdig: `ParagraphBlock → LiteralInline → LiteralInline`

**Renderer impact: no** when `preserveBackslashEscapes` is false. `markdown.ts` emits
`token.text`, so both paths render two quote characters.

#### `test-47-01` — source-preserving option

Input (same JSON string), options: `preserveBackslashEscapes: true`.
Marked: `paragraph → text → escape`
Markdig: `ParagraphBlock → LiteralInline → LiteralInline`

The Marked `escape` node has `raw = "\\\""` and `text = "\""`. Markdig's second literal has
resolved `content = "\""`, `isFirstCharacterEscaped = true`, and a precise source span covering
the backslash and quote. **Renderer impact: yes unless the adapter recovers that span.** With source
span recovery (or Markdig trivia tracking), the adapter emits the original `token.raw` equivalent;
without it, it emits `""` instead of `"\"`.

The retained `probe-escape` (`\\*not em\\*`) is **renderer-neutral** under the default option for
the same reason: `markdown.ts` uses the resolved `token.text`.

The production branch that closes this question is:

```ts
case "escape":
  result += applyTextWithNewlines(
    this.options.preserveBackslashEscapes ? token.raw : token.text
  );
```

### 4. Autolinks and bare email addresses

#### `probe-autolink` — angle URL

Input: `"<http://e.com>"`
Marked: `paragraph → link → text`
Markdig: `ParagraphBlock → AutolinkInline`

**Renderer impact: yes-if-unmapped.** `markdown.ts` has a `link` path, not an `AutolinkInline`
path. Map `AutolinkInline.Url` to `href` and use the URL as the label (or normalize it to a
`LinkInline`-equivalent token) before the existing link renderer sees it.

#### `test-72-01` — bare email

Input: `"Contact user@example.com for help"`
Marked: `paragraph → text → link → text → text`
Markdig: `ParagraphBlock → LiteralInline`

The upstream test explicitly disables terminal hyperlinks and therefore passes as plain text: the
email appears once and no `mailto:` prefix is printed. **Renderer impact: conditional.** If
hyperlinks are enabled, Marked's link token reaches the OSC 8 branch with
`mailto:user@example.com`; the observed Markdig 0.44 `UseAutoLinks()` pipeline leaves the address
as a literal, so no OSC 8 link is emitted. Add a small bare-email inline parser or an equivalent
adapter post-pass if hyperlink-capable output is a requirement.

The ordinary bare-URL case is structural only: `test-73-01` and `test-78-01` produce a Markdig
`LinkInline` with `IsAutoLink == true`, `Url == "https://example.com"`, and a literal child label.

## LaTeX extension boundary

The six complete upstream math inputs below are renderer-visible if Markdig is run with only the
core comparison pipeline. This is an expected extension gap, not evidence that Markdig cannot host
the behavior. The current production Marked parser supplies `latex`/`latexBlock` tokens and the
tests assert the following output; the Markdig proof below demonstrates the corresponding extension
hooks.

### `test-34-01` — inline dollar and parenthesis forms

Input:

```text
A map $\mathbb{C}^3 \to \mathbb{C}^3$, $xy$, $x-y$, $-x$, $\frac{1}{2}$, and \(s \to \infty\).
```

Marked (core sweep): `paragraph → text → escape → text → escape → text`
Markdig: `ParagraphBlock → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline`

**Renderer impact: yes.** Production expected line: `A map ℂ³ → ℂ³, xy, x-y, -x, 1/2, and s → ∞.`

### `test-35-01` — display dollar form

Input:

```text
Before

$$\{3x+2y,\; x \in \{0, \pm 1\}\}$$

after
```

Marked (core sweep): `paragraph → text → space → paragraph → text → escape → text → escape → text → escape → text → escape → escape → text → space → paragraph → text`
Markdig: `ParagraphBlock → LiteralInline → ParagraphBlock → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline → ParagraphBlock → LiteralInline`

**Renderer impact: yes.** Production expected display line: `{3x+2y, x ∈ {0, ± 1}}`, with one blank line on each side.

### `test-36-01` — display bracket form

Input:

```text
Before

\[
E \approx \frac{0.1\ \text{lux}}{100\ \text{lm/W}}
\]

after
```

Marked (core sweep): `paragraph → text → space → paragraph → escape → text → escape → space → paragraph → text`
Markdig: `ParagraphBlock → LiteralInline → ParagraphBlock → LiteralInline → LineBreakInline → LiteralInline → LineBreakInline → LiteralInline → ParagraphBlock → LiteralInline`

**Renderer impact: yes.** Production renders the multi-line display (`0.1 lux`, the fraction, and
`100 lm/W`) rather than the escaped source paragraph.

### `test-37-01` — matrix display

Input:

```text
Consider the matrix

\[
A=
\begin{pmatrix}
\pi & 0\\
0 & \frac{1}{\pi}
\end{pmatrix}.
\]
```

Marked (core sweep): `paragraph → text → space → paragraph → escape → text → escape → text → escape`
Markdig: `ParagraphBlock → LiteralInline → ParagraphBlock → LiteralInline → LineBreakInline → LiteralInline → LineBreakInline → LiteralInline → LineBreakInline → LiteralInline → LiteralInline → LineBreakInline → LiteralInline → LineBreakInline → LiteralInline → LineBreakInline → LiteralInline`

**Renderer impact: yes.** Production expected matrix output is `A = ⎛ π │ 0   ⎞` and
`    ⎝ 0 │ 1/π ⎠.`.

### `test-38-01` — display operator

Input:

```text
\[
\lim_{x\to 0}\frac{\frac{\sin x}{x}-1}{\frac{e^x-1}{x}-1}=0
\]
```

Marked (core sweep): `paragraph → escape → text → escape`
Markdig: `ParagraphBlock → LiteralInline → LineBreakInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LiteralInline → LineBreakInline → LiteralInline`

**Renderer impact: yes.** Production expected output contains the stacked lower limit and fraction,
not the source delimiters.

### `test-39-01` — math inside list and table

The input and both streams are recorded above under the spacing finding. **Renderer impact: yes**
for both the missing list/table gap and the missing inline math extension.

### `test-45-02` — streamed dollar delimiter closes

This is the `setText` input harvested from the same upstream test after the pending source in
`test-45-01`.

Input: `"Map $\\mathbb{C}^3$"` (operation: `setText`)

Marked (core sweep): `paragraph → text`
Markdig: `ParagraphBlock → LiteralInline → LiteralInline → LiteralInline`

**Renderer impact: yes.** The production tokenizer now returns complete `latex`, and the upstream
assertion expects `Map ℂ³`; the core Markdig pipeline still has no math node. The extension must
transition from pending raw source to a complete math node on the next parse.

### Pending and disabled math cases

These are not additional parser blockers after the adapter preserves source spans:

- `test-41-02` (`Streaming $\\mathbb{C}^3`), `test-45-01` (`Map $\\mathbb{C}^3`): the
  production tokenizer marks incomplete math pending and the renderer emits raw source; Markdig's
  literal segmentation concatenates to the same source.
- `test-42-01` (`Map \\(\\mathbb{C}^3`) and `test-42-02` (`\\[\nx^2`): these are the two
  pending delimiter cases that are renderer-visible without the extension/source-preserving rule;
  their exact streams are below.
- `test-44-01` has `renderLatex: false`; production deliberately emits raw source, so it is
  structural-only once escapes/source spans are mapped.
- `test-41-01` is unsupported complete-looking math; `renderLatex` falls back to raw source and is
  structural-only.

#### `test-42-01` — pending inline delimiter

Input: `"Map \\(\\mathbb{C}^3"`
Marked (core sweep): `paragraph → text → escape → text`
Markdig: `ParagraphBlock → LiteralInline → LiteralInline → LiteralInline → LiteralInline`

**Renderer impact: yes without the extension.** The production pending `latex` branch emits the raw
`Map \\(\\mathbb{C}^3`; a core Markdig escape resolves `\\(` to `(`. The custom inline parser
must claim the opening delimiter, including its pending form, and retain raw source.

#### `test-42-02` — pending display delimiter

Input: `"\\[\nx^2"`
Marked (core sweep): `paragraph → escape → text`
Markdig: `ParagraphBlock → LiteralInline → LineBreakInline → LiteralInline → LiteralInline → LiteralInline`

**Renderer impact: yes without the extension.** Production expected lines are `\\[` and `x^2`;
the block parser must own the opening delimiter and preserve the pending raw block.

## Proofs written and run in the spike project

The proofs are executable code in `tools/spikes/markdig/MarkdigDump.cs`; they are not prose claims.
The generated `markdig.json.extensionProofs` is written only after all assertions pass.

### Strict strikethrough

`StrictStrikethroughExtension` inserts `StrictSingleTildeLiteralParser` before Markdig's
`EmphasisInlineParser`. The parser rejects a single-tilde pair as an emphasis candidate and lets the
double-tilde parser handle `~~...~~`.

Input: `"~~double~~ and ~single~"`

```text
Markdig: ParagraphBlock → EmphasisInline → LiteralInline → LiteralInline → LiteralInline
```

The `EmphasisInline` detail is `DelimiterChar == '~'` and `DelimiterCount == 2`; the single-tilde
text is one literal `~single~`. This is the required Markdig equivalent of the production strict
regex behavior.

### Inline and block LaTeX

The proof registers:

- Markdig's built-in `UseMathematics()` for `$x$` and `$$...$$`, producing `MathInline` and
  `MathBlock`;
- a custom `ParenLatexInlineParser : InlineParser` for `\\(...\\)`;
- a custom `BracketLatexBlockParser : BlockParser` and `BracketLatexBlock : FencedCodeBlock` for
  `\\[...\\]`.

The asserted output is:

```text
inline       : ParagraphBlock → MathInline → LiteralInline → ParenLatexInline
block dollar : MathBlock
block bracket: BracketLatexBlock
```

Thus both inline and block tokenization have a concrete Markdig extension host. The proof uses the
documented `IMarkdownExtension` setup hooks and parser insertion points; see Markdig's
[extension guide](https://xoofx.github.io/markdig/docs/advanced/creating-extensions) and
[inline parser guide](https://xoofx.github.io/markdig/docs/advanced/inline-parsers).

## Structural-only differences

These differences are recorded in the exhaustive ledger but do not change renderer output once the
adapter uses the listed properties.

| Marked shape | Markdig shape | Required normalization | Coverage |
|---|---|---|---|
| `heading` + inline tokens | `HeadingBlock` + inline children | `Level`, `IsSetext`, inline children | tests 66–69, plus `probe-setext` |
| `paragraph` | `ParagraphBlock` + inline children | Render its inline child list | tests 01, 11–17, 34–45, 48–59, 63–65 |
| `strong` / `em` | `EmphasisInline` | `DelimiterCount == 2` for strong; `== 1` for em; inspect delimiter char | tests 49, 65, 69 and strict probes |
| `del` | `EmphasisInline` | `DelimiterChar == '~' && DelimiterCount == 2` | `test-70-01`, `probe-strikethrough-strict` |
| single-tilde text | `LiteralInline` segments | concatenate; strict extension is already proved | `test-71-01`, `probe-strikethrough-spaced` |
| `list_item` | `ListItemBlock` + `ParagraphBlock` | `Order`, marker properties, task child, nested blocks | tests 02–17, 33, 39, 62 |
| loose-list `space` | `ListBlock.IsLoose == true` | let list renderer add inter-item blank lines | `test-08-01` |
| `checkbox` | `TaskList` + literal with one leading space | `Checked`; remove the task marker's consumed leading space | `test-09-01` |
| `table` | `Table` / `TableRow` / `TableCell` / paragraph children | header row, alignment, cell order, inline children | tests 18–32, 33, 39 |
| `code` | `FencedCodeBlock` (or `CodeBlock`) | `Info`, `Lines`, fence char/count; apply partial-fence trim | tests 17, 40, 43, 48, 50–55, 80–81 |
| `link` | `LinkInline` | `Url`, `IsImage`, child label; auto-link flags | tests 24, 25, 27, 73–78 |
| `html` | `HtmlBlock` / `HtmlInline` | raw source span or `Tag`; block versus inline | `test-79-01`, `test-80-01` |
| `br` | hard `LineBreakInline` | `IsHard == true` maps to `br`; keep `IsBackslash` | `probe-hardbreak` |
| `space` after non-list blocks | no node | renderer's existing next-token spacing rules cover it | tests 35, 51–61 and divider/heading cases |

Representative exact rows, also present in `divergences.json`:

#### `test-08-01` — loose list (no renderer difference)

Input: `"1. Lorem ipsum dolor sit amet.\n\n   Ut enim ad minim veniam.\n\n2. Duis aute irure dolor.\n\n   Excepteur sint occaecat cupidatat.\n\n3. Beep boop"`
Marked: `list → list_item → paragraph → text → space → paragraph → text → list_item → paragraph → text → space → paragraph → text → list_item → paragraph → text`
Markdig: `ListBlock → ListItemBlock → ParagraphBlock → LiteralInline → ParagraphBlock → LiteralInline → ListItemBlock → ParagraphBlock → LiteralInline → ParagraphBlock → LiteralInline → ListItemBlock → ParagraphBlock → LiteralInline`

`ListBlock.IsLoose == true` is the renderer-visible fact; the missing internal `space` nodes do not
change output because `renderList` already inserts the blank line between loose items.

#### `test-09-01` — task list (no renderer difference after property mapping)

Input: `"- [ ] beep\n- [x] boop"`
Marked: `list → list_item → checkbox → text → text → list_item → checkbox → text → text`
Markdig: `ListBlock → ListItemBlock → ParagraphBlock → TaskList → LiteralInline → ListItemBlock → ParagraphBlock → TaskList → LiteralInline`

Use `TaskList.Checked` and strip the one leading space from the following literal. The resulting
`[ ] beep` / `[x] boop` output is the same.

#### `test-70-01` — double tilde (no renderer difference after strict mapping)

Input: `"Use ~~strikethrough~~ here"`
Marked: `paragraph → text → del → text → text`
Markdig: `ParagraphBlock → LiteralInline → EmphasisInline → LiteralInline → LiteralInline`

The `EmphasisInline` properties identify `del`; its child is `strikethrough`.

#### `test-81-01` — partial closing fence (no renderer difference after trim mapping)

Input: `"```ts\nconst x = 1;\n``"`
Marked: `code`
Markdig: `FencedCodeBlock`

Markdig exposes `Info == "ts"`, `OpeningFencedCharCount == 3`, `ClosingFencedCharCount == 0`, and
the source line content ending in ````. The adapter can apply the same final partial-fence trim as
`trimPartialClosingFences`; the other six `test-81-*` rows are captured with their fence character,
opening/closing counts, and content in the ledger.

## Adapter mapping for all 19 inspected token types

The first column is the exact token vocabulary inspected by `markdown.ts`. “Virtual” means there is
no Markdig node and the adapter creates the renderer-facing token from source gaps or context.

| `markdown.ts` token | Markdig node | Property checks / adapter rule |
|---|---|---|
| `heading` | `HeadingBlock` | `Level` → `depth`; `IsSetext`; map inline children. |
| `paragraph` | `ParagraphBlock` | Map its inline children; retain block context (top-level, quote, list item, cell). |
| `latexBlock` | `MathBlock` for `$$...$$`; custom `BracketLatexBlock` for `\\[...\\]` | `FencedCodeBlock`-style `Lines`, delimiter/count, raw span, and pending state; map to display math. |
| `code` | `FencedCodeBlock` or `CodeBlock` | `Info` → `lang`; `Lines` → text; fence char/count; recover raw span and trim a streamed partial closing fence. |
| `list` | `ListBlock` | `IsOrdered`, `OrderedStart`, `OrderedDelimiter`, `BulletType`, `IsLoose`; recursively map `ListItemBlock`; synthesize source gaps. |
| `table` | `Markdig.Extensions.Tables.Table` | `ColumnDefinitions.Alignment`; `TableRow.IsHeader`; ordered `TableCell` children; map inline children. |
| `blockquote` | `QuoteBlock` | `QuoteChar`; recursively map child blocks; map soft `LineBreakInline` to `\\n`. |
| `hr` | `ThematicBreakBlock` | Type alone is sufficient. |
| `html` | `HtmlBlock` / `HtmlInline` | Preserve block/inline context; use `HtmlInline.Tag` or the precise source span for `raw`. |
| `space` | **Virtual** | No trivia node in the comparison pipeline. Inspect source-span gaps between sibling blocks; emit a virtual space where the renderer needs it, especially after a list. |
| `latex` | `MathInline` plus custom `ParenLatexInline` | Delimiter form, raw text, pending state, and inline math content; the custom parser must claim `\\(` before escape parsing. |
| `escape` | **No distinct node**; resolved `LiteralInline` | Default: use `Content`. Preserve mode: use precise source span or `EnableTrackTrivia` to recover `raw`. |
| `text` | `LiteralInline` | Use `Content`; concatenate adjacent literals and preserve soft newlines. |
| `strong` | `EmphasisInline` | `DelimiterCount == 2` and delimiter `*`/`_`; recursively map children. |
| `em` | `EmphasisInline` | `DelimiterCount == 1` and delimiter `*`/`_`; exclude strict `~` text. |
| `codespan` | `CodeInline` | `Content`, `Delimiter`, and `DelimiterCount`. |
| `link` | `LinkInline` or normalized `AutolinkInline` | `IsImage == false`; `Url`; child label; `IsAutoLink`, `UrlHasPointyBrackets`; for email use `mailto:` href. |
| `br` | `LineBreakInline` | `IsHard == true` → `br`; inspect `IsBackslash`; `IsHard == false` is a literal newline, not `br`. |
| `del` | `EmphasisInline` | `DelimiterChar == '~' && DelimiterCount == 2`; recursively map children. |

## Open questions closed

### What does the escape branch do?

It emits `token.text` by default and `token.raw` only when
`preserveBackslashEscapes` is true. Therefore resolved Markdig literals are equivalent for the
default path. The adapter must retain a source span/trivia path for the opt-in source-preserving
path. This is a small, explicit requirement, not a reason to reject Markdig.

### Which blockquote soft-break behavior is required?

Both upstream multiline cases (`test-60-01` lazy continuation and `test-61-01` explicit `>`)
require two bordered quote lines. Preserve Markdig's soft `LineBreakInline` as a newline, then let
the existing width wrapper operate. Joining it would fail the upstream styling/line-count checks.

## Go / no-go recommendation

**GO.** Markdig represents every required structural family, and the two behaviors that were
uncertain are expressible in its extension model. Proceed provided the next packet carries these
compatibility gates:

1. source-gap synthesis for list boundaries and source-marker recovery;
2. `LineBreakInline(IsHard == false)` → newline inside quote/inline text;
3. source-span recovery for `preserveBackslashEscapes` and pending delimiter forms;
4. `AutolinkInline` normalization plus a bare-email parser if hyperlink-capable terminals are in
   scope;
5. Markdig math extension nodes for dollar, parenthesis, and bracket forms, including pending raw
   delimiters.

There is no evidence that Markdig's parser or extension model is a blocker. The recommendation is
not a drop-in approval: omitting any of the five gates would reproduce one of the renderer-visible
findings above.

## Verification

- The spike project builds with `dotnet build tools/spikes/markdig/MarkdigDump.csproj -c Release`:
  0 warnings, 0 errors.
- `dotnet build PiCSharp.slnx -c Release` passed with 0 warnings and 0 errors.
- From the repository root, `dotnet test PiCSharp.slnx -c Release --no-build --no-restore --
  --filter-not-trait Category=E2E` passed all 1,505 tests (0 failed, 0 skipped).
- `marked-dump.mjs`, `MarkdigDump.cs`, and `compare.mjs` each completed the 109-case run; the
  extension assertions passed.

## What was not delivered or was intentionally overridden

- `reference/pi` was read only; no package was installed there.
- No file under `src/` was changed.
- `PiCSharp.slnx` and `Directory.Packages.props` were not changed; the standalone spike project
  remains outside the solution and uses its own `Markdig 0.44.0` reference.
- `components/markdown.ts` was not ported or modified. No production adapter, renderer, or LaTeX
  rendering code was delivered; those belong to the next packet (and `latex.ts` belongs to T5.7).
- The Marked LaTeX tokenizer was intentionally not copied into the dump harness. The comparison
  records the core Marked stream and treats the existing production tokenizer as the extension
  boundary proved on the Markdig side.
- The comparison pipeline uses precise source locations but not `EnableTrackTrivia`; the adapter
  may choose trivia tracking or source slicing. This is deliberate so the report measures what the
  default Markdig AST loses and documents the recovery choice.

## Re-running the spike

From the repository root, with `marked@18.0.5` installed in a scratch directory (not
`reference/pi/node_modules`):

```powershell
node tools/spikes/markdig/marked-dump.mjs `
  reference/pi/packages/tui/test/markdown.test.ts `
  tools/spikes/markdig/inputs.json `
  tools/spikes/markdig/marked.json `
  --marked-module C:\path\to\scratch\node_modules\marked\lib\marked.esm.js

dotnet run --project tools/spikes/markdig/MarkdigDump.csproj -- `
  tools/spikes/markdig/inputs.json `
  tools/spikes/markdig/markdig.json

node tools/spikes/markdig/compare.mjs
```

The first command re-harvests all 81 tests. `compare.mjs` writes the exhaustive
`tools/spikes/markdig/divergences.json` ledger and verifies that every input has both parser
outputs.
