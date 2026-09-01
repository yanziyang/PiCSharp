using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Structural Workers AI binding surface used by the gateway transport.</summary>
public interface ICloudflareAiGatewayBinding
{
    /// <summary>Gets a named gateway binding.</summary>
    ICloudflareAiGateway Gateway(string id);
}

/// <summary>Structural Workers AI gateway surface used by the gateway transport.</summary>
public interface ICloudflareAiGateway
{
    /// <summary>Runs one universal provider request through the binding.</summary>
    Task<HttpResponseMessage> RunAsync(
        CloudflareAiGatewayUniversalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One universal-endpoint request accepted by a Workers AI gateway binding.</summary>
public sealed record CloudflareAiGatewayUniversalRequest
{
    /// <summary>Provider path component such as <c>openai</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider endpoint path and query string.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Lowercase request headers after gateway-derived headers are removed.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Parsed JSON request body.</summary>
    public JsonNode? Query { get; init; }
}

/// <summary>
/// Translates gateway HTTPS requests into calls to the Workers AI universal gateway binding.
/// </summary>
public sealed class CloudflareGatewayBindingTransport
{
    /// <summary>Placeholder used when a binding-routed client needs an auth marker.</summary>
    public const string AuthSentinel = "cloudflare-gateway-binding";

    private static readonly HashSet<string> _strippedHeaders = new(StringComparer.Ordinal)
    {
        "content-length",
        "host",
        "cf-aig-authorization",
    };

    private readonly ICloudflareAiGatewayBinding _binding;
    private readonly string _gateway;
    private readonly Uri _baseUri;
    private readonly string _basePath;

    /// <summary>Creates a binding transport for one exact gateway URL prefix.</summary>
    public CloudflareGatewayBindingTransport(
        ICloudflareAiGatewayBinding binding,
        string baseUrl,
        string gateway)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(gateway);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) ||
            string.IsNullOrEmpty(parsedBase.Scheme) ||
            string.IsNullOrEmpty(parsedBase.Host))
        {
            throw new ArgumentException("The gateway base URL must be an absolute URL.", nameof(baseUrl));
        }

        _baseUri = parsedBase;
        _gateway = gateway;
        _basePath = parsedBase.AbsolutePath.Length > 0 && parsedBase.AbsolutePath[^1] == '/'
            ? parsedBase.AbsolutePath
            : parsedBase.AbsolutePath + "/";
    }

    /// <summary>
    /// Routes an HTTP request through the binding, rejecting requests the universal endpoint
    /// cannot express or requests outside the configured gateway prefix.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new InvalidOperationException("The gateway request requires a URI.");
        var method = request.Method.Method.ToUpperInvariant();

        if (!IsUnderConfiguredPrefix(uri))
        {
            throw new InvalidOperationException(
                $"CloudflareGatewayBinding: {method} {uri} is outside the configured gateway prefix " +
                $"({_baseUri.GetLeftPart(UriPartial.Authority)}{_basePath}); this transport only serves its gateway-bound client");
        }

        if (!string.Equals(method, HttpMethod.Post.Method, StringComparison.Ordinal))
        {
            throw CannotExpress(method, uri, "only POST is supported");
        }

        var rest = uri.AbsolutePath[_basePath.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            throw CannotExpress(method, uri, "missing provider/endpoint path");
        }

        var body = request.Content is null
            ? throw CannotExpress(method, uri, "missing body")
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? query;
        try
        {
            query = JsonNode.Parse(body);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw CannotExpress(method, uri, "non-JSON body");
        }

        var headers = CollectHeaders(request);
        var endpoint = rest[(slash + 1)..] + uri.Query;
        return await _binding.Gateway(_gateway).RunAsync(
                new CloudflareAiGatewayUniversalRequest
                {
                    Provider = rest[..slash],
                    Endpoint = endpoint,
                    Headers = headers,
                    Query = query,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsUnderConfiguredPrefix(Uri uri) =>
        string.Equals(uri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _baseUri.Port &&
        uri.AbsolutePath.StartsWith(_basePath, StringComparison.Ordinal);

    private static Dictionary<string, string> CollectHeaders(HttpRequestMessage request)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        AddHeaders(result, request.Headers);
        if (request.Content is not null)
        {
            AddHeaders(result, request.Content.Headers);
        }

        return result;
    }

    private static void AddHeaders(
        Dictionary<string, string> destination,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var pair in headers)
        {
            var name = pair.Key.ToLowerInvariant();
            if (!_strippedHeaders.Contains(name))
            {
                destination[name] = string.Join(", ", pair.Value);
            }
        }
    }

    private static InvalidOperationException CannotExpress(string method, Uri uri, string reason) =>
        new(
            $"CloudflareGatewayBinding: cannot express {method} {uri} as a universal gateway request " +
            $"({reason}); route it over HTTPS with gateway auth instead");
}
