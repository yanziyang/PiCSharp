import { Marked } from "marked";
import fs from "node:fs";

// Constructs markdown.ts actually inspects, plus classic CommonMark divergence points.
const cases = [
  ["strikethrough-strict", "~~gone~~ and ~single~"],
  ["strikethrough-spaced", "~~ padded ~~"],
  ["table", "| a | b |\n|---|---|\n| 1 | 2 |"],
  ["list-start", "3. three\n4. four"],
  ["nested-list", "- a\n  - b\n- c"],
  ["codespan", "use `x  y` here"],
  ["fenced", "```js\nlet a=1;\n```"],
  ["link", '[t](http://e.com "ti")'],
  ["html-block", "<div>\nraw\n</div>"],
  ["setext", "Title\n====="],
  ["hr", "***"],
  ["blockquote", "> q1\n> q2"],
  ["escape", "\\*not em\\*"],
  ["autolink", "<http://e.com>"],
  ["hardbreak", "a  \nb"],
];

const walk = (toks, out = []) => {
  for (const t of toks ?? []) {
    out.push(t.type);
    if (t.tokens) walk(t.tokens, out);
    if (t.items) walk(t.items, out);
  }
  return out;
};

const m = new Marked();
const inputs = {};
const types = {};
for (const [name, src] of cases) {
  inputs[name] = src;
  types[name] = walk(m.lexer(src));
}

fs.writeFileSync(process.argv[2], JSON.stringify(inputs, null, 1));
fs.writeFileSync(process.argv[3], JSON.stringify(types, null, 1));
console.log("wrote inputs + marked token types for", cases.length, "cases");
