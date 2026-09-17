// Generates src/Pi.Tui/Marked/MarkedRegexes.g.cs: the regular expressions the marked port uses,
// translated from JavaScript to .NET semantics (docs/translation-patterns.md section 15).
//
// Sources, all read at generation time:
//   - marked 18.0.5's GFM block and inline rules and its `other` rules, imported from
//     reference/marked/src/rules.ts and checked against reference/marked/composed-rules.gfm.json;
//   - pi's parser patterns from reference/pi/packages/tui/src/components/markdown.ts, listed below as
//     literals and checked verbatim against that file.
//
// Unicode property classes are expanded from V8's own data, so the port classifies characters with the
// Unicode version of the Node that recorded the oracle. The build uses only the generated file.
//
// Usage: node tools/marked-oracle/generate-regexes.mjs [--check]
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const OUTPUT = path.join(ROOT, "src/Pi.Tui/Marked/MarkedRegexes.g.cs");
const CHECK = process.argv.includes("--check");
const BS = String.fromCharCode(92);

// ------------------------------------------------------------------------------------------ sources
const rules = await import(pathToFileURL(path.join(ROOT, "reference/marked/src/rules.ts")).href);
const composed = JSON.parse(fs.readFileSync(path.join(ROOT, "reference/marked/composed-rules.gfm.json"), "utf8"));
for (const level of ["block", "inline"]) {
  for (const [name, entry] of Object.entries(composed[level])) {
    const live = rules[level].gfm[name];
    if (!live || live.source !== entry.source || live.flags !== entry.flags) {
      throw new Error(`rules.ts ${level}.${name} differs from composed-rules.gfm.json`);
    }
  }
}

const OTHER_USED = [
  "codeRemoveIndent", "outputLinkReplace", "indentCodeCompensation", "beginningSpace", "endingHash",
  "startingSpaceChar", "endingSpaceChar", "nonSpaceChar", "newLineCharGlobal", "tabCharGlobal",
  "multipleSpaceGlobal", "blankLine", "doubleBlankLine", "blockquoteStart", "blockquoteSetextReplace",
  "blockquoteSetextReplace2", "listIsTask", "listReplaceTask", "listTaskCheckbox", "anyLine", "hrefBrackets",
  "tableDelimiter", "tableAlignChars", "tableRowBlankLine", "tableAlignRight", "tableAlignCenter", "tableAlignLeft",
  "startATag", "endATag", "startPreScriptTag", "endPreScriptTag", "startAngleBracket", "endAngleBracket",
  "unicodeAlphaNumeric", "findPipe", "splitPipe", "slashPipe", "carriageReturn",
];

// pi-tui's markdown.ts patterns, copied as literals and verified against the reference file below.
const PI_PATTERNS = [
  ["StrictStrikethrough", /^(~~)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)/],
  ["PendingDollarMath", /\\[A-Za-z]+|[_^=+*/<>()[\]|±≤≥≠≈∈→⇒∞∫∑√-]/],
  ["DollarThenWhitespace", /^\$\s/],
  ["EndsWithWhitespace", /\s$/],
  ["StartsWithDigit", /^\d/],
  ["UpperIdentifier", /^[A-Z_][A-Z0-9_]*(?:[^A-Za-z0-9_\s])?$/],
  ["IdentifierStart", /^[A-Za-z_][A-Za-z0-9_]*/],
  ["DollarBlock", /^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*?)\$\$[ \t]*(?:\n|$)/],
  ["BracketBlock", /^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*?)\\\][ \t]*(?:\n|$)/],
  ["PendingBracketBlock", /^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*)$/],
  ["PendingDollarBlock", /^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*)$/],
  ["LatexBlockStart", /(?:^|\n) {0,3}(?:\$\$|\\\[)/],
];
const markdownTs = fs.readFileSync(path.join(ROOT, "reference/pi/packages/tui/src/components/markdown.ts"), "utf8");
for (const [name, regex] of PI_PATTERNS) {
  const literal = "/" + regex.source.split(BS + "/").join("/") + "/" + regex.flags;
  if (!markdownTs.includes(literal)) throw new Error(`pi pattern ${name} no longer appears verbatim in markdown.ts`);
}

