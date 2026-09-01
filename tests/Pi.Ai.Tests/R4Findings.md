# R4 port findings and resolutions

The twelve offline upstream files are represented by 134 named xUnit cases. The initial test-only
commit exposed the following nine mismatches. They were resolved in the follow-up implementation
commit without skipping or weakening any case.

| Case | Upstream expectation | Actual C# behavior |
| --- | --- | --- |
| `derives provider and endpoint from gateway passthrough URLs` | Derive the provider and forward the endpoint path, beginning with `/anthropic/v1/messages`. | Resolved by `CloudflareGatewayBindingTransport`, which splits the normalized gateway path into provider and endpoint. |
| `accepts Request inputs and forwards their headers and body` | Forward the request as `/openai/chat/completions` while preserving the request data. | Resolved by the binding transport's request-to-universal-endpoint translation. |
| `strips gateway auth and derived headers, forwards the rest` | Remove gateway-derived/auth headers and successfully forward the remaining headers. | Resolved null-header removal now routes content headers through `HttpContent.Headers`. |
| `keeps SDK placeholder auth out of entries when paired with null auth headers` | Null auth entries remove the SDK placeholder without throwing. | Resolved by safe request/content header classification during null overrides. |
| `lets an explicit \`signal: null\` in init clear a Request input's signal, per the fetch spec` | An explicit null signal clears the input signal (`CanBeCanceled == false`). | Resolved by propagating `CancellationToken.None` when neither caller nor options supplies cancellation. |
| `returns the bare message for a non-Error value` | Preserve the upstream JSON-object non-Error fixture and its JSON-formatted message. | The C# port now uses the same upstream fixture rather than a string-only translation. |
| `uses pi's User-Agent by default` | The default value starts with `pi (`. | Resolved by capturing structured user-agent tokens with wire-equivalent spacing. |
| `completes after response.completed even when the SSE body stays open` | Emit the completed assistant event after `response.completed`, before body EOF. | Resolved by terminating the Responses SSE loop at the terminal event and ignoring `[DONE]`. |
| `streams SSE responses into AssistantMessageEventStream` | Send the Codex request with the expected Pi user-agent and stream the SSE response. | Resolved by the wire-equivalent user-agent capture; the stream assertions now execute and pass. |

The source changes are in a separate follow-up commit from the original test-only port, as required by
the packet's finding workflow.

Other upstream surfaces without a full C# adapter remain covered at their nearest available seams:
Azure OpenAI endpoint/SDK resolution, Google Vertex SDK/ADC resolution, and Codex WebSocket/zstd
transport. Environment-dependent tests inject dictionaries or handlers; they do not mutate process
globals. The Cloudflare gateway binding seam is now implemented and exercised directly.

The `.test-parity` floor was not edited because it is outside the original packet's target paths.
Full-suite verification uses the repository-required non-E2E filter; credential-dependent E2E tests
are not run.
