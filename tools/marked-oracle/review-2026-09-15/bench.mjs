// Builds benchmark documents for both runtimes and times real marked (plain and pi) on them.
// The C# side reads the same files: markedcheck bench <fuzz dir>.
import fs from "node:fs";
import path from "node:path";
import { performance } from "node:perf_hooks";
import os from "node:os";
import { fileURLToPath, pathToFileURL } from "node:url";

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const args = process.argv.slice(2);
const option = (name) => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const markedRoot = path.resolve(option("--marked-module") ?? process.env.MARKED_MODULE ?? ".");
if (!fs.existsSync(path.join(markedRoot, "package.json"))) throw new Error("pass --marked-module <directory of an installed marked 18.0.5 package>");
const DIR = path.resolve(option("--out") ?? path.join(os.tmpdir(), "picsharp-marked-review"));
const OUT = path.join(DIR, "bench");
fs.mkdirSync(OUT, { recursive: true });

const { Marked } = await import(pathToFileURL(path.join(markedRoot, "lib/marked.esm.js")).href);
// pi-parser.mjs is written by record-fuzz.mjs into the same --out directory.
const { markdownParser: pi } = await import(pathToFileURL(path.join(DIR, "pi-parser.mjs")).href);
const plain = new Marked();

const sources = fs.readFileSync(path.join(REPO, "tests/fixtures/marked/corpus.jsonl"), "utf8")
  .split("\n").filter(Boolean).map((line) => JSON.parse(line).source);
function trimSurrogate(text) {
  const last = text.charCodeAt(text.length - 1);
  return last >= 0xd800 && last <= 0xdbff ? text.slice(0, -1) : text;
}
function concatenated(size) {
  let text = "";
  for (let i = 0; text.length < size; i++) text += sources[i % sources.length] + "\n\n";
  return trimSurrogate(text.slice(0, size));
}
function repeated(unit, size) {
  let text = "";
  for (let i = 0; text.length < size; i++) text += unit.replace("#N", String(i));
  return text.slice(0, size);
}

const docs = [
  ["doc-100kb", concatenated(100_000)],
  ["doc-1mb", concatenated(1_000_000)],
  ["para-200kb", repeated("word **bold** *em* `code` [link](https://example.com) and plain text ", 200_000)],
  ["list-100kb", repeated("- item #N with **bold** and `code`\n", 100_000)],
  ["list-300kb", repeated("- item #N with **bold** and `code`\n", 300_000)],
];
for (const [name, text] of docs) fs.writeFileSync(path.join(OUT, `${name}.md`), text);

const lines = [];
for (const [configuration, parser] of [["plain", plain], ["pi", pi]]) {
  for (const [name, text] of docs) {
    const warmStart = performance.now();
    parser.lexer(text);
    const warm = performance.now() - warmStart;
    const count = warm > 20_000 ? 1 : 5;
    const runs = [];
    for (let i = 0; i < count; i++) {
      const start = performance.now();
      parser.lexer(text);
      runs.push(performance.now() - start);
    }
    runs.sort((a, b) => a - b);
    const line = `${configuration.padEnd(5)} ${name.padEnd(14)} chars=${String(text.length).padStart(8)} warm=${warm.toFixed(1).padStart(9)} ms median=${runs[Math.floor(runs.length / 2)].toFixed(1).padStart(9)} ms runs=${count}`;
    console.log(line);
    lines.push(line);
  }
}
fs.writeFileSync(path.join(OUT, "node-results.txt"), lines.join("\n") + "\n");