const pascal = (name) => name.replace(/^_/, "").replace(/^[a-z]/, (c) => c.toUpperCase());
const patterns = [];
for (const level of ["block", "inline"]) {
  for (const [name, regex] of Object.entries(rules[level].gfm)) patterns.push([pascal(level) + pascal(name), regex]);
}
for (const name of OTHER_USED) {
  const regex = rules.other[name];
  if (!(regex instanceof RegExp)) throw new Error(`other.${name} is missing`);
  patterns.push(["Other" + pascal(name), regex]);
}
const bullets = [["OrderedDot", BS + "d{1,9}" + BS + "."], ["OrderedParen", BS + "d{1,9}" + BS + ")"], ["Star", BS + "*"], ["Plus", BS + "+"], ["Dash", BS + "-"]];
for (const [suffix, bull] of bullets) patterns.push(["OtherListItem" + suffix, rules.other.listItemRegex(bull)]);
for (const fn of ["nextBulletRegex", "hrRegex", "fencesBeginRegex", "headingBeginRegex", "htmlBeginRegex", "blockquoteBeginRegex"]) {
  for (let cacheIndex = 0; cacheIndex <= 3; cacheIndex++) {
    patterns.push(["Other" + pascal(fn.replace(/Regex$/, "")) + cacheIndex, rules.other[fn](cacheIndex + 1)]);
  }
}
for (const [name, regex] of PI_PATTERNS) patterns.push(["Pi" + name, regex]);

// ------------------------------------------------------------------------------------ unicode sets
// A set is { bmp: Uint8Array(0x10000), astral: [[lo, hi], ...] } over code points.
const emptySet = () => ({ bmp: new Uint8Array(0x10000), astral: [] });
function addRange(set, lo, hi) {
  for (let cp = lo; cp <= Math.min(hi, 0xffff); cp++) set.bmp[cp] = 1;
  if (hi > 0xffff) set.astral.push([Math.max(lo, 0x10000), hi]);
}
function normaliseAstral(ranges) {
  const sorted = ranges.slice().sort((a, b) => a[0] - b[0]);
  const merged = [];
  for (const [lo, hi] of sorted) {
    const last = merged.at(-1);
    if (last && lo <= last[1] + 1) last[1] = Math.max(last[1], hi);
    else merged.push([lo, hi]);
  }
  return merged;
}
function unionInto(target, source) {
  for (let cp = 0; cp < 0x10000; cp++) if (source.bmp[cp]) target.bmp[cp] = 1;
  target.astral = normaliseAstral(target.astral.concat(source.astral));
}
function complement(set) {
  const result = emptySet();
  for (let cp = 0; cp < 0x10000; cp++) result.bmp[cp] = set.bmp[cp] ? 0 : 1;
  let next = 0x10000;
  for (const [lo, hi] of normaliseAstral(set.astral)) {
    if (lo > next) result.astral.push([next, lo - 1]);
    next = hi + 1;
  }
  if (next <= 0x10ffff) result.astral.push([next, 0x10ffff]);
  return result;
}

