// Exports this repo's Claude Code session history into ÁI/ClaudeSession.html,
// a standalone, curated, cumulative record of the project's AI-assisted work.
//
// Run from anywhere with: node tools/export-session.mjs
//
// Two filters apply:
//   1. Prompts and responses only. Tool calls, tool results and internal
//      reasoning are intermediate working detail and are excluded.
//   2. Substantive exchanges only. Routine housekeeping is dropped.
//
// The unit of judgement for (2) is the EXCHANGE, not the prompt. Several of the
// most productive turns across this project were a single word ("proceed")
// answered with a full engineering writeup; judging prompts alone would
// discard them.
//
// Committed into the repo 2026-09-13, after living only in a session scratchpad
// directory and being lost to a routine temp-cleanup during a two-week gap. It
// had to be rebuilt from the accumulated fixes rather than recovered - keep it
// here instead.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const OUT = path.join(REPO_ROOT, "ÁI", "ClaudeSession.html");

// Claude Code stores each project's session transcripts under a folder named by
// mangling the project's absolute path: drive colon and every path separator
// become "-". Derived here, rather than hard-coded, so this runs correctly for
// any account or drive the repo is checked out under - a script committed into
// shared history that only works on the machine that wrote it defeats the point
// of committing it.
const mangledProjectName = REPO_ROOT.replace(/\\/g, "/").replace(/[:/]/g, "-");
const PROJECT_DIR = path.join(os.homedir(), ".claude", "projects", mangledProjectName);
const FIRST_PROMPT_MARKER = "Evaluate feasibility of migrating";

function lastMessageTimestamp(file) {
  const lines = fs.readFileSync(file, "utf8").split("\n").filter(Boolean);
  let last = 0;
  for (const line of lines) {
    let rec;
    try { rec = JSON.parse(line); } catch { continue; }
    if (!rec.timestamp) continue;
    const ms = Date.parse(rec.timestamp);
    if (ms > last) last = ms;
  }
  return last;
}

const candidates = fs.readdirSync(PROJECT_DIR)
  .filter(f => f.endsWith(".jsonl"))
  .map(f => PROJECT_DIR + "/" + f)
  .filter(f => fs.readFileSync(f, "utf8").includes(FIRST_PROMPT_MARKER))
  .map(f => ({ file: f, lastTs: lastMessageTimestamp(f) }))
  .sort((a, b) => b.lastTs - a.lastTs);

const SRC = candidates[0]?.file;
if (!SRC) throw new Error("no transcript containing the original brief was found");

if (candidates.length > 1) {
  console.log("candidates, by last real content (not file mtime):");
  for (const c of candidates) {
    console.log(`  ${c.file.split(/[\\/]/).pop()}  ${new Date(c.lastTs).toISOString()}`);
  }
}

const esc = s => String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

