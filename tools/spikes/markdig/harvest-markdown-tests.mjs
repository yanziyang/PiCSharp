import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const sourcePath = process.argv[2] ?? path.join(repoRoot, "reference/pi/packages/tui/test/markdown.test.ts");
const sourceLabel = path.relative(repoRoot, path.resolve(sourcePath)).replaceAll(path.sep, "/");
const outputPath = process.argv[3];
const source = fs.readFileSync(sourcePath, "utf8");

function isIdentifierChar(character) {
  return character !== undefined && /[A-Za-z0-9_$]/.test(character);
}

function skipQuoted(text, start, quote) {
  let index = start + 1;
  while (index < text.length) {
    if (text[index] === "\\") index += 2;
    else if (text[index] === quote) return index + 1;
    else index++;
  }
  throw new Error(`Unterminated ${quote} string at ${start}`);
}

function skipTemplate(text, start) {
  let index = start + 1;
  let interpolationDepth = 0;
  while (index < text.length) {
    const character = text[index];
    if (character === "\\") {
      index += 2;
      continue;
    }
    if (character === "`") {
      if (interpolationDepth === 0) return index + 1;
      index++;
      continue;
    }
    if (character === "$" && text[index + 1] === "{") {
      interpolationDepth++;
      index += 2;
      continue;
    }
    if (character === "}" && interpolationDepth > 0) {
      interpolationDepth--;
      index++;
      continue;
    }
    index++;
  }
  throw new Error(`Unterminated template at ${start}`);
}

function skipComment(text, start) {
  if (text.startsWith("//", start)) {
    const end = text.indexOf("\n", start + 2);
    return end < 0 ? text.length : end + 1;
  }
  if (text.startsWith("/*", start)) {
    const end = text.indexOf("*/", start + 2);
    return end < 0 ? text.length : end + 2;
  }
  return start;
}

function matchingDelimiter(text, openIndex, open = "(", close = ")") {
  const stack = [open];
  let index = openIndex + 1;
  while (index < text.length) {
    const character = text[index];
    if (character === "'" || character === '"') {
      index = skipQuoted(text, index, character);
      continue;
    }
    if (character === "`") {
      index = skipTemplate(text, index);
      continue;
    }
    if (character === "/" && (text[index + 1] === "/" || text[index + 1] === "*")) {
      index = skipComment(text, index);
      continue;
    }
    if (character === open) stack.push(open);
    else if (character === close) {
      stack.pop();
      if (stack.length === 0) return index;
    }
    index++;
  }
  throw new Error(`Unmatched ${open} at ${openIndex}`);
}

function splitTopLevel(text, separator = ",") {
  const parts = [];
  let start = 0;
  const stack = [];
  let index = 0;
  while (index < text.length) {
    const character = text[index];
    if (character === "'" || character === '"') {
      index = skipQuoted(text, index, character);
      continue;
    }
    if (character === "`") {
      index = skipTemplate(text, index);
      continue;
    }
    if (character === "/" && (text[index + 1] === "/" || text[index + 1] === "*")) {
      index = skipComment(text, index);
      continue;
    }
    if ("([{".includes(character)) stack.push(character);
    else if (")]}".includes(character)) stack.pop();
    else if (character === separator && stack.length === 0) {
      parts.push(text.slice(start, index).trim());
      start = index + 1;
    }
    index++;
  }
  parts.push(text.slice(start).trim());
  return parts;
}

function findKeywordCalls(text, keyword) {
  const calls = [];
  let index = 0;
  while (index < text.length) {
    const found = text.indexOf(keyword, index);
    if (found < 0) break;
    const before = text[found - 1];
    const after = text[found + keyword.length];
    if (!isIdentifierChar(before) && !isIdentifierChar(after)) {
      let cursor = found + keyword.length;
      while (/\s/.test(text[cursor] ?? "")) cursor++;
      if (text[cursor] === "(") {
        const end = matchingDelimiter(text, cursor);
        calls.push({ open: cursor, end, args: text.slice(cursor + 1, end) });
        index = end + 1;
        continue;
      }
    }
    index = found + keyword.length;
  }
  return calls;
}

function literalExpression(expression, environment) {
  const trimmed = expression.trim().replace(/;\s*$/, "");
  if (!trimmed) return undefined;
  if (Object.hasOwn(environment, trimmed)) return environment[trimmed];

  const withoutConst = trimmed.replace(/\s+as\s+const\s*$/, "");
  try {
    // The source is repository test data. The caller supplies only literal,
    // template, array, object, and concatenation expressions from that file.
    return Function(...Object.keys(environment), `return (${withoutConst});`)(...Object.values(environment));
  } catch {
    return undefined;
  }
}

function declarationEnvironment(body) {
  const environment = {};
  const declaration = /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*/g;
  let match;
  while ((match = declaration.exec(body)) !== null) {
    const expressionStart = declaration.lastIndex;
    let expressionEnd = expressionStart;
    const stack = [];
    while (expressionEnd < body.length) {
      const character = body[expressionEnd];
      if (character === "'" || character === '"') {
        expressionEnd = skipQuoted(body, expressionEnd, character);
        continue;
      }
      if (character === "`") {
        expressionEnd = skipTemplate(body, expressionEnd);
        continue;
      }
      if (character === "/" && (body[expressionEnd + 1] === "/" || body[expressionEnd + 1] === "*")) {
        expressionEnd = skipComment(body, expressionEnd);
        continue;
      }
      if ("([{".includes(character)) stack.push(character);
      else if (")]}".includes(character)) stack.pop();
      if ((character === ";" || character === "\n") && stack.length === 0) break;
      expressionEnd++;
    }
    const value = literalExpression(body.slice(expressionStart, expressionEnd), environment);
    if (value !== undefined) environment[match[1]] = value;
    declaration.lastIndex = expressionEnd + (body[expressionEnd] === ";" ? 1 : 0);
  }
  return environment;
}