const propertyCache = new Map();
function propertySet(name) {
  if (propertyCache.has(name)) return propertyCache.get(name);
  const test = new RegExp("^" + BS + "p{" + name + "}$", "u");
  const set = emptySet();
  for (let cp = 0; cp < 0x10000; cp++) {
    if (cp >= 0xd800 && cp <= 0xdfff) continue;
    if (test.test(String.fromCharCode(cp))) set.bmp[cp] = 1;
  }
  let start = -1;
  for (let cp = 0x10000; cp <= 0x110000; cp++) {
    const inside = cp <= 0x10ffff && test.test(String.fromCodePoint(cp));
    if (inside && start < 0) start = cp;
    if (!inside && start >= 0) { set.astral.push([start, cp - 1]); start = -1; }
  }
  propertyCache.set(name, set);
  return set;
}
const whitespaceSet = (() => {
  const test = new RegExp("^" + BS + "s$");
  const set = emptySet();
  for (let cp = 0; cp < 0x10000; cp++) if (test.test(String.fromCharCode(cp))) set.bmp[cp] = 1;
  return set;
})();
const digitSet = (() => { const s = emptySet(); addRange(s, 0x30, 0x39); return s; })();
// The non-unicode i flag compares Canonicalize(ch) (ECMA-262): the single-unit uppercase mapping, except that
// a non-ASCII unit never canonicalises to an ASCII one. So k matches K but not the Kelvin sign, and s does not
// match U+017F. Emitting each class and literal as its exact case closure needs no .NET IgnoreCase at all.
const canonical = (() => {
  const table = new Uint16Array(0x10000);
  for (let unit = 0; unit < 0x10000; unit++) {
    const upper = String.fromCharCode(unit).toUpperCase();
    table[unit] = upper.length === 1 && !(unit >= 128 && upper.charCodeAt(0) < 128) ? upper.charCodeAt(0) : unit;
  }
  return table;
})();
function caseClosure(bmp) {
  const wanted = new Set();
  for (let unit = 0; unit < 0x10000; unit++) if (bmp[unit]) wanted.add(canonical[unit]);
  const result = new Uint8Array(0x10000);
  for (let unit = 0; unit < 0x10000; unit++) if (wanted.has(canonical[unit])) result[unit] = 1;
  return result;
}
const wordSet = (() => { const s = emptySet(); addRange(s, 0x30, 0x39); addRange(s, 0x41, 0x5a); addRange(s, 0x61, 0x7a); addRange(s, 0x5f, 0x5f); return s; })();

// --------------------------------------------------------------------------------------- emitting
const hex4 = (unit) => BS + "u" + unit.toString(16).toUpperCase().padStart(4, "0");
const HIGH = "[" + hex4(0xd800) + "-" + hex4(0xdbff) + "]";
const LOW = "[" + hex4(0xdc00) + "-" + hex4(0xdfff) + "]";
const LINE_TERMINATORS = BS + "n" + BS + "r" + hex4(0x2028) + hex4(0x2029);
const WORD = "[A-Za-z0-9_]";

function classUnit(unit) {
  if (unit >= 0x20 && unit < 0x7f) {
    const ch = String.fromCharCode(unit);
    return (BS + "[]^-").includes(ch) ? BS + ch : ch;
  }
  return hex4(unit);
}
// A range endpoint is written as a hex escape unless alphanumeric. .NET does not take an escaped hyphen as the
// start of a range, so emitting one silently turns the range into literal characters.
const rangeEnd = (unit) => (/[A-Za-z0-9]/.test(String.fromCharCode(unit)) ? String.fromCharCode(unit) : hex4(unit));
function bmpRanges(bmp) {
  const parts = [];
  for (let cp = 0; cp < 0x10000; cp++) {
    if (!bmp[cp]) continue;
    let end = cp;
    while (end + 1 < 0x10000 && bmp[end + 1]) end++;
    if (end === cp) parts.push(classUnit(cp));
    else if (end === cp + 1) parts.push(classUnit(cp) + classUnit(end));
    else parts.push(rangeEnd(cp) + "-" + rangeEnd(end));
    cp = end;
  }
  return parts.join("");
}
function astralAlternation(ranges) {
  const byHigh = new Map();
  for (const [lo, hi] of normaliseAstral(ranges)) {
    for (let cp = lo; cp <= hi;) {
      const high = 0xd800 + ((cp - 0x10000) >> 10);
      const lastInHigh = 0x10000 + ((high - 0xd800 + 1) << 10) - 1;
      const end = Math.min(hi, lastInHigh);
      if (!byHigh.has(high)) byHigh.set(high, []);
      byHigh.get(high).push([0xdc00 + ((cp - 0x10000) & 0x3ff), 0xdc00 + ((end - 0x10000) & 0x3ff)]);
      cp = end + 1;
    }
  }
  const lowClass = (lows) => {
    if (lows.length === 1 && lows[0][0] === lows[0][1]) return hex4(lows[0][0]);
    return "[" + lows.map(([a, b]) => (a === b ? hex4(a) : hex4(a) + "-" + hex4(b))).join("") + "]";
  };
  const entries = [...byHigh.entries()].sort((a, b) => a[0] - b[0]).map(([high, lows]) => [high, lowClass(lows)]);
  const parts = [];
  for (let i = 0; i < entries.length;) {
    let j = i;
    while (j + 1 < entries.length && entries[j + 1][0] === entries[j][0] + 1 && entries[j + 1][1] === entries[i][1]) j++;
    const highs = i === j ? hex4(entries[i][0]) : "[" + hex4(entries[i][0]) + "-" + hex4(entries[j][0]) + "]";
    parts.push(highs + entries[i][1]);
    i = j + 1;
  }
  return parts.length === 1 ? parts[0] : "(?:" + parts.join("|") + ")";
}
const hasBmp = (bmp) => bmp.some((v) => v === 1);

