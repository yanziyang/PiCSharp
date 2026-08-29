using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Ai;

/// <summary>Resolves HTTP and HTTPS proxy URLs using Pi's environment rules.</summary>
public static class HttpProxyUtilities
{
    private static readonly IReadOnlyDictionary<string, int> _defaultProxyPorts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ftp"] = 21,
            ["gopher"] = 70,
            ["http"] = 80,
            ["https"] = 443,
            ["ws"] = 80,
            ["wss"] = 443,
        };

    /// <summary>Error text used when a SOCKS or PAC proxy is requested.</summary>
    public const string UnsupportedProxyProtocolMessage =
        "Unsupported proxy protocol. SOCKS and PAC proxy URLs are not supported; use an HTTP or HTTPS proxy URL.";

    /// <summary>Resolves the configured proxy for an absolute target URI.</summary>
    public static Uri? ResolveHttpProxyUrlForTarget(
        Uri targetUrl,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(targetUrl);
        var proxy = GetProxyForUrl(targetUrl, environment);
        if (string.IsNullOrEmpty(proxy))
        {
            return null;
        }

        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUrl))
        {
            throw new InvalidOperationException(
                $"Invalid proxy URL {JsonValue.Create(proxy)!.ToJsonString()}: URI is not absolute");
        }

        if (!string.Equals(proxyUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(proxyUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{UnsupportedProxyProtocolMessage} Got {proxyUrl.Scheme}:");
        }

        return proxyUrl;
    }

    /// <summary>Resolves the configured proxy for a target URL string.</summary>
    public static Uri? ResolveHttpProxyUrlForTarget(
        string targetUrl,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(targetUrl);
        return Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsed)
            ? ResolveHttpProxyUrlForTarget(parsed, environment)
            : null;
    }

    private static string GetProxyForUrl(Uri targetUrl, IReadOnlyDictionary<string, string>? environment)
    {
        if (!targetUrl.IsAbsoluteUri || string.IsNullOrEmpty(targetUrl.Host))
        {
            return string.Empty;
        }

        var protocol = targetUrl.Scheme;
        var hostname = targetUrl.Host;
        var port = targetUrl.IsDefaultPort
            ? _defaultProxyPorts.GetValueOrDefault(protocol)
            : targetUrl.Port;
        if (!ShouldProxyHostname(hostname, port, environment))
        {
            return string.Empty;
        }

        var proxy = GetProxyEnv($"{protocol}_proxy", environment);
        if (string.IsNullOrEmpty(proxy))
        {
            proxy = GetProxyEnv("all_proxy", environment);
        }

        if (!string.IsNullOrEmpty(proxy) && !proxy.Contains("://", StringComparison.Ordinal))
        {
            proxy = $"{protocol}://{proxy}";
        }

        return proxy;
    }

    private static string GetProxyEnv(string key, IReadOnlyDictionary<string, string>? environment)
    {
        var lowercaseKey = key.ToLowerInvariant();
        var uppercaseKey = key.ToUpperInvariant();
        return GetScopedEnvironmentValue(environment, lowercaseKey) ??
               GetScopedEnvironmentValue(environment, uppercaseKey) ??
               ProviderEnvironmentUtilities.GetProviderEnvValue(lowercaseKey) ??
               ProviderEnvironmentUtilities.GetProviderEnvValue(uppercaseKey) ??
               string.Empty;
    }

    private static string? GetScopedEnvironmentValue(
        IReadOnlyDictionary<string, string>? environment,
        string key) =>
        environment is not null && environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    private static bool ShouldProxyHostname(
        string hostname,
        int port,
        IReadOnlyDictionary<string, string>? environment)
    {
        var noProxy = GetProxyEnv("no_proxy", environment).ToLowerInvariant();
        if (string.IsNullOrEmpty(noProxy))
        {
            return true;
        }

        if (noProxy == "*")
        {
            return false;
        }

        foreach (var token in Regex.Split(noProxy, "[,\\s]"))
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var match = Regex.Match(token, "^(.+):(\\d+)$");
            var proxyHostname = match.Success ? match.Groups[1].Value : token;
            var proxyPort = match.Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (proxyPort != 0 && proxyPort != port)
            {
                continue;
            }

            if (proxyHostname.Length == 0 || (proxyHostname[0] != '.' && proxyHostname[0] != '*'))
            {
                if (string.Equals(hostname, proxyHostname, StringComparison.Ordinal))
                {
                    return false;
                }

                continue;
            }

            if (proxyHostname.StartsWith('*'))
            {
                proxyHostname = proxyHostname[1..];
            }

            if (hostname.EndsWith(proxyHostname, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