function sourceValues(expression, environment, body) {
  const value = literalExpression(expression, environment);
  if (typeof value === "string") return [value];

  const identifier = expression.trim();
  const loopMatches = [
    ...body.matchAll(new RegExp(`for\\s*\\(\\s*const\\s*\\{\\s*${identifier}\\s*(?:,[^}]*)?}\\s+of\\s+([A-Za-z_$][\\w$]*)`, "g")),
    ...body.matchAll(new RegExp(`for\\s*\\(\\s*const\\s+${identifier}\\s+of\\s+([A-Za-z_$][\\w$]*)`, "g")),
  ];
  for (const loop of loopMatches) {
    const values = environment[loop[1]];
    if (!Array.isArray(values)) continue;
    if (loop[0].includes("{")) return values.map((entry) => entry?.[identifier]).filter((entry) => typeof entry === "string");
    return values.filter((entry) => typeof entry === "string");
  }
  return [];
}

function markdownOptions(expression, environment) {
  const value = expression ? literalExpression(expression, environment) : undefined;
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const names = ["preserveOrderedListMarkers", "preserveBackslashEscapes", "renderLatex"];
  const options = Object.fromEntries(names.filter((name) => typeof value[name] === "boolean").map((name) => [name, value[name]]));
  return Object.keys(options).length > 0 ? options : undefined;
}

function testBlocks(text) {
  const blocks = [];
  let index = 0;
  while (index < text.length) {
    const match = /\bit\s*\(/g;
    match.lastIndex = index;
    const found = match.exec(text);
    if (!found) break;
    const open = text.indexOf("(", found.index);
    const end = matchingDelimiter(text, open);
    const args = splitTopLevel(text.slice(open + 1, end));
    const name = literalExpression(args[0], {});
    const bodyStart = text.indexOf("{", open);
    if (bodyStart < 0) break;
    const bodyEnd = matchingDelimiter(text, bodyStart, "{", "}");
    const body = text.slice(bodyStart + 1, bodyEnd);
    blocks.push({ name: typeof name === "string" ? name : `unnamed-${blocks.length + 1}`, body });
    index = bodyEnd + 1;
  }
  return blocks;
}

const upstreamCases = [];
const blocks = testBlocks(source);
for (const [testIndex, test] of blocks.entries()) {
  const environment = declarationEnvironment(test.body);
  const markdownCalls = findKeywordCalls(test.body, "new Markdown");
  const values = [];
  const operationByValue = new Map();
  const optionsByValue = new Map();
  const collectValues = (calls, operation) => {
    for (const call of calls) {
      const callArguments = splitTopLevel(call.args);
      const firstArgument = callArguments[0];
      const options = operation === "constructor" ? markdownOptions(callArguments[5], environment) : undefined;
      if (!firstArgument) continue;
      for (const value of sourceValues(firstArgument, environment, test.body)) {
        if (!values.includes(value)) values.push(value);
        if (!operationByValue.has(value)) operationByValue.set(value, operation);
        if (options) optionsByValue.set(value, options);
      }
    }
  };
  collectValues(markdownCalls, "constructor");
  collectValues(findKeywordCalls(test.body, "setText"), "setText");
  for (const [inputIndex, value] of values.entries()) {
    upstreamCases.push({
      id: `test-${String(testIndex + 1).padStart(2, "0")}-${String(inputIndex + 1).padStart(2, "02")}`,
      testIndex: testIndex + 1,
      test: test.name,
      source: value,
      kind: "upstream",
      operation: operationByValue.get(value) ?? "constructor",
      ...(optionsByValue.has(value) ? { options: optionsByValue.get(value) } : {}),
    });
  }
}

if (blocks.length !== 81 || upstreamCases.length !== 94) {
  throw new Error(`Harvest guard failed: found ${blocks.length} tests and ${upstreamCases.length} inputs; expected 81 and 94`);
}

const probes = [
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
for (const [name, value] of probes) {
  upstreamCases.push({
    id: `probe-${name}`,
    testIndex: 0,
    test: `preliminary probe: ${name}`,
    source: value,
    kind: "probe",
  });
}

const output = {
  metadata: {
    source: sourceLabel,
    upstreamTestCount: blocks.length,
    upstreamInputCount: upstreamCases.filter((entry) => entry.kind === "upstream").length,
    retainedProbeCount: probes.length,
    caseCount: upstreamCases.length,
    parserInputsOnly: true,
  },
  cases: upstreamCases,
};

if (outputPath) {
  fs.writeFileSync(outputPath, JSON.stringify(output, null, 1));
  console.log(`harvested ${blocks.length} tests and ${upstreamCases.length - probes.length} upstream inputs; retained ${probes.length} probes`);
} else {
  process.stdout.write(JSON.stringify(output, null, 2));
}