function emitClass(set, negated, unicode) {
  if (!unicode) {
    // Only complement shorthands add astral ranges here, and their code units are already in bmp.
    return "[" + (negated ? "^" : "") + bmpRanges(set.bmp) + "]";
  }
  const bmp = set.bmp.slice();
  for (let cp = 0xd800; cp <= 0xdfff; cp++) bmp[cp] = 0;
  if (!negated) {
    const parts = [];
    if (set.astral.length) parts.push(astralAlternation(set.astral));
    if (hasBmp(bmp)) parts.push("[" + bmpRanges(bmp) + "]");
    if (parts.length === 0) return "(?!)";
    return parts.length === 1 ? parts[0] : "(?:" + parts.join("|") + ")";
  }
  // A negated class matches any code point outside the set: a BMP unit, a whole surrogate pair, or a
  // lone surrogate (category Cs, never a member here). It must never match half of a pair.
  const outside = bmp.slice();
  for (let cp = 0xd800; cp <= 0xdfff; cp++) outside[cp] = 1;
  const pair = (set.astral.length ? "(?!" + astralAlternation(set.astral) + ")" : "") + HIGH + LOW;
  return "(?:[^" + bmpRanges(outside) + "]|" + pair + "|" + HIGH + "(?!" + LOW + ")|(?<!" + HIGH + ")" + LOW + ")";
}

function literal(cp, ctx) {
  if (ctx.ignoreCase && cp < 0x10000) {
    const single = new Uint8Array(0x10000);
    single[cp] = 1;
    const closed = caseClosure(single);
    if (closed.reduce((count, value) => count + value, 0) > 1) return "[" + bmpRanges(closed) + "]";
  }
  if (cp > 0xffff) {
    const units = String.fromCodePoint(cp);
    return hex4(units.charCodeAt(0)) + hex4(units.charCodeAt(1));
  }
  if (cp >= 0x20 && cp < 0x7f) {
    const ch = String.fromCharCode(cp);
    return (BS + "*+?|{}[]()^$.#").includes(ch) ? BS + ch : ch;
  }
  return hex4(cp);
}

// ------------------------------------------------------------------------------------- translating
function readEscapeUnit(source, i) {
  // i points at the character after the backslash; returns [codeUnit or null, nextIndex].
  const c = source[i];
  const simple = { n: 10, r: 13, t: 9, v: 11, f: 12, 0: 0 };
  if (c in simple && !(c === "0" && /[0-9]/.test(source[i + 1] ?? ""))) return [simple[c], i + 1];
  if (c === "x") return [parseInt(source.slice(i + 1, i + 3), 16), i + 3];
  if (c === "u" && source[i + 1] !== "{") return [parseInt(source.slice(i + 1, i + 5), 16), i + 5];
  if (/[A-Za-z0-9]/.test(c)) return [null, i];
  return [source.codePointAt(i), i + (source.codePointAt(i) > 0xffff ? 2 : 1)];
}

