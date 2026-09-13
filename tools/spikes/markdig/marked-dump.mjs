import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { execFileSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

const argumentsList = [...process.argv.slice(2)];
let markedModule = process.env.MARKED_MODULE ?? "marked";
const moduleFlag = argumentsList.indexOf("--marked-module");
if (moduleFlag >= 0) {
  markedModule = argumentsList[moduleFlag + 1];
  argumentsList.splice(moduleFlag, 2);
}

if (argumentsList.length < 2 || argumentsList.length > 3) {
  throw new Error(
    "usage: marked-dump.mjs <test-file> <inputs.json> <marked.json> [--marked-module <specifier>]\n" +
    "   or: marked-dump.mjs <inputs.json> <marked.json> [--marked-module <specifier>]",
  );
}

const harvesting = !argumentsList[0].toLowerCase().endsWith(".json");
const testPath = harvesting ? argumentsList[0] : undefined;
const inputPath = harvesting ? argumentsList[1] : argumentsList[0];
const outputPath = harvesting ? argumentsList[2] : argumentsList[1];
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");

if (harvesting) {
  const harvesterPath = fileURLToPath(new URL("./harvest-markdown-tests.mjs", import.meta.url));
  execFileSync(process.execPath, [harvesterPath, testPath, inputPath], { stdio: "inherit" });
}

const moduleSpecifier = path.isAbsolute(markedModule) || markedModule.startsWith(".")
  ? pathToFileURL(path.resolve(markedModule)).href
  : markedModule;
const { Marked, Tokenizer } = await import(moduleSpecifier);

const inputDocument = JSON.parse(fs.readFileSync(inputPath, "utf8"));
const cases = Array.isArray(inputDocument.cases)
  ? inputDocument.cases
  : Object.entries(inputDocument).map(([id, source]) => ({ id, source }));

const STRICT_STRIKETHROUGH_REGEX = /^(~~)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)/;

class StrictStrikethroughTokenizer extends Tokenizer {
  del(src) {
    const match = src.match(STRICT_STRIKETHROUGH_REGEX);
    if (!match) return undefined;
    const text = match[2];
    return {
      type: "del",
      raw: match[0],
      text,
      tokens: this.lexer.inlineTokens(text),
    };
  }
}

function scalarProperties(token) {
  const names = [
    "raw", "text", "href", "title", "lang", "depth", "ordered", "start", "checked",
    "task", "loose", "header", "align", "quote", "name", "type", "url", "pre",
  ];
  const detail = {};
  for (const name of names) {
    if (Object.hasOwn(token, name) && ["string", "number", "boolean"].includes(typeof token[name]))
      detail[name] = token[name];
  }
  if (Array.isArray(token.align)) detail.align = token.align;
  return detail;
}

function walk(tokens, types = [], nodes = []) {
  for (const token of tokens ?? []) {
    types.push(token.type);
    nodes.push({ type: token.type, ...scalarProperties(token) });
    if (token.tokens) walk(token.tokens, types, nodes);
    if (token.items) walk(token.items, types, nodes);
  }
  return { types, nodes };
}

const parser = new Marked();
parser.setOptions({ tokenizer: new StrictStrikethroughTokenizer() });
const result = {};
for (const entry of cases) {
  const source = typeof entry === "string" ? entry : entry.source;
  const walked = walk(parser.lexer(source));
  result[typeof entry === "string" ? source : entry.id] = walked;
}

const output = {
  metadata: {
    parser: "Marked",
    version: "18.0.5",
    strictStrikethrough: true,
    latexExtensions: "not loaded; the production tokenizer is the extension under test",
    caseCount: Object.keys(result).length,
    source: testPath ? path.relative(repoRoot, path.resolve(testPath)).replaceAll(path.sep, "/") : inputPath,
  },
  cases: result,
};
fs.writeFileSync(outputPath, JSON.stringify(output, null, 1));
console.log(`wrote Marked streams for ${Object.keys(result).length} cases`);
