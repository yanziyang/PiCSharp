# R5 findings

The seven listed upstream files contain 42 transport-independent cases: 31 client cases and 11
server cases. All 42 are represented by xUnit facts with the upstream display names. The existing
overlapping client and server facts were renamed to their upstream cases and retained as the single
authoritative test for that behavior.

No listed case required the Unix transport or `TestServerService`. Client cases use an in-memory
byte transport and server listener cases use an in-memory `IPiServerListener`; the R5b harness was
not introduced.

The following source-language values require explicit C# treatment:

- JavaScript `undefined`, sparse-array holes, and object cycles do not have direct
  `System.Text.Json.Nodes.JsonNode` representations. The tests cover the corresponding C# null,
  dense-array, and sanitized-marker behavior. These are translation findings, not skipped tests.
- The upstream frame-limit case also probes a value above the unsigned 32-bit range. C# exposes
  `PiClientOptions.MaxFrameLength` as `uint`, so that invalid value is rejected by the type system;
  the port tests the runtime lower-bound rejection and the representable upper bound.
- The upstream invalid-timestamp case uses `NaN`; C# timestamps are `long`, so the port uses the
  representable invalid value `-1`.

`src/` remains unchanged for the initial R5 port. If the faithful port exposes a runtime
divergence, it will be recorded with the upstream expectation and C# result before any source fix
is made in a separate commit.