function shorthandSet(letter, ctx, source, i) {
  switch (letter) {
    case "d": return [digitSet, false, i + 1];
    case "D": return [complement(digitSet), true, i + 1];
    case "w": return [wordSet, false, i + 1];
    case "W": return [complement(wordSet), true, i + 1];
    case "s": return [whitespaceSet, false, i + 1];
    case "S": return [complement(whitespaceSet), true, i + 1];
    case "p":
    case "P": {
      if (!ctx.unicode) throw new Error("property escape without the u flag");
      const close = source.indexOf("}", i);
      const set = propertySet(source.slice(i + 2, close));
      return [letter === "p" ? set : complement(set), letter === "P", close + 1];
    }
    default: return null;
  }
}

function parseClass(source, start, ctx) {
  let i = start;
  let negated = false;
  if (source[i] === "^") { negated = true; i++; }
  if (source[i] === "]") throw new Error("empty JavaScript class");
  const set = emptySet();
  const readAtom = () => {
    if (source[i] === BS) {
      const shorthand = shorthandSet(source[i + 1], ctx, source, i + 1);
      if (shorthand) {
        const [members, isComplement, next] = shorthand;
        i = next;
        return { set: members, isComplement };
      }
      if (source[i + 1] === "b") { i += 2; return { unit: 8 }; }
      const [unit, next] = readEscapeUnit(source, i + 1);
      if (unit === null) throw new Error(`unsupported class escape ${BS}${source[i + 1]}`);
      i = next;
      return { unit };
    }
    // Without the u flag a JavaScript class reads UTF-16 code units, not code points.
    const cp = ctx.unicode ? source.codePointAt(i) : source.charCodeAt(i);
    i += cp > 0xffff ? 2 : 1;
    return { unit: cp };
  };
  while (source[i] !== "]") {
    if (i >= source.length) throw new Error("unterminated class");
    const atom = readAtom();
    if (atom.set) {
      unionInto(set, atom.set);
      continue;
    }
    if (source[i] === "-" && source[i + 1] !== "]") {
      i++;
      const upper = readAtom();
      if (upper.set) throw new Error("class range bounded by a shorthand");
      if (upper.unit < atom.unit) throw new Error("reversed class range");
      addRange(set, atom.unit, upper.unit);
      continue;
    }
    addRange(set, atom.unit, atom.unit);
  }
  if (ctx.ignoreCase) {
    if (ctx.unicode) throw new Error("the i and u flags together use case folding, which is not supported");
    set.bmp = caseClosure(set.bmp);
  }
  return [set, negated, i + 1];
}

