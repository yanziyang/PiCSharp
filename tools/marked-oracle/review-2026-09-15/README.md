# T5.9 review harness: the marked port against real marked, on input it never saw

Written for the review of `T5.9` on 2026-09-15. It is not part of the build, the test suite or CI.

The committed oracle in `tests/fixtures/marked/` proves the port on its own 358-case corpus, and that
proof holds: re-recording the fixtures from the pinned marked reproduces them byte for byte. This
harness tests the stronger claim that justified porting marked at all, from `docs/dependencies.md §4`:
that the port can be checked against the real marked **on any input**. It generates a fresh corpus,
lexes it with both, and compares every token.

## Contents

| File | What it does |
|---|---|
| `targeted.mjs` | Inputs aimed at each JavaScript-versus-.NET difference in `docs/translation-patterns.md §15`, plus ordinary Markdown constructs |
| `record-fuzz.mjs` | Builds the corpus and records real marked 18.0.5 tokens in the `plain` and `pi` configurations. The corpus is the targeted inputs, every prefix of two streaming documents, and 3,000 character-level plus 3,000 line-structured random documents from seed `20260915`. pi's parser is extracted from `reference/pi` exactly as `tools/marked-oracle/record.mjs` does |
| `markedcheck/` | C# harness over the built `src/Pi.Tui/bin/Release/net10.0/Pi.Tui.dll`. Its assembly is named `Pi.Tui.Tests` so that the port's `InternalsVisibleTo` exposes `MarkdownParser`. Modes: `fuzz`, `bench`, `concurrency`, `coverage`, `overhead`, `regex`, `nesting`, `trace` |
| `compare.mjs` | Structural comparison: exact key sets, value types and array order, as `MarkedOracleTests` compares |
| `record-regexes.mjs`, `compare-regexes.mjs` | The regex differential: runs every JavaScript pattern behind `src/Pi.Tui/Marked/MarkedRegexes.g.cs` over probe inputs and records match and group offsets, then compares them with the harness's `regex` mode |
| `markedcheck/TokenJson.cs` | The harness's own copy of the token writer, which the port keeps in its tests |
| `analyse.mjs` | Groups the differences by cause: exceptions, ASCII-only inputs, each special character, pi-only |
| `bench.mjs` | Builds benchmark documents and times real marked; `markedcheck bench` times the port on the same files. The scaling documents were added after the review |
| `nesting-limits.mjs` | Bisects how deep real marked nests blockquotes, lists and emphasis before V8's stack runs out; `markedcheck nesting` lexes the same shapes with the port |

## Running

From the repository root, with marked installed outside the repository:

```bash
npm install marked@18.0.5 --prefix <scratch>
dotnet build PiCSharp.slnx -c Release
node tools/marked-oracle/review-2026-09-15/record-fuzz.mjs --marked-module <scratch>/node_modules/marked --out <dir>
dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- fuzz <dir>
node tools/marked-oracle/review-2026-09-15/compare.mjs <dir>
node tools/marked-oracle/review-2026-09-15/analyse.mjs <dir>
```

The regex differential, after `record-fuzz.mjs` has written `<dir>/inputs.jsonl`:

```bash
node tools/marked-oracle/generate-regexes.mjs --check --manifest <dir>/manifest.json
node tools/marked-oracle/review-2026-09-15/record-regexes.mjs <dir> <dir>/inputs.jsonl
dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- regex <dir>
node tools/marked-oracle/review-2026-09-15/compare-regexes.mjs <dir>
```

Concurrency. This lexes the cases on eight explicit threads and compares each result with a single-threaded
baseline. The optional arguments take every n-th case and set the number of passes; the default is every case,
twice:

```bash
dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- concurrency <dir> [stride] [passes]
```

At `ed89414` every lex allocated about 0.9 MB, and all cases with two passes did not finish within ten minutes
on a four-core machine. The review used `4 1` with `DOTNET_gcServer=1`. After remediation, all cases with two
passes take under four minutes.

Benchmarks, after `record-fuzz.mjs` has written `pi-parser.mjs` into `<dir>`:

```bash
node tools/marked-oracle/review-2026-09-15/bench.mjs --marked-module <scratch>/node_modules/marked --out <dir>
dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- bench <dir>
```

