// Compares JavaScript and .NET match offsets per (pattern, input); prints each differing pattern with
// its shortest differing input.
// Usage: node compare-regexes.mjs <regexcheck dir> [limit]
import fs from "node:fs";
import path from "node:path";

const DIR = process.argv[2];
const LIMIT = Number(process.argv[3] ?? 40);
const load = (file) => {
  const map = new Map();
  for (const line of fs.readFileSync(path.join(DIR, file), "utf8").split("\n")) {
    if (!line) continue;
    const value = JSON.parse(line);
    map.set(value.p + "#" + value.n, JSON.stringify(value.r));
  }
  return map;
};
const js = load("js.jsonl");
const cs = load("cs.jsonl");
const inputs = JSON.parse(fs.readFileSync(path.join(DIR, "inputs.json"), "utf8"));
const printable = (text) => {
  let out = "";
  for (const ch of text) {
    const cp = ch.codePointAt(0);
    if (cp === 10) out += "\\n";
    else if (cp === 9) out += "\\t";
    else if (cp >= 0x20 && cp < 0x7f) out += ch;
    else out += "<U+" + cp.toString(16).toUpperCase().padStart(4, "0") + ">";
  }
  return JSON.stringify(out);
};

const keys = new Set([...js.keys(), ...cs.keys()]);
const byPattern = new Map();
for (const key of keys) {
  const expected = js.get(key) ?? "no match";
  const actual = cs.get(key) ?? "no match";
  if (expected === actual) continue;
  const [pattern, n] = key.split("#");
  if (!byPattern.has(pattern)) byPattern.set(pattern, []);
  byPattern.get(pattern).push({ n: Number(n), expected, actual });
}
console.log(`pairs with a match on either side: ${keys.size}; patterns that differ: ${byPattern.size}`);
for (const [pattern, list] of [...byPattern].sort((a, b) => b[1].length - a[1].length).slice(0, LIMIT)) {
  list.sort((a, b) => inputs[a.n].length - inputs[b.n].length);
  const first = list[0];
  console.log(`${pattern}: ${list.length} inputs differ; shortest ${printable(inputs[first.n])}`);
  console.log(`    js  ${first.expected.slice(0, 200)}`);
  console.log(`    .net ${first.actual.slice(0, 200)}`);
}