// ---------------------------------------------------------------- markdown
function md(src) {
  if (!src) return "";
  const blocks = [];
  let s = String(src).replace(/\r\n/g, "\n");

  s = s.replace(/```([a-zA-Z0-9+_-]*)\n([\s\S]*?)```/g, (_m, lang, code) => {
    blocks.push(`<pre class="code"${lang ? ` data-lang="${esc(lang)}"` : ""}><code>${esc(code.replace(/\n$/, ""))}</code></pre>`);
    return `\u0000BLOCK${blocks.length - 1}\u0000`;
  });

  s = esc(s);
  const lines = s.split("\n");
  const out = [];
  let i = 0;

  const inline = t => t
    .replace(/`([^`]+)`/g, (_m, c) => `<code>${c}</code>`)
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|[\s(])\*([^*\n]+)\*(?=[\s).,;:!?]|$)/g, "$1<em>$2</em>")
    // Only real URLs become links. Repo-relative paths render as code: several
    // were renamed later in the session and would 404 from an export.
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_m, txt, href) =>
      /^(https?:|#)/.test(href)
        ? `<a href="${href}"${/^https?:/.test(href) ? ' target="_blank" rel="noopener"' : ""}>${txt}</a>`
        : `<code>${href}</code>`);

  while (i < lines.length) {
    const line = lines[i];

    if (/^\u0000BLOCK\d+\u0000$/.test(line.trim())) { out.push(line.trim()); i++; continue; }
    if (!line.trim()) { i++; continue; }

    let m;
    if ((m = line.match(/^(#{1,6})\s+(.*)$/))) {
      const lvl = Math.min(m[1].length + 2, 6);
      out.push(`<h${lvl}>${inline(m[2])}</h${lvl}>`); i++; continue;
    }
    if (/^(---+|\*\*\*+)\s*$/.test(line)) { out.push("<hr>"); i++; continue; }

    if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1])) {
      const cells = r => r.trim().replace(/^\||\|$/g, "").split("|").map(c => c.trim());
      const head = cells(line);
      i += 2;
      const rows = [];
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) { rows.push(cells(lines[i])); i++; }
      out.push(`<div class="tw"><table><thead><tr>${head.map(h => `<th>${inline(h)}</th>`).join("")}</tr></thead><tbody>${
        rows.map(r => `<tr>${r.map(c => `<td>${inline(c)}</td>`).join("")}</tr>`).join("")}</tbody></table></div>`);
      continue;
    }

    if (/^\s*([-*+]|\d+\.)\s+/.test(line)) {
      const ordered = /^\s*\d+\./.test(line);
      const items = [];
      while (i < lines.length && /^\s*([-*+]|\d+\.)\s+/.test(lines[i])) {
        let txt = lines[i].replace(/^\s*([-*+]|\d+\.)\s+/, "");
        i++;
        while (i < lines.length && lines[i].trim() && !/^\s*([-*+]|\d+\.)\s+/.test(lines[i]) && !/^#{1,6}\s/.test(lines[i]) && !/^\s*\|/.test(lines[i])) {
          txt += " " + lines[i].trim(); i++;
        }
        items.push(`<li>${inline(txt)}</li>`);
      }
      out.push(`<${ordered ? "ol" : "ul"}>${items.join("")}</${ordered ? "ol" : "ul"}>`);
      continue;
    }

    let p = line; i++;
    while (i < lines.length && lines[i].trim() && !/^(#{1,6}\s|\s*([-*+]|\d+\.)\s|\s*\||---+|\u0000BLOCK)/.test(lines[i])) {
      p += " " + lines[i].trim(); i++;
    }
    out.push(`<p>${inline(p)}</p>`);
  }

  return out.join("\n").replace(/\u0000BLOCK(\d+)\u0000/g, (_m, n) => blocks[+n]);
}

// ---------------------------------------------------------------- parse
const recs = fs.readFileSync(SRC, "utf8").split("\n").filter(Boolean)
  .map(l => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);

const conv = recs.filter(r => (r.type === "user" || r.type === "assistant") && r.message && !r.isSidechain);

const turns = [];
for (const r of conv) {
  const c = r.message.content;

  if (r.type === "user") {
    let text = null;
    if (typeof c === "string") text = c;
    else if (Array.isArray(c)) {
      const t = c.filter(b => b.type === "text").map(b => b.text).join("\n").trim();
      if (t) text = t;
    }
    if (!text) continue;
    if (/^<(command-name|local-command|system-reminder)/.test(text.trim())) continue;
    turns.push({ role: "user", text, ts: r.timestamp });
    continue;
  }

  if (!Array.isArray(c)) continue;
  for (const b of c) {
    if (b.type === "text" && b.text.trim()) turns.push({ role: "assistant", text: b.text, ts: r.timestamp });
  }
}

// ---------------------------------------------------------------- select
// Three filters, in order.
//
// A. Bare continuations ("proceed", "continue") are merged into the preceding
//    exchange rather than starting a new one. They carry no instruction, but the
//    work they trigger is often the most substantial in the session, so the
//    replies are kept while the empty prompt bubble is not. The same treatment
//    applies to harness-injected messages: the context-limit continuation
//    summary and usage-limit resumption look like user prompts but are not user
//    intent, and folding them the same way keeps the work they triggered from
//    being filed under text the user never wrote.
//
// B. Narration is dropped. Short, unstructured assistant blocks that sit between
//    tool calls ("Building the scaffold now.") are process, not content. The
//    final block of an exchange is always kept, however short, since that is the
//    answer.
//
// C. Whole exchanges of routine housekeeping are dropped, judged on the reply
//    rather than the prompt.

const CONTINUATION =
  /^(proceed|continue|go ahead|carry on|go on|next|ok|okay|yes|please proceed)[.!]?$/i;

const HARNESS =
  /^(this session is being continued from a previous conversation|i hit my usage limit|caveat: the messages below)/i;

const exchanges = [];
for (const t of turns) {
  if (t.role === "user") {
    const text = t.text.trim();
    if (CONTINUATION.test(text) && exchanges.length) continue;  // A
    if (HARNESS.test(text)) {
      // Same harness text, two shapes. Mid-transcript it folds into the
      // preceding exchange like a bare continuation. At position zero there is
      // no preceding exchange to fold into, and dropping it outright would also
      // drop the real reply that follows it - so it starts an exchange, but with
      // a short synthetic prompt standing in for the raw summary dump, which is
      // usually tens of KB of text no one asked to have rendered as a bubble.
      if (exchanges.length) continue;
      exchanges.push({ prompt: { ...t, text: "[Continued from a prior conversation, summarized by the harness]" }, replies: [] });
      continue;
    }
    exchanges.push({ prompt: t, replies: [] });
  } else if (exchanges.length) {
    exchanges[exchanges.length - 1].replies.push(t);
  }
}

// B: narration filter
const NARRATION_MIN = 240;
const hasStructure = t =>
  /^#{2,4}\s/m.test(t) || /\n\|.*\|/.test(t) || /^\s*[-*]\s/m.test(t) || t.includes("```");

