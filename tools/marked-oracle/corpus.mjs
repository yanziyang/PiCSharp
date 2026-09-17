import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import * as T from "./review-2026-09-15/targeted.mjs";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const OUT = path.join(ROOT, "tests/fixtures/marked/corpus.jsonl");
const spikePath = path.join(ROOT, "tools/spikes/markdig/inputs.json");
const edgePath = path.join(ROOT, "tools/spikes/markdig/probes-2026-09-13/marked/edge-inputs.json");
const extrasPath = path.join(ROOT, "tools/spikes/markdig/probes-2026-09-13/marked/emphasis-extras-inputs.json");

const cases = [];
const seen = new Set();

function add(id, source, kind = "generated") {
  if (typeof source !== "string" || seen.has(id)) return;
  seen.add(id);
  cases.push({ id, source, kind });
}

const spike = JSON.parse(fs.readFileSync(spikePath, "utf8"));
for (const entry of spike.cases) add(entry.id, entry.source, entry.kind ?? "spike");

const seeds = [
  ["blocks-basic", "\n\n    indented\n    code\n\n~~~csharp\nclass C {}\n~~~\n\n# heading\n\n---\n\n> quote\n> continuation\n\n- one\n- two"],
  ["blocks-html", "<div>\ncontent\n</div>\n\n<!-- comment -->\n\n<script>\nalert(1)\n</script>"],
  ["blocks-definitions", "[one]: https://example.com \"title\"\n\nUse [one][one] and [one][].\n\n[duplicate]: /first\n[duplicate]: /second"],
  ["blocks-tables", "| a | b | c |\n| :--- | :---: | ---: |\n| `a|b` | escaped \\| pipe | tail |\n| short | long value |"],
  ["lists-tasks", "1) first\n2) second\n\n- [x] done\n- [ ] todo\n\n  continuation"],
  ["inline-delimiters", "*em* **strong** _under_ __strong__ ~~strike~~ ~plain~ ***both*** a_b_c a~~b~~a"],
  ["inline-links", "[text](<https://example.com/a b> \"title\") ![alt](/image.png) [ref][one] [one][] [one]"],
  ["inline-autolinks", "<https://example.com> <user@example.com> www.example.com https://example.com user@example.com ftp://host/x"],
  ["inline-html", "<span class='x'>visible</span> <br/> <!-- x --> <a href='x'>link</a>"],
  ["inline-escapes", "\\*not em\\* \\_not_ \\#hash \\| pipe \\~tilde"],
  ["inline-code", "`a  b` and `` `code` `` and ```not a fence```"],
  ["inline-breaks", "hard  \nbreak\nnext"],
  ["special-chars", "CJK *文字* 😀 _emoji_" + T.LS + "line" + T.PS + "next" + T.NEL + " NBSP" + T.NBSP + " FEFF" + T.BOM],
  ["latex-inline", "math $x^2 + y^2$ and \\(a \\to b\\) and \\[c\\]"],
  ["latex-block", "before\n\n$$\nx^2\n$$\n\nafter\n\n\\[\ny^2\n\\]"],
  ["latex-pending", "streaming $\\mathbb{C}^3 and \\[x^2"],
  ["unclosed-fences", "```typescript\nline\n``\n\n~~~~\npartial"],
  ["nested-structure", "> - one\n>   1. nested\n>      ```\n>      code\n>      ```\n> - two"],
  ["probe-extensions", "before\n\n@@B:[[inside]]@@\n\ntext [[inline]] and # heading"],
];
for (const [id, source] of seeds) add(`seed-${id}`, source);

const delimiterChars = ["*", "_", "~"];
let generated = 0;
for (const delimiter of delimiterChars) {
  for (let length = 1; length <= 4; length++) {
    const d = delimiter.repeat(length);
    add(`generated-delimiter-${delimiter}-${length}`, `${d}alpha${d} ${d} punctuation! ${d} CJK文字 ${d} 😀 ${d}`);
    generated++;
  }
}

const escapedPunctuation = `!"#$%&'()*+,-./:;<=>?@[\\\\]^_\`{|}~`;
add("generated-all-escapes", [...escapedPunctuation].map(ch => `\\${ch}`).join(" "));

const generatedDocuments = [
  "- a\n  - b\n    - c\n      - d",
  "1. a\n   1. b\n      1. c\n         1. d",
  "> > > nested quote\n>\n> paragraph",
  "| h1 | h2 |\n| --- | --- |\n| a \\| b | `c|d` |\n| e | f |",
  "[a]: /a\n\nparagraph\n[a] and [missing] and [a][]",
  "<http://example.com> <mailto:user@example.com> <tel:+1> user@example.com www.example.com",
];
for (let i = 0; i < generatedDocuments.length; i++) add(`generated-document-${String(i + 1).padStart(3, "0")}`, generatedDocuments[i]);

const streaming = [
  "# Heading\n\n- item with **bold** and `code`\n- second item\n\nparagraph with https://example.com",
  "Before\n\n```typescript\nconst value = 42;\n```\n\nafter",
  "| Name | Value |\n| --- | --- |\n| alpha | beta |\n| gamma | delta |",
];
for (let doc = 0; doc < streaming.length; doc++) {
  const source = streaming[doc];
  const step = source.length <= 120 ? 1 : 3;
  for (let end = 1; end <= source.length; end += step) add(`stream-${doc + 1}-${String(end).padStart(4, "0")}`, source.slice(0, end), "stream");
  add(`stream-${doc + 1}-final`, source, "stream");
}

