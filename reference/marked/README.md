# marked 18.0.5 — vendored reference

Read-only specification for `src/Pi.Tui/Marked/`. Do not edit anything here.

## Why this exists

`pi-tui` renders Markdown from the token stream of the `marked` package
(`packages/tui/src/components/markdown.ts`), and re-exports `Marked`, `Token` and `Tokens` for
`coding-agent`. PiCSharp ports the marked **lexer** rather than adopting a .NET Markdown parser:
measured against marked, Markdig diverged in renderer-visible ways that no upstream test catches.
The decision and its evidence are in `docs/spikes/markdig-divergence.md`.

## Provenance

- Package: `marked@18.0.5` from the npm registry.
- Integrity: identical to the `node_modules/marked` entry in `reference/pi/package-lock.json`, so this
  is exactly the marked that pi runs. The full value is in `PINNED`.
- `src/*.ts` — the TypeScript sources, extracted from `sourcesContent` in the published
  `lib/marked.esm.js.map`. The npm tarball ships only bundled JavaScript; its source map carries the
  exact sources the bundle was built from.
- `lib/marked.d.ts` — the published type declarations. The token shapes in `Tokens.*` are the contract
  `src/Pi.Tui/Marked/` mirrors.
- `LICENSE` — marked's licence file, verbatim. marked is MIT-licensed; ported code must carry the notice.
- `composed-rules.gfm.json` — the **final** regular expressions for the option path pi uses
  (`gfm: true`, `pedantic: false`, `breaks: false`), read from `Lexer.rules.block.gfm` and
  `Lexer.rules.inline.gfm` at runtime. marked composes its rules with an `edit()` helper; this file is
  what that composition produces, so a port translates final patterns instead of re-deriving them.

## What is in scope for the port

pi uses `new Marked()`, `setOptions({ tokenizer })` with a subclassed `Tokenizer`, `use({ extensions })`
with block and inline tokenizer extensions, and `lexer(src)`. That is `src/Lexer.ts`,
`src/Tokenizer.ts`, `src/rules.ts` (normal and GFM paths), `src/helpers.ts`, `src/defaults.ts`, and the
options and tokenizer-extension paths of `src/Instance.ts`.

Not in scope: `Renderer.ts`, `TextRenderer.ts`, `Parser.ts`, `Hooks.ts` and `marked.ts`, which produce
HTML that pi never uses, and the pedantic and breaks rule variants.

## Reproducing

    npm install marked@18.0.5 --prefix <scratch-directory>

Check the installed integrity in `<scratch-directory>/node_modules/.package-lock.json` against `PINNED`.
Then read `<scratch-directory>/node_modules/marked/lib/marked.esm.js.map` and, for each entry in
`sources`, write the matching `sourcesContent` entry to `src/<basename>`.