Nesting, after `record-fuzz.mjs` has written `pi-parser.mjs` into `<dir>`:

```bash
node tools/marked-oracle/review-2026-09-15/nesting-limits.mjs --marked-module <scratch>/node_modules/marked --out <dir>
dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- nesting
```

`markedcheck trace '<markdown with \n escapes>'` prints the tokens or the full exception for one input.

## Results at `ed89414`

| | `plain` | `pi` |
|---|---:|---:|
| Cases | 6,881 | 6,881 |
| Differ from marked | 346 | 341 |
| Port throws where marked does not | 47 | 43 |
| Differences on ASCII-only input (excluding exceptions) | 5 | 4 |
| Differences on streaming prefixes | 0 | 0 |

Every exception is the same `ArgumentOutOfRangeException` from `SourceView.Slice`. The shortest input is
`> - a` followed by a lazy `b` line, which is plain ASCII.

The findings, with the shortest input for each cause, are recorded in the status of
`ÍmplementationKit/packets/T5.9-marked-lexer.md`.

## Performance at `ed89414`

Median of five runs after one warm-up, on 2026-09-15:
- marked on Node v24.18.0;
- the port on .NET 10.0.11, Release.

The corpus documents concatenate the 358 cases `tests/fixtures/marked/corpus.jsonl` held at the time. The list
documents repeat one list item.

| Parser | Document | marked | Port | Port allocation per run |
|---|---|---:|---:|---:|
| `plain` | Corpus, 100 KB | 125 ms | 314 ms | 87 MB |
| `plain` | Corpus, 1 MB | 920 ms | 5,121 ms | 4.5 GB |
| `plain` | One 200 KB paragraph | 4,390 ms | 194 ms | 54 MB |
| `plain` | List, 100 KB | 133 ms | 987 ms | 1.3 GB |
| `plain` | List, 300 KB | 502 ms | 8,123 ms | 11.8 GB |
| `pi` | Corpus, 100 KB | 163 ms | 453 ms | 383 MB |
| `pi` | Corpus, 1 MB | 1,972 ms | 26,679 ms | 35.9 GB |
| `pi` | One 200 KB paragraph | 2,335 ms | 5,504 ms | 6.7 GB |
| `pi` | List, 100 KB | 66 ms | 1,008 ms | 1.3 GB |
| `pi` | List, 300 KB | 156 ms | 7,652 ms | 11.8 GB |

Before any lexing, `Lexer("a")` alone costs 2.7 ms (`pi`) to 6.7 ms (`plain`) and about 0.9 MB,
because every `Lexer` rebuilds its rule set of about 80 regular expressions.

## Results after remediation

The findings above were fixed on 2026-09-16 and 2026-09-17; `tests/Pi.Tui.Tests/Marked/T5.9-findings.md` records
the remediated port. With the same 6,881 cases:

| | `plain` | `pi` |
|---|---:|---:|
| Cases | 6,881 | 6,881 |
| Differ from marked | 0 | 0 |
| Port throws where marked does not | 0 | 0 |

- **Regex differential:** all 119 generated patterns over 5,391 inputs, with 100,648 pattern-input pairs that
  match. Match and group offsets agree, except `BlockBlockquote` on 2 inputs, where JavaScript clears a capture
  inside a repeated group that the port never reads.
- **Concurrency:** eight explicit threads over all 6,881 cases, twice, on one shared parser. 0 mismatches in
  `plain` and in `pi`.
- **`Lexer("a")`:** 0.066 ms and 34 KB (`plain`), 0.096 ms and 66 KB (`pi`).

## Performance after remediation

Back to back on 2026-09-16, on the same documents:
- marked on Node v24.18.0, then the port on .NET 10.0.11, Release;
- median of five runs after one warm-up.

marked's own times moved between days. The 1 MB corpus document took marked 920 ms on 2026-09-15 and 230 ms here,
so compare figures within one table only.