let narrationDropped = 0;
for (const x of exchanges) {
  const last = x.replies.length - 1;
  const before = x.replies.length;
  x.replies = x.replies.filter((r, i) =>
    i === last || hasStructure(r.text) || r.text.trim().length >= NARRATION_MIN);
  narrationDropped += before - x.replies.length;
}

// C: routine repository housekeeping, and requests about this transcript itself.
const HOUSEKEEPING =
  /^(commit|dirrect commit|direct commit|push|add to claudesession|update claudesession|there (are|is) two duplicated|the sha|i have clone|move ['"]|rename)/i;

const replyText = x => x.replies.map(r => r.text).join("\n");

function isSubstantive(x, index) {
  if (index === 0) return true;                          // the original brief
  const body = replyText(x);
  const structured = /^#{2,4}\s/m.test(body) || /\n\|.*\|/.test(body);
  if (HOUSEKEEPING.test(x.prompt.text.trim()) && !structured && body.length < 2500) return false;
  return structured || body.length >= 900;
}

const allKept = exchanges.filter(isSubstantive);

// ---------------------------------------------------------------- splice
// ClaudeSession.html is a cumulative project record spanning many sessions, not
// a per-session snapshot. The live source transcript, however, only goes back to
// this session's own harness-continuation boundary - the multi-week history
// before that lives solely in the ALREADY-PUBLISHED file, built up over many
// prior exports. Regenerating from this transcript alone would silently discard
// every exchange from before that boundary, which is a much bigger loss than it
// looks: the boundary sits mid-project, not at the start.
//
// So: if a previous export exists, treat it as a validated prefix and splice on
// only the exchanges strictly newer than its last recorded timestamp, rather
// than replacing it outright. The splice point is the prompt <time> of the last
// <section class="turn user"> in the existing file - content-derived, exact, and
// immune to any drift in how many exchanges either side thinks it has.
let prefix = null;
if (fs.existsSync(OUT)) {
  const oldHtml = fs.readFileSync(OUT, "utf8");
  const userSections = [...oldHtml.matchAll(/<section class="turn user" id="p(\d+)">\s*<div class="who"><span class="badge u">Exchange \d+<\/span><time>([^<]+)<\/time>/g)];
  if (userSections.length) {
    const lastTs = Date.parse(userSections.at(-1)[2].replace(" ", "T") + "Z");
    const navMatch = oldHtml.match(/<nav><div class="t">Exchanges<\/div>\n([\s\S]*?)\n<\/nav>/);
    const mainMatch = oldHtml.match(/<main>\n([\s\S]*?)\n<\/main>/);
    const omittedMatch = oldHtml.match(/A further ([\d,]+) exchange/);
    const recordsMatch = oldHtml.match(/from ([\d,]+) transcript records/);
    const replyCount = (oldHtml.match(/<section class="turn asst">/g) || []).length;
    if (navMatch && mainMatch && lastTs) {
      prefix = {
        count: userSections.length,
        replies: replyCount,
        omitted: omittedMatch ? parseInt(omittedMatch[1].replace(/,/g, ""), 10) : 0,
        records: recordsMatch ? parseInt(recordsMatch[1].replace(/,/g, ""), 10) : 0,
        nav: navMatch[1],
        main: mainMatch[1],
        lastTs,
      };
    }
  }
}

// The published file's <time> is truncated to the second (no milliseconds), so
// a raw timestamp landing in the SAME second as the cutoff (e.g. ...11.080Z vs a
// cutoff parsed as ...11.000Z) would compare as later and re-include the exchange
// that produced the cutoff. Compare at second granularity to match what was
// actually rendered.
const afterCutoff = ts => !prefix || Math.floor(Date.parse(ts) / 1000) > Math.floor(prefix.lastTs / 1000);
const kept = prefix ? allKept.filter(x => afterCutoff(x.prompt.ts)) : allKept;
const consideredAfterSplice = prefix ? exchanges.filter(x => afterCutoff(x.prompt.ts)) : exchanges;
const omitted = consideredAfterSplice.length - kept.length;

if (prefix) {
  console.log(`splicing onto existing export: ${prefix.count} prior exchanges kept as-is, appending from after ${new Date(prefix.lastTs).toISOString()}`);
}

const summarise = x => {
  const first = x.prompt.text.trim().split("\n").find(l => l.trim()) || "";
  // A one-word answer to a question ("1", "yes, all") says nothing on its own in
  // the contents list. Borrow the reply's first heading so the row is navigable.
  if (first.length <= 12) {
    const body = replyText(x);
    const h = (body.match(/^#{2,4}\s+(.+)$/m) || [])[1]
           || (body.match(/^\*\*(.+?)\*\*/m) || [])[1];
    if (h) {
      const t = h.replace(/[.:]$/, "");
      return `${first} \u2014 ${t.length > 58 ? t.slice(0, 58) + "\u2026" : t}`;
    }
  }
  return first.length > 72 ? first.slice(0, 72) + "\u2026" : first;
};

// ---------------------------------------------------------------- render
let body = "";
let uIdx = prefix ? prefix.count : 0;
for (const x of kept) {
  uIdx++;
  body += `<section class="turn user" id="p${uIdx}">
  <div class="who"><span class="badge u">Exchange ${uIdx}</span><time>${esc((x.prompt.ts || "").replace("T", " ").slice(0, 19))}</time></div>
  <div class="bubble">${md(x.prompt.text)}</div>
</section>\n`;
  for (const r of x.replies) {
    body += `<section class="turn asst">
  <div class="who"><span class="badge a">Claude</span><time>${esc((r.ts || "").replace("T", " ").slice(0, 19))}</time></div>
  <div class="bubble">${md(r.text)}</div>
</section>\n`;
  }
}

const baseIdx = prefix ? prefix.count : 0;
const stats = {
  prompts: baseIdx + kept.length,
  replies: (prefix ? prefix.replies : 0) + kept.reduce((n, x) => n + x.replies.length, 0),
  omitted: (prefix ? prefix.omitted : 0) + omitted,
};

const newToc = kept.map((x, n) =>
  `<a href="#p${baseIdx + n + 1}"><span class="n">${baseIdx + n + 1}</span>${esc(summarise(x))}</a>`).join("\n");
const toc = prefix ? prefix.nav + "\n" + newToc : newToc;
body = prefix ? prefix.main + "\n" + body : body;

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Claude Code Session \u2014 PiCSharp \u2014 Aug\u2013Sep 2026</title>
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  body { margin: 0; font-family: -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; background: #f7f7f8; color: #1b1b1f; }
  .wrap { max-width: 1180px; margin: 0 auto; padding-left: 32px; padding-right: 32px; }
  .mast { padding: 28px 0 22px; background: #14141a; color: #fff; }
  .eyebrow { text-transform: uppercase; letter-spacing: .08em; font-size: 12px; color: #9a9aa8; margin: 0 0 6px; }
  .mast h1 { margin: 0 0 14px; font-size: 22px; line-height: 1.3; }
  .stat-bar { display: flex; gap: 22px; margin: 0 0 12px; flex-wrap: wrap; }
  .stat-bar div { font-size: 13px; color: #c8c8d4; }
  .stat-bar b { display: block; font-size: 20px; color: #fff; }
  .shell { display: grid; grid-template-columns: 300px 1fr; align-items: start; max-width: 1180px; margin: 0 auto; }
  nav { position: sticky; top: 0; max-height: 100vh; overflow-y: auto; padding: 18px 14px; border-right: 1px solid #e2e2e8; background: #fff; }
  nav .t { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .06em; color: #9a9aa8; padding: 4px 8px 10px; }
  nav a { display: flex; gap: 8px; padding: 6px 8px; border-radius: 6px; font-size: 12.5px; color: #33333d; text-decoration: none; line-height: 1.35; }
  nav a:hover { background: #f0f0f4; }
  nav .n { flex-shrink: 0; font-weight: 600; color: #8a8a98; min-width: 18px; }
  main { min-width: 0; padding: 28px 40px 80px; max-width: 900px; }
  section.turn { margin: 0 0 20px; }
  .who { display: flex; align-items: baseline; gap: 10px; margin: 0 0 6px; }
  .badge { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .04em; padding: 2px 8px; border-radius: 999px; }
  .badge.u { background: #dbe6ff; color: #1a4fc4; }
  .badge.a { background: #eee; color: #555; }
  time { font-size: 11px; color: #9a9aa8; }
  .bubble { border: 1px solid #e2e2e8; border-radius: 10px; padding: 14px 18px; background: #fff; }
  .turn.user .bubble { background: #f5f8ff; border-color: #d8e3fb; }
  .bubble p { margin: 0 0 10px; line-height: 1.55; }
  .bubble p:last-child { margin-bottom: 0; }
  .bubble h4, .bubble h5, .bubble h6 { margin: 16px 0 6px; }
  .bubble ul, .bubble ol { margin: 8px 0; padding-left: 22px; }
  .bubble li { margin: 3px 0; line-height: 1.5; }
  .bubble code { background: #eef0f4; padding: 1px 5px; border-radius: 4px; font-size: .92em; }
  pre.code { background: #14141a; color: #e6e6ef; padding: 12px 14px; border-radius: 8px; overflow-x: auto; font-size: 12.5px; line-height: 1.5; }
  pre.code code { background: none; padding: 0; color: inherit; }
  .tw { overflow-x: auto; margin: 10px 0; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  th, td { border: 1px solid #e2e2e8; padding: 6px 10px; text-align: left; vertical-align: top; }
  th { background: #f5f5f8; }
  a { color: #1a4fc4; }
  hr { border: none; border-top: 1px solid #e2e2e8; margin: 18px 0; }
  footer { border-top: 1px solid #e2e2e8; padding: 18px 0 40px; font-size: 12.5px; color: #6b6b78; line-height: 1.55; }
  @media print { .mast { background:#312e81!important; -webkit-print-color-adjust:exact; print-color-adjust:exact } nav { display:none } .shell { grid-template-columns:1fr } .turn { break-inside:avoid } }
</style>
</head>
<body>
<header class="mast"><div class="wrap">
<p class="eyebrow">Claude Code \u00b7 Session Transcript</p>
<h1>PiCSharp \u2014 Pi to .NET 10 feasibility, port scaffold and review</h1>
<div class="stat-bar">
  <div>Exchanges<b>${stats.prompts}</b></div>
  <div>Responses<b>${stats.replies}</b></div>
</div>
</div></header>
<div class="shell">
<nav><div class="t">Exchanges</div>
${toc}
</nav>
<main>
${body}
</main>
</div>
<footer><div class="wrap">
<p style="margin:0 0 6px"><b>Session transcript exported from Claude Code.</b> Prompts and responses only \u2014 tool calls, tool results and internal reasoning are excluded as intermediate working detail.</p>
<p style="margin:0 0 6px">This is a curated record, not a complete one. Bare continuations such as \u201cproceed\u201d are folded into the preceding exchange, keeping the work they triggered without the empty prompt. Short progress narration between tool calls is dropped. A further ${stats.omitted} exchange${stats.omitted === 1 ? " was" : "s were"} omitted as routine housekeeping.</p>
<p style="margin:0">Generated ${new Date().toISOString().slice(0, 10)} \u00b7 ${stats.prompts + stats.replies} entries from ${((prefix ? prefix.records : 0) + recs.length).toLocaleString()} transcript records.</p>
</div></footer>
</body>
</html>`;

fs.writeFileSync(OUT, html, "utf8");
console.log("source:", SRC.split(/[\\/]/).pop());
console.log("wrote", OUT);
console.log("size:", Math.round(html.length / 1024), "KB");
console.log(`total exchanges: ${stats.prompts} (${kept.length} new) | omitted: ${stats.omitted} | responses: ${stats.replies} | narration blocks dropped: ${narrationDropped}`);
