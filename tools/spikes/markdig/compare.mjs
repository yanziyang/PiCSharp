import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const [inputsPath = path.join(here, "inputs.json"), markedPath = path.join(here, "marked.json"), markdigPath = path.join(here, "markdig.json"), outputPath = path.join(here, "divergences.json")] = process.argv.slice(2);
const inputs = JSON.parse(fs.readFileSync(inputsPath, "utf8"));
const marked = JSON.parse(fs.readFileSync(markedPath, "utf8"));
const markdig = JSON.parse(fs.readFileSync(markdigPath, "utf8"));

const impact = new Map([
  ["test-06-01", ["yes", "Blank lines between list blocks and preserveOrderedListMarkers require source-gap and marker recovery."]],
  ["test-10-01", ["yes", "Marked emits space nodes around list/code/list boundaries; the list renderer has no automatic trailing spacing."]],
  ["test-33-01", ["yes", "The list-to-table blank line is observable because list rendering does not add one before the table."]],
  ["test-34-01", ["yes", "Production markdown.ts tokenizes the complete inline math and renders Unicode math; the core Markdig comparison has no math node."]],
  ["test-35-01", ["yes", "Production markdown.ts tokenizes the display-dollar block and renders it; the core Markdig comparison leaves it as paragraph text."]],
  ["test-36-01", ["yes", "Production markdown.ts tokenizes the display-bracket block and renders a multi-line display; core Markdig leaves escaped text and soft breaks."]],
  ["test-37-01", ["yes", "Production markdown.ts tokenizes the matrix display; core Markdig leaves the source split into literals and line-break nodes."]],
  ["test-38-01", ["yes", "Production markdown.ts tokenizes the display operator; core Markdig leaves the source split into literals and line-break nodes."]],
  ["test-39-01", ["yes", "This combines the list-to-table spacing gap with inline math in the list and table cells."]],
  ["test-42-01", ["yes", "The production pending \\( form preserves its raw backslash; a core Markdig escape resolves it to a bare parenthesis unless source text is recovered."]],
  ["test-42-02", ["yes", "The production pending \\[ block preserves \\[; a core Markdig escape resolves it to [ unless the block extension owns the delimiter."]],
  ["test-47-01", ["yes", "With preserveBackslashEscapes=true, markdown.ts emits escape.raw; Markdig must recover the source span instead of resolved literal content."]],
  ["test-45-02", ["yes", "After setText closes the dollar delimiter, production markdown.ts renders Unicode math; core Markdig still has only literal inline nodes."]],
  ["test-60-01", ["yes", "The renderer must retain Markdig's soft LineBreakInline as a newline so both quote lines receive a border/style."]],
  ["test-61-01", ["yes", "The renderer must retain Markdig's soft LineBreakInline as a newline so both quote lines receive a border/style."]],
  ["test-72-01", ["conditional", "The upstream assertion runs with hyperlinks disabled and passes as plain text; with hyperlinks enabled Marked emits OSC 8 mailto while core Markdig emits no link."]],
  ["probe-autolink", ["yes-if-unmapped", "AutolinkInline has to be adapted to the link path (URL plus label); otherwise the renderer's token switch does not handle it."]],
  ["probe-blockquote", ["yes", "Same soft-break rule as tests 60/61; retained probe confirms it independently."]],
]);

const cases = inputs.cases.map((input) => {
  const markedCase = marked.cases[input.id];
  const markdigCase = markdig.cases[input.id];
  if (!markedCase || !markdigCase) throw new Error(`Missing parser output for ${input.id}`);
  const [rendererImpact, consequence] = impact.get(input.id) ?? ["no", "Raw wrapper/property differences are consumed by the adapter mapping without changing renderer output."];
  return {
    id: input.id,
    kind: input.kind,
    testIndex: input.testIndex,
    test: input.test,
    ...(input.operation ? { operation: input.operation } : {}),
    source: input.source,
    ...(input.options ? { options: input.options } : {}),
    marked: markedCase.types,
    markdig: markdigCase.types,
    rendererImpact,
    consequence,
  };
});

const output = {
  metadata: {
    upstreamTestCount: inputs.metadata.upstreamTestCount,
    upstreamInputCount: inputs.metadata.upstreamInputCount,
    retainedProbeCount: inputs.metadata.retainedProbeCount,
    caseCount: cases.length,
    note: "Raw streams use different vocabularies by design. rendererImpact is the post-adapter audit against markdown.ts, not a claim that the raw arrays should match.",
  },
  cases,
};
fs.writeFileSync(outputPath, JSON.stringify(output, null, 1));
const counts = Object.groupBy(cases, (entry) => entry.rendererImpact);
console.log(`wrote exhaustive ledger for ${cases.length} cases: ${Object.entries(counts).map(([name, values]) => `${name}=${values.length}`).join(", ")}`);
