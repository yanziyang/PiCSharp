// Runs every translated pattern's JavaScript original over probe inputs and records match and group
// offsets (the d flag), so the .NET translation can be compared exactly without encoding group text.
// Usage: node record-regexes.mjs <regexcheck dir> <fuzz inputs.jsonl>
import fs from "node:fs";
import path from "node:path";

const DIR = process.argv[2];
const manifest = JSON.parse(fs.readFileSync(path.join(DIR, "manifest.json"), "utf8"));
const fuzz = fs.readFileSync(process.argv[3], "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line));

const chosen = fuzz.filter((entry, index) => entry.id.startsWith("t-") || index % 5 === 0).map((entry) => entry.source);
// Suffixes exercise matching from mid-string starts. Never start inside a surrogate pair: .NET's JSON reader
// rejects the lone low surrogate that would leave, and marked never slices one off either.
const startAt = (source, index) => {
  const unit = source.charCodeAt(index);
  return unit >= 0xdc00 && unit <= 0xdfff ? index + 1 : index;
};
const inputs = [];
for (const source of chosen) {
  inputs.push(source);
  if (source.length > 2) {
    inputs.push(source.slice(startAt(source, 1)));
    inputs.push(source.slice(startAt(source, Math.floor(source.length / 2))));
  }
}
fs.writeFileSync(path.join(DIR, "inputs.json"), JSON.stringify(inputs));

const lines = [];
let matches = 0;
const offsets = (match) => Array.from(match.indices, (range) => (range ? [range[0], range[1]] : null));
for (const { name, source, flags } of manifest) {
  const global = flags.includes("g");
  const regex = new RegExp(source, flags + "d");
  inputs.forEach((input, n) => {
    const results = [];
    if (flags.includes("y")) {
      // A sticky pattern is asked at every start that is not inside a surrogate pair, as the port asks it.
      for (let start = 0; start <= input.length && results.length < 100; start++) {
        const unit = input.charCodeAt(start);
        if (start > 0 && unit >= 0xdc00 && unit <= 0xdfff && input.charCodeAt(start - 1) >= 0xd800 && input.charCodeAt(start - 1) <= 0xdbff) continue;
        regex.lastIndex = start;
        const match = regex.exec(input);
        if (match) results.push(offsets(match));
      }
    } else {
      regex.lastIndex = 0;
      for (;;) {
        const match = regex.exec(input);
        if (!match) break;
        results.push(offsets(match));
        if (!global || results.length >= 100) break;
        // .NET NextMatch after an empty match resumes one code unit later; do the same.
        if (match[0].length === 0) regex.lastIndex = match.index + 1;
      }
    }
    if (results.length) {
      matches += results.length;
      lines.push(JSON.stringify({ p: name, n, r: results }));
    }
  });
}
fs.writeFileSync(path.join(DIR, "js.jsonl"), lines.join("\n") + "\n");
console.log(`patterns ${manifest.length}, inputs ${inputs.length}, matching pairs ${lines.length}, matches ${matches}`);