const repeated = [
  "# generated heading\n\nThis paragraph has **bold**, *emphasis*, `code`, a [link](https://example.com), and a table below.\n\n",
  "| one | two | three |\n| --- | --- | --- |\n| alpha | beta | gamma |\n\n",
  "> quoted **text**\n\n",
];
let large = "";
while (large.length < 50_000) large += repeated[large.length % repeated.length];
add("large-50kb", large.slice(0, 50_000), "large");

// ---------------------------------------------------------------------------------------------------------
// Added 2026-09-15 by the T5.9 review. Appended after the original cases, which keep their order and content.
// The two probe files hold { metadata, cases }; the original loop read them as arrays and added nothing.
for (const file of [edgePath, extrasPath]) {
  const document = JSON.parse(fs.readFileSync(file, "utf8"));
  for (const entry of document.cases) add(`probe-${path.basename(file, ".json")}-${entry.id}`, entry.source, "probe");
}

// Inputs of tools/spikes/markdig/probes-2026-09-13/strictprobe and tableprobe, which the original never read.
const strictAndTableProbes = [
  ["st-lazy-pairing", "~~a ~~b~~"],
  ["st-two-pairs", "~~a~~ ~~b~~"],
  ["st-three-openers", "~~a ~~b ~~c~~"],
  ["st-tilde-after", "~~a~~~"],
  ["st-nested-strong-tilde-after", "~~**a**~~~"],
  ["st-nested-strong", "~~**a**~~"],
  ["tb-escaped-pipe", "| a |\n|---|\n| x \\| y |"],
  ["tb-align", "| l | c | r |\n|:--|:-:|--:|\n| 1 | 2 | 3 |"],
  ["tb-overflow", "| a |\n|---|\n| x | y |"],
  ["tb-underflow", "| a | b |\n|---|---|\n| x |"],
  ["tb-header-only", "| a | b |\n|---|---|"],
  ["tb-code-pipe", "| a |\n|---|\n| `x|y` |"],
];
for (const [id, source] of strictAndTableProbes) add(`probe-${id}`, source, "probe");

// Inputs aimed at each JavaScript-versus-.NET difference in docs/translation-patterns.md section 15.
for (const [group, sources] of Object.entries(T.targeted)) {
  sources.forEach((source, i) => add(`review-${group}-${String(i).padStart(3, "0")}`, source, "review"));
}

// A seeded generated set: character-level and line-structured Markdown, including special spaces, emoji,
// astral letters, Unicode digits and case-mapping characters beside delimiters, tabs and \r\n.
function seeded(seed) {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const random = seeded(591);
const pick = (list) => list[Math.floor(random() * list.length)];
const between = (lo, hi) => lo + Math.floor(random() * (hi - lo + 1));
const atoms = [
  "a", "b", "c", "x", "1", " ", " ", "  ", "\n", "\n", "\n\n", "\t",
  "*", "*", "**", "_", "_", "__", "~", "~~", "`", "``", "```", "#", "# ", ">", "> ", "-", "- ", "+", "1. ", "2) ", "=", "---",
  "|", "| ", ":", ":-", "[", "]", "(", ")", "![", "](", "]:", "<", ">", "\\", "$", "$$", "\\(", "\\)", "\\[", "\\]",
  "&", "&amp;", "@", ".", "/", "\"", "'", "!", "?", ",", "^", "http://", "https://", "www.", "a@b.co", "<div>", "</div>",
  "<span>", "<!--", "-->", "<a href=\"x\">", "</a>", "[x]: /u", "[x]", "[ ] ", "[x] ",
  T.LS, T.PS, T.NEL, T.NBSP, T.BOM, T.EMOJI, T.MATHA, T.CJK, T.FW3, T.AR3, T.KELVIN, T.IDOT, T.E_ACUTE, "\r", "\r\n",
];
const inlineAtoms = atoms.filter((atom) => !atom.includes("\n") && !atom.includes("\r"));
const prefixes = ["", "", "", "# ", "## ", "> ", "> > ", "- ", "* ", "1. ", "2) ", "    ", "\t", "  - ", "| ", "```", "~~~", "$$", "[a]: ", "- [ ] ", "---", "<div>"];
const suffixes = ["", "", "", " |", "  ", "\\", "```", "$$", " #"];
const line = () => {
  let text = pick(prefixes);
  for (let n = between(0, 8); n > 0; n--) text += pick(inlineAtoms);
  return text + pick(suffixes);
};
for (let i = 0; i < 500; i++) {
  let text = "";
  for (let n = between(1, 30); n > 0; n--) text += pick(atoms);
  add(`fuzz-char-${String(i).padStart(3, "0")}`, text, "fuzz");
}
for (let i = 0; i < 500; i++) {
  let text = line();
  for (let n = between(0, 6); n > 0; n--) text += pick(["\n", "\n", "\n\n", "\r\n", "\n  ", "\n> "]) + line();
  add(`fuzz-line-${String(i).padStart(3, "0")}`, text, "fuzz");
}

const lines = cases.map(entry => JSON.stringify(entry)).join("\n") + "\n";
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, lines);
const hash = crypto.createHash("sha256").update(lines).digest("hex");
console.log(JSON.stringify({ cases: cases.length, bytes: Buffer.byteLength(lines), sha256: hash, generated }, null, 2));
