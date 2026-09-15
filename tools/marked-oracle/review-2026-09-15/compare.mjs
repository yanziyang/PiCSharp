// Compares real marked output with the C# port's output for the fresh corpus: exact key sets, value
// types and array order, as MarkedOracleTests does. Groups mismatches by corpus group and by the
// special characters present, to separate regex-translation defects from logic defects.
// Special characters are tested by code point: no Unicode escapes in regex literals.
import fs from "node:fs";
import path from "node:path";

const DIR = process.argv[2] ?? ".";
const LIMIT = Number(process.argv[3] ?? 20);
const readJsonl = (file) => fs.readFileSync(path.join(DIR, file), "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line));
const inputs = new Map(readJsonl("inputs.jsonl").map((entry) => [entry.id, entry.source]));

function difference(expected, actual, at) {
  const isObject = (value) => value !== null && typeof value === "object";
  if (!isObject(expected) || !isObject(actual)) {
    return JSON.stringify(expected) === JSON.stringify(actual) ? null : { at, expected, actual };
  }
  if (Array.isArray(expected) !== Array.isArray(actual)) return { at, expected, actual };
  if (Array.isArray(expected)) {
    if (expected.length !== actual.length) return { at: at + ".length", expected, actual };
    for (let i = 0; i < expected.length; i++) {
      const found = difference(expected[i], actual[i], `${at}[${i}]`);
      if (found) return found;
    }
    return null;
  }
  for (const key of [...new Set([...Object.keys(expected), ...Object.keys(actual)])].sort()) {
    if (!Object.hasOwn(expected, key) || !Object.hasOwn(actual, key)) {
      return { at: `${at}.${key}`, expected: expected[key], actual: actual[key], note: Object.hasOwn(expected, key) ? "missing in C#" : "extra in C#" };
    }
    const found = difference(expected[key], actual[key], `${at}.${key}`);
    if (found) return found;
  }
  return null;
}

const CASE_CODE_POINTS = [0x212a, 0x130, 0x3a3, 0x3c2, 0x3c3, 0x1e9e, 0xdf, 0x212b, 0xfb00, 0x17f];
const FEATURES = [
  ["LS/PS", (cp) => cp === 0x2028 || cp === 0x2029],
  ["NEL", (cp) => cp === 0x85],
  ["BOM", (cp) => cp === 0xfeff],
  ["NBSP", (cp) => cp === 0xa0],
  ["astral", (cp) => cp > 0xffff],
  ["CR", (cp) => cp === 0x0d],
  ["uDigit", (cp) => (cp >= 0xff10 && cp <= 0xff19) || (cp >= 0x660 && cp <= 0x669)],
  ["case", (cp) => CASE_CODE_POINTS.includes(cp)],
  ["latin1", (cp) => cp >= 0xc0 && cp <= 0xff && cp !== 0xdf],
  ["CJK", (cp) => cp >= 0x4e00 && cp <= 0x9fff],
];
function features(source) {
  const found = [];
  for (const [name, test] of FEATURES) {
    for (const character of source) {
      if (test(character.codePointAt(0))) { found.push(name); break; }
    }
  }
  return found.join("+") || "ascii";
}
const clip = (value, n = 240) => {
  const text = JSON.stringify(value);
  return text === undefined ? "undefined" : text.length > n ? text.slice(0, n) + "..." : text;
};
const show = (m) => console.log(`  ${m.id} [${features(m.source)}] source=${clip(m.source, 140)}\n      at ${m.d.at}${m.d.note ? ` (${m.d.note})` : ""}\n      marked: ${clip(m.d.expected)}\n      C#:     ${clip(m.d.actual)}`);

const configurations = process.argv.length > 4 ? process.argv.slice(4) : ["plain", "pi"];
for (const configuration of configurations) {
  const marked = new Map(readJsonl(`${configuration}.jsonl`).map((entry) => [entry.id, entry.result]));
  const port = new Map(readJsonl(`cs-${configuration}.jsonl`).map((entry) => [entry.id, entry.result]));
  const groups = new Map();
  const byFeature = new Map();
  const mismatches = [];
  for (const [id, source] of inputs) {
    const group = id.replace(/-\d+$/, "");
    const tally = groups.get(group) ?? { total: 0, mismatched: 0 };
    tally.total++;
    groups.set(group, tally);
    const d = port.has(id) ? difference(marked.get(id), port.get(id), "$") : { at: "$", note: "no C# result" };
    if (!d) continue;
    tally.mismatched++;
    mismatches.push({ id, source, d });
    const key = features(source);
    byFeature.set(key, (byFeature.get(key) ?? 0) + 1);
  }
  const errors = mismatches.filter((m) => m.d.at === "$.error" || (m.d.note && m.d.at.endsWith(".error")));
  console.log(`\n===== ${configuration}: ${mismatches.length} of ${inputs.size} cases differ (${errors.length} where only C# threw)`);
  for (const [group, tally] of groups) console.log(`  ${group.padEnd(26)} ${String(tally.mismatched).padStart(5)} / ${tally.total}`);
  console.log("  by special characters present: " + JSON.stringify(Object.fromEntries([...byFeature].sort((a, b) => b[1] - a[1]))));
  const bySize = (a, b) => a.source.length - b.source.length;
  const targeted = mismatches.filter((m) => m.id.startsWith("t-"));
  const ascii = mismatches.filter((m) => !m.id.startsWith("t-") && features(m.source) === "ascii").sort(bySize);
  const other = mismatches.filter((m) => !m.id.startsWith("t-") && features(m.source) !== "ascii").sort(bySize);
  console.log(`  --- targeted mismatches (${targeted.length}), first ${LIMIT}`);
  targeted.slice(0, LIMIT).forEach(show);
  console.log(`  --- shortest ASCII-only generated mismatches (${ascii.length}), first ${LIMIT}`);
  ascii.slice(0, LIMIT).forEach(show);
  console.log(`  --- shortest non-ASCII generated mismatches (${other.length}), first ${Math.min(LIMIT, 8)}`);
  other.slice(0, Math.min(LIMIT, 8)).forEach(show);
  fs.writeFileSync(path.join(DIR, `mismatches-${configuration}.json`), JSON.stringify(mismatches.map((m) => ({ id: m.id, source: m.source, at: m.d.at, note: m.d.note, expected: m.d.expected, actual: m.d.actual })), null, 1));
}
