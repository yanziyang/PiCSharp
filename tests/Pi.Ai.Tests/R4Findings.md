# R4 port findings

The twelve offline upstream files are represented by 134 named xUnit cases. The following cases remain red because the R4 packet freezes `src/` and the corresponding C# adapter behavior is not yet present. No test was skipped or weakened.

| Case | Upstream expectation | Actual C# behavior |
| --- | --- | --- |
| `derives provider and endpoint from gateway passthrough URLs` | Derive the provider and forward the endpoint path, beginning with `/anthropic/v1/messages`. | The generic client sends `/v1/account-id/my-gateway/anthropic/v1/messages`. |
| `accepts Request inputs and forwards their headers and body` | Forward the request as `/openai/chat/completions` while preserving the request data. | The generic client sends `/v1/account-id/my-gateway/openai/chat/completions`. |
| `strips gateway auth and derived headers, forwards the rest` | Remove gateway-derived/auth headers and successfully forward the remaining headers. | Frozen `ProviderHttpClient.BuildRequest` throws `InvalidOperationException: Misused header name, 'content-length'`. |
| `keeps SDK placeholder auth out of entries when paired with null auth headers` | Null auth entries remove the SDK placeholder without throwing. | Frozen `ProviderHttpClient.BuildRequest` throws `InvalidOperationException: Misused header name, 'Authorization'`. |
| `lets an explicit \`signal: null\` in init clear a Request input's signal, per the fetch spec` | An explicit null signal clears the input signal (`CanBeCanceled == false`). | `ProviderRequestOptions.Signal` remains cancellable (`CanBeCanceled == true`). |
| `returns the bare message for a non-Error value` | Format `plain message` without JSON string quotes. | `ErrorBodyUtilities` returns `"plain message"`. |
| `uses pi's User-Agent by default` | The default value starts with `pi (`. | The captured value is `pi, (win32 Microsoft Windows NT 10.0.26200.0; x64)`. |
| `completes after response.completed even when the SSE body stays open` | Emit the completed assistant event after `response.completed`, before body EOF. | The frozen OpenAI Responses parser produces an empty event collection for the fixture. |
| `streams SSE responses into AssistantMessageEventStream` | Send the Codex request with the expected Pi user-agent and stream the SSE response. | The request user-agent is `pi, (win32 Microsoft Windows NT 10.0.26200.0; x64)`, so the expectation fails before the stream assertions. |

These source fixes require a separate implementation change and commit. They were not made here because the packet explicitly freezes `src/` and limits this task to `tests/Pi.Ai.Tests/**`.

Other upstream surfaces without a C# adapter in this frozen scope are covered at their nearest available seams: Cloudflare gateway binding, Azure OpenAI endpoint/SDK resolution, Google Vertex SDK/ADC resolution, and Codex WebSocket/zstd transport. Environment-dependent tests inject dictionaries or handlers; they do not mutate process globals. The ambient fallback behavior in frozen source remains unchanged.

The `.test-parity` floor was not edited because it is outside the packet's target paths. Full-suite verification used the repository-required non-E2E filter; credential-dependent E2E tests were not run.