function translate(name, regex) {
  const source = regex.source;
  const flags = regex.flags;
  for (const flag of flags) if (!"gimsuy".includes(flag)) throw new Error(`${name}: unsupported flag ${flag}`);
  const ctx = { ignoreCase: flags.includes("i"), multiline: flags.includes("m"), unicode: flags.includes("u"), dotAll: flags.includes("s") };
  let out = "";
  let captures = 0;
  const names = new Map();
  const stack = [];
  const groupSource = [];
  const backreference = (number) => {
    if (ctx.ignoreCase) {
      const text = groupSource[number];
      if (text === undefined || /[kK]/.test(text) || /[^\x00-\x7f]/.test(text)) {
        throw new Error(`${name}: case-insensitive backreference to a group .NET would fold differently`);
      }
      return "(?i:" + BS + number + ")";
    }
    return "(?:" + BS + number + ")";
  };
  let i = 0;
  while (i < source.length) {
    const ch = source[i];
    if (ch === BS) {
      const next = source[i + 1];
      const shorthand = shorthandSet(next, ctx, source, i + 1);
      if (shorthand) {
        const [members, isComplement, end] = shorthand;
        out += isComplement ? emitClass(complement(members), true, ctx.unicode) : emitClass(members, false, ctx.unicode);
        i = end;
        continue;
      }
      if (next === "b") { out += "(?:(?<=" + WORD + ")(?!" + WORD + ")|(?<!" + WORD + ")(?=" + WORD + "))"; i += 2; continue; }
      if (next === "B") { out += "(?:(?<=" + WORD + ")(?=" + WORD + ")|(?<!" + WORD + ")(?!" + WORD + "))"; i += 2; continue; }
      if (next === "k") {
        const close = source.indexOf(">", i);
        out += backreference(names.get(source.slice(i + 3, close)));
        i = close + 1;
        continue;
      }
      if (/[1-9]/.test(next)) {
        let end = i + 1;
        while (/[0-9]/.test(source[end] ?? "")) end++;
        out += backreference(Number(source.slice(i + 1, end)));
        i = end;
        continue;
      }
      const [unit, end] = readEscapeUnit(source, i + 1);
      if (unit === null) throw new Error(`${name}: unsupported escape ${BS}${next}`);
      out += literal(unit, ctx);
      i = end;
      continue;
    }
    if (ch === "[") {
      const [set, negated, end] = parseClass(source, i + 1, ctx);
      out += emitClass(set, negated, ctx.unicode);
      i = end;
      continue;
    }
    if (ch === "(") {
      if (source.startsWith("(?<=", i) || source.startsWith("(?<!", i)) { stack.push(0); out += source.slice(i, i + 4); i += 4; continue; }
      if (source.startsWith("(?<", i)) {
        const close = source.indexOf(">", i);
        captures++;
        names.set(source.slice(i + 3, close), captures);
        stack.push({ number: captures, start: close + 1 });
        out += "(";
        i = close + 1;
        continue;
      }
      if (source.startsWith("(?:", i) || source.startsWith("(?=", i) || source.startsWith("(?!", i)) { stack.push(0); out += source.slice(i, i + 3); i += 3; continue; }
      if (source[i + 1] === "?") throw new Error(`${name}: unsupported group`);
      captures++;
      stack.push({ number: captures, start: i + 1 });
      out += "(";
      i++;
      continue;
    }
    if (ch === ")") {
      const group = stack.pop();
      if (group) groupSource[group.number] = source.slice(group.start, i);
      out += ")";
      i++;
      continue;
    }
    if (ch === ".") {
      if (ctx.dotAll) out += ctx.unicode ? emitClass(emptySet(), true, true) : "[" + BS + "s" + BS + "S]";
      else if (ctx.unicode) out += emitClass((() => { const s = emptySet(); addRange(s, 10, 10); addRange(s, 13, 13); addRange(s, 0x2028, 0x2029); return s; })(), true, true);
      else out += "[^" + LINE_TERMINATORS + "]";
      i++;
      continue;
    }
    if (ch === "^") { out += ctx.multiline ? "(?:^|(?<=[" + LINE_TERMINATORS + "]))" : "^"; i++; continue; }
    if (ch === "$") { out += ctx.multiline ? "(?=[" + LINE_TERMINATORS + "]|" + BS + "z)" : BS + "z"; i++; continue; }
    if (ch === "{") {
      const quantifier = /^\{[0-9]+(,[0-9]*)?\}/.exec(source.slice(i));
      if (quantifier) { out += quantifier[0]; i += quantifier[0].length; continue; }
      out += BS + "{";
      i++;
      continue;
    }
    if ("*+?|".includes(ch)) { out += ch; i++; continue; }
    const cp = source.codePointAt(i);
    out += literal(cp, ctx);
    i += cp > 0xffff ? 2 : 1;
  }
  if (stack.length) throw new Error(`${name}: unbalanced groups`);
  // The y flag anchors the match at lastIndex, which is .NET's \G at startat.
  return { pattern: flags.includes("y") ? BS + "G(?:" + out + ")" : out, captures };
}

