// Bisects how deep real marked (plain and pi) nests blockquotes, lists and emphasis before V8's stack runs out.
// V8's limit moves with JIT state, so read the result as a range across runs rather than a constant.
// `markedcheck nesting` lexes the same shapes with the port.
// Usage: node nesting-limits.mjs --marked-module <installed marked 18.0.5 package> --out <dir with pi-parser.mjs>
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { pathToFileURL } from "node:url";

const args = process.argv.slice(2);
const option = (name) => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const markedRoot = path.resolve(option("--marked-module") ?? process.env.MARKED_MODULE ?? ".");
if (!fs.existsSync(path.join(markedRoot, "package.json"))) throw new Error("pass --marked-module <directory of an installed marked 18.0.5 package>");
const DIR = path.resolve(option("--out") ?? path.join(os.tmpdir(), "picsharp-marked-review"));

const { Marked } = await import(pathToFileURL(path.join(markedRoot, "lib/marked.esm.js")).href);
// pi-parser.mjs is written by record-fuzz.mjs into the same --out directory.
const { markdownParser: pi } = await import(pathToFileURL(path.join(DIR, "pi-parser.mjs")).href);

// Emphasis alternates two delimiters with spaces between, which marked nests one level per delimiter.
const alternating = (depth, first, second) => {
  const delimiters = Array.from({ length: depth }, (_, level) => (level % 2 ? second : first));
  return delimiters.map((delimiter) => delimiter + "a ").join("") + "leaf" + delimiters.reverse().map((delimiter) => " b" + delimiter).join("");
};
const shapes = [
  ["quote", (depth) => "> ".repeat(depth) + "leaf"],
  ["list", (depth) => "- ".repeat(depth) + "leaf"],
  ["ordered", (depth) => "1. ".repeat(depth) + "leaf"],
  ["em", (depth) => alternating(depth, "*", "_")],
  ["strongEm", (depth) => alternating(depth, "**", "*")],
  ["delEm", (depth) => alternating(depth, "~~", "*")],
];

for (const [configuration, parser] of [["plain", new Marked()], ["pi", pi]]) {
  for (const [shape, make] of shapes) {
    let low = 1;
    let high = 8000;
    const failures = new Set();
    while (low < high) {
      const middle = Math.floor((low + high + 1) / 2);
      try {
        parser.lexer(make(middle));
        low = middle;
      } catch (error) {
        // Usually RangeError; near the limit V8 can also fail compiling a regex, as a SyntaxError.
        failures.add(error.name);
        high = middle - 1;
      }
    }
    const reached = low === 8000 ? "at least 8000, the search bound" : String(low);
    console.log(`${configuration.padEnd(5)} ${shape.padEnd(8)} deepest lexed ${reached}${failures.size ? `; failures: ${[...failures].join(", ")}` : ""}`);
  }
}
