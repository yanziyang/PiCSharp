using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Shared HTTP request execution for Pi provider adapters.</summary>
public sealed class ProviderHttpClient
{
    private static readonly HttpClient _sharedHttpClient = new();
    private readonly HttpClient _httpClient;

    /// <summary>Creates a transport using the supplied client or the process-shared client.</summary>
    public ProviderHttpClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? _sharedHttpClient;
    }

    /// <summary>
    /// Sends a provider JSON request, invokes the response callback before body consumption, and
    /// captures non-success response bodies in a provider metadata exception.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        Model model,
        HttpMethod method,
        Uri uri,
        JsonNode? payload,
        ProviderRequestOptions? options = null,
        IReadOnlyDictionary<string, string?>? defaultHeaders = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);

        var effectivePayload = payload;
        if (options?.OnPayload is not null)
        {
            var replacedPayload = await options.OnPayload(payload, model).ConfigureAwait(false);
            if (replacedPayload is not null)
            {
                effectivePayload = replacedPayload;
            }
        }

        using var operationCancellation = CreateOperationCancellation(options, cancellationToken);
        using var request = BuildRequest(model, method, uri, effectivePayload, options, defaultHeaders);
        var response = options?.Fetch is not null
            ? await options.Fetch(request, operationCancellation.Token).ConfigureAwait(false)
            : await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    operationCancellation.Token)
                .ConfigureAwait(false);

        if (options?.OnResponse is not null)
        {
            await options.OnResponse(
                    new ProviderResponse((int)response.StatusCode, HeaderUtilities.HeadersToRecord(response.Headers)),
                    model)
                .ConfigureAwait(false);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(operationCancellation.Token).ConfigureAwait(false);
        response.Dispose();
        throw new ProviderErrorMetadataException(
            $"{(int)response.StatusCode} status code (no body)",
            new ProviderErrorMetadata
            {
                Status = (int)response.StatusCode,
                ResponseBody = body,
            });
    }

    /// <summary>Builds a provider request after applying model and caller header precedence.</summary>
    public static HttpRequestMessage BuildRequest(
        Model model,
        HttpMethod method,
        Uri uri,
        JsonNode? payload,
        ProviderRequestOptions? options = null,
        IReadOnlyDictionary<string, string?>? defaultHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);

        var request = new HttpRequestMessage(method, uri);
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        MergeHeaders(headers, defaultHeaders);
        if (model.Headers is not null)
        {
            foreach (var pair in model.Headers)
            {
                headers[pair.Key] = pair.Value;
            }
        }
        if (payload is not null)
        {
            headers["Content-Type"] = "application/json";
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(options?.ApiKey) && !headers.ContainsKey("Authorization"))
        {
            headers["Authorization"] = $"Bearer {options.ApiKey}";
        }

        MergeHeaders(headers, options?.Headers);
        foreach (var pair in headers)
        {
            if (pair.Value is null)
            {
                request.Headers.Remove(pair.Key);
                request.Content?.Headers.Remove(pair.Key);
                continue;
            }

            if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content is not null)
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(pair.Value);
                }

                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        return request;
    }

    private static CancellationTokenSource CreateOperationCancellation(
        ProviderRequestOptions? options,
        CancellationToken cancellationToken)
    {
        var tokens = new[] { cancellationToken, options?.Signal ?? default }
            .Where(static token => token.CanBeCanceled)
            .ToArray();
        var source = tokens.Length switch
        {
            0 => new CancellationTokenSource(),
            1 => CancellationTokenSource.CreateLinkedTokenSource(tokens[0]),
            _ => CancellationTokenSource.CreateLinkedTokenSource(tokens),
        };
        if (options?.TimeoutMs is > 0)
        {
            source.CancelAfter(options.TimeoutMs.Value);
        }

        return source;
    }

    private static void MergeHeaders(
        Dictionary<string, string?> destination,
        IReadOnlyDictionary<string, string?>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

}