// ------------------------------------------------------------------------------ delimiter classifiers
// Tokenizer.ts reads which capture group of emStrongRDelimAst, emStrongRDelimUnd and delRDelim matched. In .NET,
// reading captures allocates a Match for every delimiter scanned, and under deeply nested emphasis every garbage
// collection then walks a deep stack, so the cost grows with the cube of the depth. The port scans without
// captures and asks two sticky patterns, built from the same alternatives, whether groups 1-2 or groups 3-4 match
// where the scan matched. That is exact only for the shape asserted here: capture-free alternatives first, then
// alternatives of one character, one captured delimiter run and lookaheads, so the captured run is the match
// minus its first code point.

// Top-level items of a JavaScript pattern: classes, escapes, groups, `|` and single characters, with quantifiers.
function topLevelItems(source) {
  const escapeEnd = (i) => (/[pPu]/.test(source[i + 1]) && source[i + 2] === "{" ? source.indexOf("}", i) + 1 : i + 2);
  const classEnd = (i) => {
    for (i++; source[i] !== "]"; ) i = source[i] === BS ? escapeEnd(i) : i + 1;
    return i + 1;
  };
  const items = [];
  let i = 0;
  while (i < source.length) {
    const start = i;
    let kind = "char";
    if (source[i] === BS) {
      i = escapeEnd(i);
      kind = "escape";
    } else if (source[i] === "[") {
      i = classEnd(i);
      kind = "class";
    } else if (source[i] === "(") {
      let depth = 0;
      do {
        if (source[i] === BS) i = escapeEnd(i);
        else if (source[i] === "[") i = classEnd(i);
        else {
          if (source[i] === "(") depth++;
          if (source[i] === ")") depth--;
          i++;
        }
      } while (depth > 0);
      const opener = source.slice(start, start + 3);
      kind = opener === "(?=" || opener === "(?!" ? "lookahead" : opener === "(?:" ? "group" : opener[1] === "?" ? "other" : "capture";
    } else if (source[i] === "|") {
      i++;
      kind = "or";
    } else {
      i++;
    }
    const quantifier = /^(?:[*+?]|\{\d+(?:,\d*)?\})\??/.exec(source.slice(i));
    if (quantifier) i += quantifier[0].length;
    items.push({ kind, text: source.slice(start, i), quantified: quantifier !== null });
  }
  return items;
}

function alternatives(source) {
  const result = [[]];
  for (const item of topLevelItems(source)) {
    if (item.kind === "or") result.push([]);
    else result[result.length - 1].push(item);
  }
  return result;
}

// The capture-free alternatives marked 18.0.5 uses. Checked by hand: none can have the delimiter right after its
// first code point, which is how the port tells them apart from a captured run.
const CAPTURE_FREE = new Set([
  String.raw`^[^_*]*?__[^_*]*?\*[^_*]*?(?=__)`,
  String.raw`[^*]+(?=[^*])`,
  String.raw`^[^_*]*?\*\*[^_*]*?_[^_*]*?(?=\*\*)`,
  String.raw`[^_]+(?=[^_])`,
  String.raw`^[^~]+(?=[^~])`,
]);
const DELIMITER_RUNS = [String.raw`(\*+)`, "(_+)", "(~~?)"];

const oneCodePoint = (item) =>
  item !== undefined && !item.quantified &&
  (item.kind === "class" || /^\\[sSdDwW]$/.test(item.text) ||
    (item.kind === "group" && alternatives(item.text.slice(3, -1)).every((option) =>
      option.length === 1 && !option[0].quantified && (option[0].kind === "class" || (option[0].kind === "char" && !"^$".includes(option[0].text))))));

