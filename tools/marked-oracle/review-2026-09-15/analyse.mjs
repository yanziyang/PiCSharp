import fs from "node:fs";
process.chdir(process.argv[2] ?? ".");
const esc = (s) => { let out = ""; for (const ch of s) { const cp = ch.codePointAt(0); out += cp >= 0x20 && cp <= 0x7e ? ch : cp === 10 ? "\n" : cp === 9 ? "\t" : cp === 13 ? "\r" : "<U+" + cp.toString(16).toUpperCase().padStart(4, "0") + ">"; } return out; };
const clip = (v, n = 200) => { const t = v === undefined ? "undefined" : JSON.stringify(v); return esc(t.length > n ? t.slice(0, n) + "..." : t); };
const CASE = [0x212a, 0x130, 0x3a3, 0x3c2, 0x3c3, 0x1e9e, 0xdf, 0x212b, 0xfb00, 0x17f];
const feat = (s) => { const f = new Set(); for (const ch of s) { const cp = ch.codePointAt(0);
  if (cp === 0x2028 || cp === 0x2029) f.add("LS/PS"); else if (cp === 0x85) f.add("NEL"); else if (cp === 0xfeff) f.add("BOM");
  else if (cp === 0xa0) f.add("NBSP"); else if (cp > 0xffff) f.add("astral"); else if (cp === 13) f.add("CR");
  else if ((cp >= 0xff10 && cp <= 0xff19) || (cp >= 0x660 && cp <= 0x669)) f.add("uDigit"); else if (CASE.includes(cp)) f.add("case");
  else if (cp > 0x7e) f.add("other"); } return [...f]; };
const bySize = (a, b) => a.source.length - b.source.length;
const show = (m, n = 170) => console.log(`    ${m.id} src=${clip(m.source, 110)}\n      at ${m.at}${m.note ? " (" + m.note + ")" : ""}\n      marked=${clip(m.expected, n)}\n      C#    =${clip(m.actual, n)}`);
const plain = JSON.parse(fs.readFileSync("mismatches-plain.json", "utf8"));
const pi = JSON.parse(fs.readFileSync("mismatches-pi.json", "utf8"));
for (const [cfg, list] of [["plain", plain], ["pi", pi]]) {
  const errors = list.filter((m) => m.at === "$.error");
  const byMessage = new Map();
  for (const m of errors) { const key = String(m.actual).replace(/\d+/g, "N"); const e = byMessage.get(key) ?? { count: 0, shortest: m }; e.count++; if (m.source.length < e.shortest.source.length) e.shortest = m; byMessage.set(key, e); }
  console.log(`\n=== ${cfg}: ${list.length} mismatches; C#-only exceptions ${errors.length}`);
  for (const [key, e] of byMessage) console.log(`    ${e.count}x "${key}"  shortest: ${clip(e.shortest.source, 100)}`);
}
const ascii = plain.filter((m) => m.at !== "$.error" && feat(m.source).length === 0).sort(bySize);
console.log(`\n=== plain ASCII-only non-exception mismatches: ${ascii.length}`);
ascii.slice(0, 6).forEach((m) => show(m));
for (const name of ["BOM", "NEL", "LS/PS", "astral", "case", "uDigit", "NBSP", "CR"]) {
  const only = plain.filter((m) => m.at !== "$.error" && feat(m.source).length === 1 && feat(m.source)[0] === name).sort(bySize);
  console.log(`\n=== plain, only ${name} present: ${only.length}`);
  only.slice(0, 3).forEach((m) => show(m, 150));
}
const plainIds = new Set(plain.map((m) => m.id));
const piOnly = pi.filter((m) => !plainIds.has(m.id)).sort(bySize);
console.log(`\n=== pi-only mismatches (not also in plain): ${piOnly.length}`);
piOnly.slice(0, 8).forEach((m) => show(m, 150));
