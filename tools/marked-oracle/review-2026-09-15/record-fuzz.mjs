// Independent differential check for the T5.9 marked port. Records real marked 18.0.5 tokens, in the
// plain and pi configurations, for a fresh seeded corpus the delivery never saw.
import fs from "node:fs";
import path from "node:path";
import { stripTypeScriptTypes } from "node:module";
import os from "node:os";
import { fileURLToPath, pathToFileURL } from "node:url";
import * as T from "./targeted.mjs";

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const args = process.argv.slice(2);
const option = (name) => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const markedRoot = path.resolve(option("--marked-module") ?? process.env.MARKED_MODULE ?? ".");
if (!fs.existsSync(path.join(markedRoot, "package.json"))) throw new Error("pass --marked-module <directory of an installed marked 18.0.5 package>");
const OUT = path.resolve(option("--out") ?? path.join(os.tmpdir(), "picsharp-marked-review"));
fs.mkdirSync(OUT, { recursive: true });

if (JSON.parse(fs.readFileSync(path.join(markedRoot, "package.json"), "utf8")).version !== "18.0.5") throw new Error("wrong marked");
const markedUrl = pathToFileURL(path.join(markedRoot, "lib/marked.esm.js")).href;
const { Marked } = await import(markedUrl);

// pi's parser, extracted from reference/pi exactly as tools/marked-oracle/record.mjs does.
const markdownTs = fs.readFileSync(path.join(REPO, "reference/pi/packages/tui/src/components/markdown.ts"), "utf8");
const startMarker = "const STRICT_STRIKETHROUGH_REGEX";
const endMarker = "markdownParser.use({ extensions: [...LATEX_MARKDOWN_EXTENSIONS] });";
const begin = markdownTs.indexOf(startMarker);
const end = markdownTs.indexOf(endMarker);
if (begin < 0 || end < 0) throw new Error("markdown.ts markers missing");
const piModule = path.join(OUT, "pi-parser.mjs");
fs.writeFileSync(piModule, "import { Marked, Tokenizer } from " + JSON.stringify(markedUrl) + ";\n"
  + stripTypeScriptTypes(markdownTs.slice(begin, end + endMarker.length), { mode: "strip" }) + "\nexport { markdownParser };\n");
const { markdownParser: pi } = await import(pathToFileURL(piModule).href);
const plain = new Marked();

function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const random = rng(20260915);
const pick = (list) => list[Math.floor(random() * list.length)];
const int = (lo, hi) => lo + Math.floor(random() * (hi - lo + 1));

const charAtoms = [
  "a", "b", "c", "x", "y", "1", "2", " ", " ", " ", "  ", "\n", "\n", "\n\n", "\t",
  "*", "*", "**", "_", "_", "__", "~", "~~", "`", "``", "```", "#", "# ", ">", "> ", "-", "- ", "+", "1. ", "2) ", "=", "===", "---",
  "|", "| ", ":", ":-", "[", "]", "(", ")", "![", "](", "]:", "<", ">", "\\", "\\", "$", "$", "$$", "\\(", "\\)", "\\[", "\\]",
  "&", "&amp;", "&#35;", "@", ".", "/", "\"", "'", "!", "?", ",", ";", "{", "}", "^", "%",
  "http://", "https://", "www.", "a@b.co", "mailto:", "<div>", "</div>", "<span>", "</span>", "<!--", "-->", "<a href=\"x\">", "</a>",
  "<pre>", "</pre>", "[x]: /u", "[x]", "[ ] ", "[x] ",
  T.LS, T.PS, T.NEL, T.NBSP, T.BOM, T.EMOJI, T.MATHA, T.CJK, T.FW3, T.AR3, T.KELVIN, T.IDOT, "\u03A3", T.E_ACUTE, "\r", "\r\n",
];
const inlineAtoms = charAtoms.filter((atom) => !/[\n\r]/.test(atom));
const linePrefixes = ["", "", "", "", "# ", "## ", "###### ", "> ", "> > ", ">", "- ", "* ", "+ ", "1. ", "2) ", "10. ", "    ", "\t",
  "  - ", "   ", "| ", "|", "```", "```js", "~~~", "$$", "\\[", "[a]: ", "[b]: <", "- [ ] ", "- [x] ", "---", "***", "___", "===",
  "<div>", "</div>", "<!--", "<pre>", "1. [x] "];
const lineSuffixes = ["", "", "", "", " |", "  ", "\\", "```", "$$", "\\]", " #", "-->", "</pre>", ":", " \"t\"", ")"];
const joiners = ["\n", "\n", "\n", "\n\n", "\r\n", "\n  ", "\n    ", "\n> "];
function makeLine() {
  let s = pick(linePrefixes);
  for (let i = int(0, 10); i > 0; i--) s += pick(inlineAtoms);
  return s + pick(lineSuffixes);
}

const cases = [];
const seen = new Set();
const add = (id, source) => { if (!seen.has(source)) { seen.add(source); cases.push({ id, source }); } };
const pad = (n, w) => String(n).padStart(w, "0");

for (const [group, sources] of Object.entries(T.targeted)) sources.forEach((source, i) => add(`t-${group}-${pad(i, 3)}`, source));
T.streamingDocs.forEach((doc, d) => { for (let n = 1; n <= doc.length; n++) add(`stream-${d + 1}-${pad(n, 4)}`, doc.slice(0, n)); });
for (let i = 0; i < 3000; i++) {
  let s = "";
  for (let j = int(1, 40); j > 0; j--) s += pick(charAtoms);
  add(`fuzz-char-${pad(i, 5)}`, s);
}
for (let i = 0; i < 3000; i++) {
  let s = makeLine();
  for (let j = int(0, 7); j > 0; j--) s += pick(joiners) + makeLine();
  add(`fuzz-line-${pad(i, 5)}`, s);
}

function lex(parser, source) {
  try {
    const list = parser.lexer(source);
    return { tokens: JSON.parse(JSON.stringify(list)), links: JSON.parse(JSON.stringify(list.links || {})) };
  } catch (error) {
    return { error: String(error?.message ?? error) };
  }
}

fs.writeFileSync(path.join(OUT, "inputs.jsonl"), cases.map((c) => JSON.stringify(c)).join("\n") + "\n");
for (const [name, parser] of [["plain", plain], ["pi", pi]]) {
  let errors = 0;
  const lines = cases.map((c) => {
    const result = lex(parser, c.source);
    if (result.error) errors++;
    return JSON.stringify({ id: c.id, result });
  });
  fs.writeFileSync(path.join(OUT, `${name}.jsonl`), lines.join("\n") + "\n");
  console.log(`${name}: ${cases.length} cases, ${errors} errors`);
}
const groups = {};
for (const c of cases) { const g = c.id.replace(/-\d+$/, ""); groups[g] = (groups[g] || 0) + 1; }
console.log(JSON.stringify(groups));