function delimiterClassifiers(name, regex) {
  const captured = [];
  for (const alternative of alternatives(regex.source)) {
    const text = alternative.map((item) => item.text).join("");
    const captures = translate(name, new RegExp(text, "u")).captures;
    if (captures === 0) {
      if (captured.length > 0 || !CAPTURE_FREE.has(text)) throw new Error(`${name}: unexpected capture-free alternative ${text}`);
      continue;
    }
    let k = 0;
    while (alternative[k]?.kind === "lookahead" && !alternative[k].quantified) k++;
    const run = alternative[k + 1];
    const shaped = captures === 1 && oneCodePoint(alternative[k]) && run?.kind === "capture" && !run.quantified &&
      DELIMITER_RUNS.includes(run.text) && alternative.slice(k + 2).every((item) => item.kind === "lookahead" && !item.quantified);
    if (!shaped) throw new Error(`${name}: alternative ${text} is not one character, a captured delimiter run and lookaheads`);
    captured.push(text);
  }
  if (captured.length < 5) throw new Error(`${name}: expected at least five captured alternatives`);
  const sticky = (texts) => new RegExp("(?:" + texts.join("|") + ")", regex.flags.replace("g", "") + "y");
  return [[name + "Groups12", sticky(captured.slice(0, 2))], [name + "Groups34", sticky(captured.slice(2, 4))]];
}

for (const [name, regex] of [
  ["InlineEmStrongRDelimAst", rules.inline.gfm.emStrongRDelimAst],
  ["InlineEmStrongRDelimUnd", rules.inline.gfm.emStrongRDelimUnd],
  ["InlineDelRDelim", rules.inline.gfm.delRDelim],
]) {
  patterns.push(...delimiterClassifiers(name, regex));
}

// ------------------------------------------------------------------------------------------ output
const lines = [
  "// <auto-generated>",
  "// Generated by tools/marked-oracle/generate-regexes.mjs. Do not edit; regenerate instead.",
  "// Sources: marked 18.0.5 rules (reference/marked; see LICENSE in this directory) and pi-tui's markdown.ts (reference/pi).",
  `// Unicode property classes expanded from V8 (Node ${process.version}, Unicode ${process.versions.unicode}).`,
  "// </auto-generated>",
  "using System.Text.RegularExpressions;",
  "",
  "namespace Pi.Tui;",
  "",
  "internal static partial class MarkedRegexes",
  "{",
];
const asciiComment = (text) => [...text].map((c) => (c.codePointAt(0) < 0x7f && c.codePointAt(0) >= 0x20 ? c : hex4(c.codePointAt(0)))).join("");
for (const [name, regex] of patterns) {
  const { pattern } = translate(name, regex);
  lines.push(`    // JavaScript: /${asciiComment(regex.source)}/${regex.flags}`);
  lines.push(`    [GeneratedRegex(@"${pattern.split('"').join('""')}", RegexOptions.CultureInvariant)]`);
  lines.push(`    internal static partial Regex ${name}();`);
  lines.push("");
}
lines[lines.length - 1] = "}";
const text = lines.join("\n") + "\n";

// --manifest <path> also writes the pattern list (name, JavaScript source, flags) for differential checks.
const manifestIndex = process.argv.indexOf("--manifest");
if (manifestIndex >= 0) {
  fs.writeFileSync(process.argv[manifestIndex + 1], JSON.stringify(patterns.map(([name, regex]) => ({ name, source: regex.source, flags: regex.flags }))));
}

if (CHECK) {
  const current = fs.existsSync(OUTPUT) ? fs.readFileSync(OUTPUT, "utf8") : "";
  if (current !== text) {
    console.error("MarkedRegexes.g.cs is out of date: run node tools/marked-oracle/generate-regexes.mjs");
    process.exit(1);
  }
  console.log("MarkedRegexes.g.cs is up to date");
} else {
  fs.writeFileSync(OUTPUT, text);
  console.log(`wrote ${path.relative(ROOT, OUTPUT)}: ${patterns.length} patterns, ${Buffer.byteLength(text)} bytes`);
}