| Parser | Document | marked | Port | Port allocation per run |
|---|---|---:|---:|---:|
| `plain` | Corpus, 100 KB | 24.6 ms | 92.6 ms | 9.6 MB |
| `plain` | Corpus, 1 MB | 229.5 ms | 452.1 ms | 97.6 MB |
| `plain` | One 200 KB paragraph | 854.0 ms | 75.4 ms | 18.9 MB |
| `plain` | List, 100 KB | 26.7 ms | 50.9 ms | 17.3 MB |
| `plain` | List, 300 KB | 88.0 ms | 182.7 ms | 50.2 MB |
| `pi` | Corpus, 100 KB | 30.0 ms | 73.8 ms | 9.2 MB |
| `pi` | Corpus, 1 MB | 286.5 ms | 679.9 ms | 93.5 MB |
| `pi` | One 200 KB paragraph | 1,140.9 ms | 323.9 ms | 18.9 MB |
| `pi` | List, 100 KB | 32.9 ms | 40.6 ms | 17.3 MB |
| `pi` | List, 300 KB | 96.4 ms | 111.1 ms | 50.2 MB |

The scaling documents each repeat one construct to 25 KB and to 100 KB, so linear work takes four times as long.
The remediation added them to find string growth and rescans the first benchmark set missed.

| Parser | Document | marked, 25 KB and 100 KB | Port, 25 KB and 100 KB |
|---|---|---:|---:|
| `plain` | Words with `_` | 3.1 and 11.7 ms | 4.0 and 14.5 ms |
| `plain` | One long list item | 36.5 and 522.7 ms | 164.0 and 2,552.3 ms |
| `plain` | Blockquote lines | 2.7 and 9.4 ms | 3.3 and 20.9 ms |
| `plain` | Table rows | 7.7 and 21.4 ms | 4.4 and 47.2 ms |
| `plain` | Backslash escapes | 23.8 and 278.8 ms | 8.6 and 30.1 ms |
| `pi` | Words with `_` | 17.0 and 156.5 ms | 9.4 and 113.5 ms |
| `pi` | One long list item | 46.1 and 875.6 ms | 164.7 and 2,733.3 ms |
| `pi` | Blockquote lines | 8.5 and 58.5 ms | 6.1 and 47.4 ms |
| `pi` | Table rows | 5.6 and 29.1 ms | 6.5 and 30.0 ms |
| `pi` | Backslash escapes | 593.5 and 1,733.5 ms | 16.2 and 201.5 ms |

The long list item grows about 15 times in both, and the growth is marked's own. Inside a list item marked lexes
one `text` token per line and tries `lheading` before each; that pattern scans lazily to the end of the item for a
setext underline. The port runs the same scans 3 to 5 times slower: at 100 KB, `Lheading` takes 2,494 ms of the
port's 2,536 ms (`plain`).

Medians of five runs still vary between runs on a desktop. In two runs of the same build, the `plain` blockquote
document at 100 KB measured 123.5 ms and 20.9 ms, and the table document 99.7 ms and 47.2 ms. Timed alone on
growing blockquote text, each block regex grew linearly, with occasional stalls of up to 440 ms and no garbage
collection.

## Nesting after remediation

marked recurses once per nesting level until V8 throws. `nesting-limits.mjs` found its deepest levels on
Node v24.18.0, which move with JIT state:

| Shape | `plain` | `pi` |
|---|---:|---:|
| Blockquotes, `> ` repeated | 2,000 | 2,000 |
| Lists, `- ` repeated | 2,875 | 2,000 |
| Ordered lists, `1. ` repeated | 2,000 | 2,360 |
| Emphasis, `*` and `_` alternating | 3,501 | 3,128 |
| Strong and emphasis alternating | 3,293 | 3,128 |

At `ed89414` the port threw `InsufficientExecutionStackException` after at most 407 levels on a 1 MB stack. It now
moves to a new thread with a larger stack when its stack is nearly full, and stops at a fixed limit of 5,000 levels.
`markedcheck nesting` on a 1 MB stack, `plain`; a 256 KB stack gives the same outcomes:

| Shape | 3,500 levels | 5,000 levels | 5,001 levels | 10,000 levels |
|---|---:|---:|---:|---:|
| Blockquotes | tokens, 322 ms | tokens, 518 ms | exception, 421 ms | exception, 1,212 ms |
| Lists | tokens, 1,100 ms | tokens, 2,575 ms | exception, 2,853 ms | exception, 8,453 ms |
| Emphasis | tokens, 4,634 ms | tokens, 8,715 ms | exception, 9,147 ms | not run |
