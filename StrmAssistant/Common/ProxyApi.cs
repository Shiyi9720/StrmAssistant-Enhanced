using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Provides HTTP proxy support for the plugin's outgoing requests.
    /// Emby's own IHttpClient does not expose per-request proxy settings, so proxied requests
    /// are executed with a dedicated HttpClientHandler.Proxy instance instead. The client behind
    /// that handler is shared per proxy configuration so connections are reused.
    /// </summary>
    public static class ProxyApi
    {
        private static readonly char[] WhitelistSeparators = { ',', ';', '\r', '\n', ' ', '\t' };

        // HttpClient keeps its own default of 100 s when the caller does not ask for a timeout.
        private const int DefaultTimeoutMs = 100000;
        private static readonly TimeSpan RetireGracePeriod = TimeSpan.FromSeconds(15);

        private static readonly ConcurrentDictionary<string, HttpClient> ClientPool =
            new ConcurrentDictionary<string, HttpClient>(StringComparer.Ordinal);

        private static readonly object ClientPoolSync = new object();

        public static NetworkOptions GetOptions()
        {
            return Plugin.Instance.GetPluginOptions().NetworkOptions;
        }

        public static bool IsProxyConfigured()
        {
            var options = GetOptions();

            return options.EnableProxyServer && CommonUtility.IsValidProxyUrl(options.ProxyServer);
        }

        public static bool ShouldProxy(string url)
        {
            var options = GetOptions();

            if (!options.EnableProxyServer || !CommonUtility.IsValidProxyUrl(options.ProxyServer))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (IsLocalHost(uri.Host))
            {
                return false;
            }

            return options.ProxyMode == ProxyModeOption.Global || IsWhitelisted(uri.Host, options.ProxyWhitelist);
        }

        /// <summary>
        /// One client per proxy configuration, reused by every proxied request so that connections
        /// (and TLS handshakes) are pooled instead of rebuilt for each call. A new configuration
        /// retires the previous client, which both releases its connections and makes a changed
        /// proxy address take effect immediately. The handler's own connections are recycled once
        /// they have been idle, so a proxy name that moves in DNS is re-resolved as well.
        /// </summary>
        public static HttpClient GetHttpClient()
        {
            var key = BuildProxyKey();

            if (ClientPool.TryGetValue(key, out var cached)) return cached;

            lock (ClientPoolSync)
            {
                if (ClientPool.TryGetValue(key, out cached)) return cached;

                var client = new HttpClient(CreateHttpClientHandler())
                {
                    // Per-request timeouts are enforced with a linked token source, because the
                    // timeout of a shared client cannot be changed per request.
                    Timeout = Timeout.InfiniteTimeSpan
                };

                foreach (var staleKey in ClientPool.Keys.ToList())
                {
                    if (string.Equals(staleKey, key, StringComparison.Ordinal)) continue;

                    if (ClientPool.TryRemove(staleKey, out var stale)) RetireClient(stale);
                }

                ClientPool[key] = client;

                return client;
            }
        }

        public static HttpClientHandler CreateHttpClientHandler()
        {
            var handler = new HttpClientHandler
            {
                Proxy = CreateWebProxy(),
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            return handler;
        }

        // Credentials are part of the identity but must not be readable from the key.
        private static string BuildProxyKey()
        {
            if (!CommonUtility.TryParseProxyUrl(GetOptions().ProxyServer, out var schema, out var host, out var port,
                    out var username, out var password))
            {
                return "unconfigured";
            }

            var raw = string.Join("|", schema, host, port.ToString(CultureInfo.InvariantCulture),
                username, password);

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));

                return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        // The client lives in a pool entry that is only reachable while its configuration is the
        // active one, so its connections can still be serving a request that was already sent.
        private static void RetireClient(HttpClient client)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RetireGracePeriod).ConfigureAwait(false);
                    client.Dispose();
                }
                catch
                {
                    // Best effort; the handler is collected with the client.
                }
            });
        }

        public static WebProxy CreateWebProxy()
        {
            var options = GetOptions();

            if (!CommonUtility.TryParseProxyUrl(options.ProxyServer, out var schema, out var host, out var port,
                    out var username, out var password))
            {
                return null;
            }

            var proxyUri = new UriBuilder(schema, host, port).Uri;
            var proxy = new WebProxy(proxyUri);

            if (!string.IsNullOrEmpty(username))
            {
                proxy.Credentials = new NetworkCredential(username, password);
            }

            return proxy;
        }

        public static IHttpClient Wrap(IHttpClient httpClient)
        {
            if (httpClient is null || httpClient is ProxyHttpClient) return httpClient;

            return new ProxyHttpClient(httpClient);
        }

        /// <summary>
        /// Replaces the IHttpClient fields of a server provider with a proxy-aware wrapper.
        /// Used for providers (TMDB, OpenSubtitles) that create their own outgoing requests.
        /// </summary>
        public static void PatchProviderHttpClients(object provider)
        {
            if (provider is null) return;

            for (var type = provider.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(IHttpClient).IsAssignableFrom(field.FieldType)) continue;

                    if (field.GetValue(provider) is IHttpClient httpClient && !(httpClient is ProxyHttpClient))
                    {
                        field.SetValue(provider, Wrap(httpClient));
                    }
                }
            }
        }

        private static bool IsWhitelisted(string host, string whitelist)
        {
            if (string.IsNullOrWhiteSpace(whitelist)) return false;

            foreach (var entry in whitelist.Split(WhitelistSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var domain = entry.Trim().TrimStart('.').ToLowerInvariant();

                if (domain.Length == 0) continue;

                var subdomainOnly = domain.StartsWith("*.", StringComparison.Ordinal);

                if (subdomainOnly) domain = domain.Substring(2);

                if (domain.Length == 0) continue;

                if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)) return true;

                if (!subdomainOnly && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;

                if (subdomainOnly && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool IsLocalHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
            {
                if (IPAddress.IsLoopback(address)) return true;

                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var bytes = address.GetAddressBytes();

                    return bytes[0] == 10 ||
                           bytes[0] == 127 ||
                           bytes[0] == 169 && bytes[1] == 254 ||
                           bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 ||
                           bytes[0] == 192 && bytes[1] == 168;
                }

                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback);
                }

                return false;
            }

            // A host name without a dot is usually a machine name on the local network.
            return !host.Contains(".");
        }

        internal sealed class ProxyHttpClient : IHttpClient
        {
            private readonly IHttpClient _inner;

            public ProxyHttpClient(IHttpClient inner)
            {
                _inner = inner;
            }

            public Task<HttpResponseInfo> GetResponse(HttpRequestOptions options)
            {
                return ProxyApi.ShouldProxy(options?.Url)
                    ? SendForResponseAsync(options, "GET")
                    : _inner.GetResponse(options);
            }

            public Task<Stream> Get(HttpRequestOptions options)
            {
                return ProxyApi.ShouldProxy(options?.Url)
                    ? SendForStreamAsync(options, "GET")
                    : _inner.Get(options);
            }

            public Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod)
            {
                return ProxyApi.ShouldProxy(options?.Url)
                    ? SendForResponseAsync(options, httpMethod)
                    : _inner.SendAsync(options, httpMethod);
            }

            public Task<HttpResponseInfo> Post(HttpRequestOptions options)
            {
                return ProxyApi.ShouldProxy(options?.Url)
                    ? SendForResponseAsync(options, "POST")
                    : _inner.Post(options);
            }

            public async Task<string> GetTempFile(HttpRequestOptions options)
            {
                if (!ProxyApi.ShouldProxy(options?.Url)) return await _inner.GetTempFile(options).ConfigureAwait(false);

                var response = await SendForResponseAsync(options, "GET").ConfigureAwait(false);

                return response?.TempFilePath;
            }

            public async Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options)
            {
                if (!ProxyApi.ShouldProxy(options?.Url))
                {
                    return await _inner.GetTempFileResponse(options).ConfigureAwait(false);
                }

                return await SendForResponseAsync(options, "GET", true).ConfigureAwait(false);
            }

            public IDisposable GetConnectionContext(HttpRequestOptions options)
            {
                return _inner.GetConnectionContext(options);
            }

            private async Task<Stream> SendForStreamAsync(HttpRequestOptions options, string httpMethod)
            {
                var response = await SendForResponseAsync(options, httpMethod).ConfigureAwait(false);

                return response?.Content;
            }

            private async Task<HttpResponseInfo> SendForResponseAsync(HttpRequestOptions options, string httpMethod,
                bool writeToTempFile = false)
            {
                var client = ProxyApi.GetHttpClient();

                using var timeoutSource = CreateTimeoutSource(options);
                var cancellationToken = timeoutSource.Token;

                using var request = CreateRequest(options, httpMethod);
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var memoryStream = new MemoryStream();

                await sourceStream.CopyToAsync(memoryStream, 81920, cancellationToken)
                    .ConfigureAwait(false);
                memoryStream.Position = 0;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var header in response.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }

                foreach (var header in response.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }

                var result = new HttpResponseInfo
                {
                    StatusCode = response.StatusCode,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    ResponseUrl = options.Url,
                    ContentLength = memoryStream.Length,
                    Headers = headers
                };

                if (writeToTempFile)
                {
                    var tempFilePath = GetTempFilePath();
                    await File.WriteAllBytesAsync(tempFilePath, memoryStream.ToArray(), cancellationToken)
                        .ConfigureAwait(false);

                    result.TempFilePath = tempFilePath;
                    memoryStream.Dispose();
                }
                else
                {
                    result.Content = memoryStream;
                }

                return result;
            }

            // The client is shared, so its Timeout cannot carry a per-request value: the caller's
            // token is linked with the requested timeout instead, which the whole transfer honours.
            // A timeout surfaces as a cancellation of this source only, never of the caller's token.
            private static CancellationTokenSource CreateTimeoutSource(HttpRequestOptions options)
            {
                var timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : ProxyApi.DefaultTimeoutMs;

                var source = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
                source.CancelAfter(timeoutMs);

                return source;
            }

            private static HttpRequestMessage CreateRequest(HttpRequestOptions options, string httpMethod)
            {
                var request = new HttpRequestMessage(new HttpMethod(httpMethod ?? "GET"), options.Url);

                if (!string.IsNullOrEmpty(options.AcceptHeader))
                {
                    request.Headers.TryAddWithoutValidation("Accept", options.AcceptHeader);
                }

                if (!string.IsNullOrEmpty(options.UserAgent))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
                }

                if (options.RequestHeaders != null)
                {
                    foreach (var header in options.RequestHeaders)
                    {
                        if (string.IsNullOrEmpty(header.Key) || header.Value is null) continue;

                        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        {
                            request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                }

                if (options.RequestHttpContent != null)
                {
                    request.Content = options.RequestHttpContent;
                }
                else if (!options.RequestContentBytes.IsEmpty)
                {
                    request.Content = new ByteArrayContent(options.RequestContentBytes.ToArray());
                }
                else if (!options.RequestContent.IsEmpty)
                {
                    var stringContent = new StringContent(options.RequestContent.ToString(), Encoding.UTF8);

                    if (!string.IsNullOrEmpty(options.RequestContentType))
                    {
                        stringContent.Headers.TryAddWithoutValidation("Content-Type", options.RequestContentType);
                    }

                    request.Content = stringContent;
                }

                return request;
            }

            private static string GetTempFilePath()
            {
                var tempDirectory = Plugin.Instance.ApplicationPaths.TempDirectory;

                Directory.CreateDirectory(tempDirectory);

                return Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            }
        }
    }
}
