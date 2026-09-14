import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { stripTypeScriptTypes } from "node:module";
import { execFileSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const PINNED = path.join(ROOT, "reference/marked/PINNED");
const CORPUS = path.join(ROOT, "tests/fixtures/marked/corpus.jsonl");
const OUT_DIR = path.join(ROOT, "tests/fixtures/marked");
const MARKDOWN = path.join(ROOT, "reference/pi/packages/tui/src/components/markdown.ts");
const args = process.argv.slice(2);
function argumentValue(name) {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
}
const requestedModule = argumentValue("--marked-module") || process.env.MARKED_MODULE;
const scratch = argumentValue("--scratch") || fs.mkdtempSync(path.join(os.tmpdir(), "picsharp-marked-"));

function die(message) {
  throw new Error(`[marked-oracle] ${message}`);
}

const pinText = fs.readFileSync(PINNED, "utf8");
const pin = /^marked\s+(\S+)\s+(\S+)\s*$/m.exec(pinText);
if (!pin) die(`cannot parse ${PINNED}`);
const pinnedVersion = pin[1];
const pinnedIntegrity = pin[2];

function packageIntegrity(packageRoot) {
  const pkg = JSON.parse(fs.readFileSync(path.join(packageRoot, "package.json"), "utf8"));
  const lockPath = path.join(packageRoot, "package-lock.json");
  let integrity = pkg._integrity || "";
  if (!integrity && fs.existsSync(lockPath)) {
    const lock = JSON.parse(fs.readFileSync(lockPath, "utf8"));
    integrity = lock.packages?.["node_modules/marked"]?.integrity || "";
  }
  if (!integrity) {
    const upstreamLock = path.join(ROOT, "reference/pi/package-lock.json");
    if (fs.existsSync(upstreamLock)) {
      const lock = JSON.parse(fs.readFileSync(upstreamLock, "utf8"));
      integrity = lock.packages?.["node_modules/marked"]?.integrity || "";
    }
  }
  return { version: pkg.version, integrity };
}

let moduleRoot;
if (requestedModule) {
  const resolved = path.resolve(requestedModule);
  moduleRoot = fs.existsSync(path.join(resolved, "package.json")) ? resolved : path.dirname(require.resolve(requestedModule));
} else {
  const prefix = path.join(scratch, "marked-install");
  fs.mkdirSync(prefix, { recursive: true });
  execFileSync("npm", ["install", "--ignore-scripts", "--no-package-lock", "--prefix", prefix, `marked@${pinnedVersion}`], { stdio: "inherit" });
  moduleRoot = path.join(prefix, "node_modules", "marked");
}
const packageInfo = packageIntegrity(moduleRoot);
if (packageInfo.version !== pinnedVersion) die(`marked version ${packageInfo.version}, expected ${pinnedVersion}`);
if (!packageInfo.integrity) die(`marked integrity is unavailable for ${moduleRoot}`);
if (packageInfo.integrity !== pinnedIntegrity) die(`marked integrity ${packageInfo.integrity}, expected ${pinnedIntegrity}`);

const markedEntry = fs.existsSync(path.join(moduleRoot, "lib/marked.esm.js"))
  ? path.join(moduleRoot, "lib/marked.esm.js")
  : moduleRoot;
const markedSpecifier = pathToFileURL(markedEntry).href;
const { Marked, Tokenizer } = await import(markedSpecifier);

function readCorpus() {
  const raw = fs.readFileSync(CORPUS, "utf8");
  const entries = raw.split("\n").filter(Boolean).map(line => JSON.parse(line));
  return { raw, entries };
}

function scalarProperties(token) {
  const names = ["raw", "text", "href", "title", "lang", "depth", "ordered", "start", "checked", "task", "loose", "header", "align", "type", "pre", "block", "inLink", "inRawBlock", "escaped", "codeBlockStyle", "tag"];
  const result = {};
  for (const name of names) if (Object.hasOwn(token, name)) result[name] = token[name];
  return result;
}

function makePiParser() {
  const source = fs.readFileSync(MARKDOWN, "utf8");
  const start = source.indexOf("const STRICT_STRIKETHROUGH_REGEX");
  const endMarker = "markdownParser.use({ extensions: [...LATEX_MARKDOWN_EXTENSIONS] });";
  const end = source.indexOf(endMarker);
  if (start < 0 || end < 0) die("markdown.ts parser markers are missing");
  const extracted = source.slice(start, end + endMarker.length);
  const stripped = stripTypeScriptTypes(extracted, { mode: "strip" });
  const moduleText = `import { Marked, Tokenizer } from ${JSON.stringify(markedSpecifier)};\n${stripped}\nexport { markdownParser };`;
  const temp = path.join(scratch, "pi-parser.mjs");
  fs.writeFileSync(temp, moduleText);
  return import(pathToFileURL(temp).href).then(mod => mod.markdownParser);
}

class ProbeTokenizer extends Tokenizer {
  heading(src) {
    return super.heading(src);
  }
}

function probeBlock(name, raw, tokens, marker) {
  const text = raw.slice(marker.length, raw.length - marker.length);
  return { type: name, raw, text, seen: tokens.length, tokens: this.lexer.inlineTokens(text) };
}

function makeProbeParser() {
  const parser = new Marked();
  parser.setOptions({ tokenizer: new ProbeTokenizer() });
  parser.use({ extensions: [
    {
      name: "probeBlockFirst",
      level: "block",
      start(source) { return source.indexOf("@@"); },
      tokenizer(source, tokens) {
        if (!source.startsWith("@@B:")) return undefined;
        const end = source.indexOf("@@", 4);
        if (end < 0) return undefined;
        const raw = source.slice(0, end + 2);
        return probeBlock.call(this, "probe_block_first", raw, tokens, "@@B:");
      },
    },
    {
      name: "probeBlockSecond",
      level: "block",
      start(source) { return source.indexOf("@@"); },
      tokenizer(source, tokens) {
        if (!source.startsWith("@@B:")) return undefined;
        const end = source.indexOf("@@", 4);
        if (end < 0) return undefined;
        const raw = source.slice(0, end + 2);
        return probeBlock.call(this, "probe_block_second", raw, tokens, "@@B:");
      },
    },
    {
      name: "probeBlockNegativeStart",
      level: "block",
      start() { return -1; },
      tokenizer() { return undefined; },
    },
    {
      name: "probeInlineContext",
      level: "inline",
      start(source) { return source.indexOf("[["); },
      tokenizer(source, tokens) {
        if (!source.startsWith("[[")) return undefined;
        const end = source.indexOf("]]", 2);
        if (end < 0) return undefined;
        const raw = source.slice(0, end + 2);
        const text = raw.slice(2, -2);
        return { type: "probe_inline", raw, text, seen: tokens.length, tokens: this.lexer.inlineTokens(text) };
      },
    },
  ] });
  return parser;
}

function normaliseResult(parser, source) {
  try {
    const tokenList = parser.lexer(source);
    const links = tokenList.links || {};
    return { tokens: JSON.parse(JSON.stringify(tokenList)), links: JSON.parse(JSON.stringify(links)) };
  } catch (error) {
    return { error: String(error?.message ?? error) };
  }
}

function writeFixture(name, entries, parser) {
  const rawCorpus = fs.readFileSync(CORPUS, "utf8");
  const corpusHash = crypto.createHash("sha256").update(rawCorpus).digest("hex");
  const output = [{
    kind: "header",
    markedVersion: packageInfo.version,
    integrity: packageInfo.integrity,
    configuration: name,
    corpusSha256: corpusHash,
    node: process.version,
    caseCount: entries.length,
  }];
  for (const entry of entries) output.push({ id: entry.id, source: entry.source, kind: entry.kind, result: normaliseResult(parser, entry.source) });
  fs.writeFileSync(path.join(OUT_DIR, `${name}.jsonl`), output.map(item => JSON.stringify(item)).join("\n") + "\n");
  return output.length - 1;
}

const { raw, entries } = readCorpus();
if (!raw.endsWith("\n")) die("corpus must be JSONL ending in newline");
fs.mkdirSync(OUT_DIR, { recursive: true });
const plain = new Marked();
const pi = await makePiParser();
const probe = makeProbeParser();
const counts = {
  plain: writeFixture("plain", entries, plain),
  pi: writeFixture("pi", entries, pi),
  probe: writeFixture("probe", entries, probe),
};
console.log(JSON.stringify({ marked: packageInfo, corpusCases: entries.length, corpusSha256: crypto.createHash("sha256").update(raw).digest("hex"), counts }, null, 2));
