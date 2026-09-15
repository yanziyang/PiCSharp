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
| `markedcheck/` | C# harness over the built `src/Pi.Tui/bin/Release/net10.0/Pi.Tui.dll`. Its assembly is named `Pi.Tui.Tests` so that the port's `InternalsVisibleTo` exposes `MarkdownParser`. Modes: `fuzz`, `bench`, `concurrency`, `coverage`, `overhead`, `trace` |
| `compare.mjs` | Structural comparison: exact key sets, value types and array order, as `MarkedOracleTests` compares |
| `analyse.mjs` | Groups the differences by cause: exceptions, ASCII-only inputs, each special character, pi-only |
| `bench.mjs` | Builds benchmark documents and times real marked; `markedcheck bench` times the port on the same files |

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

Concurrency. This lexes every fourth case on eight explicit threads and compares each result with a
single-threaded baseline. A run over all cases with two passes did not finish within ten minutes on a
four-core machine, because every lex allocates about 0.9 MB:

```bash
DOTNET_gcServer=1 dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- concurrency <dir> 4 1
```

Benchmarks, after `record-fuzz.mjs` has written `pi-parser.mjs` into `<dir>`:

```bash
node tools/marked-oracle/review-2026-09-15/bench.mjs --marked-module <scratch>/node_modules/marked --out <dir>
dotnet run --project tools/marked-oracle/review-2026-09-15/markedcheck -c Release -- bench <dir>
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

The corpus documents concatenate `tests/fixtures/marked/corpus.jsonl`. The list documents repeat one list
item.

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
