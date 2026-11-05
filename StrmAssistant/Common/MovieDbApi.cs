using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class MovieDbApi
    {
        private const string MovieDbProviderBaseTypeName = "MovieDbProviderBase";
        private const string MovieDbApiHost = "api.themoviedb.org";
        private const string MovieDbAltApiHost = "api.tmdb.org";
        private const string MovieDbImageHost = "image.tmdb.org";
        private const string DefaultMovieDbApiRoot = "https://api.themoviedb.org";
        private const string DefaultMovieDbImageRoot = "https://image.tmdb.org";

        // Emby uses zh-Hant as its Traditional Chinese code, which is not a TMDB request language.
        // TMDB exposes zh-TW for Traditional Chinese and zh-CN for Simplified Chinese.
        private const string PreferredTraditionalChineseLanguage = "zh-TW";

        private static readonly Regex LanguageParamRegex =
            new Regex(@"([?&]language=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ImageLanguageParamRegex =
            new Regex(@"([?&]include_image_language=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ApiKeyParamRegex =
            new Regex(@"([?&]api_key=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger _logger;

        private static string _originalImageBaseUrl;
        private static string _appliedImageBaseUrl;
        private static string _defaultApiKey;

        public MovieDbApi()
        {
            _logger = Plugin.Instance.Logger;
        }

        public void Initialize()
        {
            try
            {
                var providers = Plugin.Instance.ApplicationHost.GetExports<IMetadataProvider>().Cast<object>()
                    .Concat(Plugin.Instance.ApplicationHost.GetExports<IImageProvider>())
                    .Where(IsMovieDbProvider)
                    .Distinct()
                    .ToList();

                foreach (var provider in providers)
                {
                    var httpClientField = FindHttpClientField(provider.GetType());

                    if (httpClientField is null) continue;

                    var httpClient = httpClientField.GetValue(provider) as IHttpClient;

                    if (httpClient is null || httpClient is MovieDbHttpClient) continue;

                    httpClientField.SetValue(provider, new MovieDbHttpClient(ProxyApi.Wrap(httpClient)));
                    _logger.Info("MovieDb provider is patched: {0}", provider.GetType().Name);
                }

                ApplyAlternativeImageBaseUrl();
            }
            catch (Exception e)
            {
                _logger.ErrorException("MovieDbApi Initialize", e);
            }
        }

        private static bool IsMovieDbProvider(object provider)
        {
            for (var type = provider.GetType(); type != null; type = type.BaseType)
            {
                if (string.Equals(type.Name, MovieDbProviderBaseTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static FieldInfo FindHttpClientField(Type providerType)
        {
            for (var type = providerType; type != null; type = type.BaseType)
            {
                var field = type.GetField("HttpClient",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (field != null && typeof(IHttpClient).IsAssignableFrom(field.FieldType))
                {
                    return field;
                }
            }

            return null;
        }

        /// <summary>
        /// The effective TMDB endpoints. A configured local mirror wins over the legacy
        /// alternative source settings; when it is disabled the legacy options behave exactly
        /// as they did before.
        /// </summary>
        private sealed class MovieDbSource
        {
            public string ApiRoot { get; set; }
            public string ImageRoot { get; set; }
            public string ApiKey { get; set; }

            public bool IsEmpty => string.IsNullOrEmpty(ApiRoot) && string.IsNullOrEmpty(ImageRoot);
        }

        private static MovieDbSource GetEffectiveSource()
        {
            var options = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions;

            if (options.EnableLocalMovieDb && CommonUtility.IsValidHttpUrl(options.LocalMovieDbUrl))
            {
                var root = options.LocalMovieDbUrl.Trim().TrimEnd('/');
                var apiKey = CommonUtility.IsValidMovieDbApiKey(options.LocalMovieDbApiKey)
                    ? options.LocalMovieDbApiKey.Trim()
                    : CommonUtility.IsValidMovieDbApiKey(options.AltMovieDbApiKey)
                        ? options.AltMovieDbApiKey.Trim()
                        : null;

                return new MovieDbSource
                {
                    ApiRoot = root,
                    ImageRoot = root,
                    ApiKey = apiKey
                };
            }

            if (!options.EnableAltMovieDbUrl) return null;

            return new MovieDbSource
            {
                ApiRoot = CommonUtility.IsValidHttpUrl(options.AltMovieDbApiUrl)
                    ? options.AltMovieDbApiUrl.Trim().TrimEnd('/')
                    : null,
                ImageRoot = CommonUtility.IsValidHttpUrl(options.AltMovieDbImageUrl)
                    ? NormalizeImageRoot(options.AltMovieDbImageUrl)
                    : null,
                ApiKey = CommonUtility.IsValidMovieDbApiKey(options.AltMovieDbApiKey)
                    ? options.AltMovieDbApiKey.Trim()
                    : null
            };
        }

        private static string NormalizeImageRoot(string url)
        {
            var root = url.Trim().TrimEnd('/');

            if (root.EndsWith("/t/p", StringComparison.OrdinalIgnoreCase))
            {
                root = root.Substring(0, root.Length - 4).TrimEnd('/');
            }

            return root;
        }

        private static string GetEffectiveImageBaseUrl(MovieDbSource source)
        {
            return string.IsNullOrEmpty(source?.ImageRoot)
                ? null
                : source.ImageRoot.TrimEnd('/') + "/t/p/";
        }

        private static string GetDefaultApiKey()
        {
            if (_defaultApiKey != null) return _defaultApiKey;

            try
            {
                var provider = Plugin.Instance.ApplicationHost.GetExports<IMetadataProvider>()
                    .FirstOrDefault(IsMovieDbProvider);

                for (var type = provider?.GetType(); type != null; type = type.BaseType)
                {
                    var field = type.GetField("ApiKey",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (field != null && field.FieldType == typeof(string))
                    {
                        _defaultApiKey = field.GetValue(null) as string ?? string.Empty;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("MovieDbApi GetDefaultApiKey - " + e.Message);
            }

            return _defaultApiKey ?? string.Empty;
        }

        /// <summary>
        /// Root address (without the /3 path) used by plugin-side TMDB lookups so that they
        /// follow the same local mirror as the built-in providers.
        /// </summary>
        public static string GetEffectiveApiRoot()
        {
            return GetEffectiveSource()?.ApiRoot ?? DefaultMovieDbApiRoot;
        }

        public static string GetEffectiveApiKey()
        {
            var sourceKey = GetEffectiveSource()?.ApiKey;

            return CommonUtility.IsValidMovieDbApiKey(sourceKey) ? sourceKey : GetDefaultApiKey();
        }

        private static string GetQueryParam(Uri uri, string name)
        {
            var query = uri.Query;

            if (string.IsNullOrEmpty(query)) return null;

            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrEmpty(pair)) continue;

                var separator = pair.IndexOf('=');
                var key = separator >= 0 ? pair.Substring(0, separator) : pair;

                if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
                {
                    return separator >= 0 ? Uri.UnescapeDataString(pair.Substring(separator + 1)) : string.Empty;
                }
            }

            return null;
        }

        private static bool IsTmdbApiHost(Uri uri)
        {
            return string.Equals(uri.Host, MovieDbApiHost, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, MovieDbAltApiHost, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTmdbImageHost(Uri uri)
        {
            return string.Equals(uri.Host, MovieDbImageHost, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReplaceBaseUrl(string url, Uri uri, string altBaseUrl)
        {
            if (!CommonUtility.IsValidHttpUrl(altBaseUrl)) return url;

            var baseUrl = altBaseUrl.Trim().TrimEnd('/');

            return baseUrl + uri.PathAndQuery;
        }

        private sealed class UrlRewriteResult
        {
            public string Url { get; set; }
            public bool IsTmdbApiRequest { get; set; }
            public bool IsConfigurationRequest { get; set; }
        }

        private static UrlRewriteResult RewriteUrl(string url)
        {
            var result = new UrlRewriteResult { Url = url };

            if (string.IsNullOrEmpty(url)) return result;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return result;

            var source = GetEffectiveSource();
            var isApiHost = IsTmdbApiHost(uri);
            var isImageHost = IsTmdbImageHost(uri);

            if (!isApiHost && !isImageHost) return result;

            var rewrittenUrl = url;

            if (isApiHost)
            {
                result.IsTmdbApiRequest = true;
                result.IsConfigurationRequest = string.Equals(uri.AbsolutePath.TrimEnd('/'), "/3/configuration",
                    StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(source?.ApiRoot))
                {
                    rewrittenUrl = ReplaceBaseUrl(rewrittenUrl, uri, source.ApiRoot);

                    if (!string.IsNullOrEmpty(source.ApiKey))
                    {
                        rewrittenUrl = ApiKeyParamRegex.Replace(rewrittenUrl,
                            match => match.Groups[1].Value + source.ApiKey);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(source?.ImageRoot))
            {
                rewrittenUrl = ReplaceBaseUrl(rewrittenUrl, uri, source.ImageRoot);
            }

            result.Url = rewrittenUrl;

            return result;
        }

        private void ApplyAlternativeImageBaseUrl()
        {
            var imageBaseUrl = GetEffectiveImageBaseUrl(GetEffectiveSource());

            EnsureImageBaseUrl(imageBaseUrl);
        }

        private static void EnsureImageBaseUrl(string imageBaseUrl)
        {
            if (string.IsNullOrEmpty(imageBaseUrl))
            {
                if (string.IsNullOrEmpty(_appliedImageBaseUrl)) return;
            }
            else if (string.Equals(_appliedImageBaseUrl, imageBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var tmdbSettings = FindTmdbSettings();

            if (tmdbSettings is null) return;

            try
            {
                var images = tmdbSettings.GetType().GetProperty("images")?.GetValue(tmdbSettings);
                var secureBaseUrlProperty = images?.GetType().GetProperty("secure_base_url");

                if (secureBaseUrlProperty is null || !secureBaseUrlProperty.CanWrite) return;

                if (!string.IsNullOrEmpty(imageBaseUrl))
                {
                    if (string.IsNullOrEmpty(_originalImageBaseUrl))
                    {
                        _originalImageBaseUrl = secureBaseUrlProperty.GetValue(images) as string;
                    }

                    secureBaseUrlProperty.SetValue(images, imageBaseUrl);
                    _appliedImageBaseUrl = imageBaseUrl;
                    Plugin.Instance.Logger.Info("MovieDb image base url is set to {0}", imageBaseUrl);
                }
                else
                {
                    var originalImageBaseUrl = _originalImageBaseUrl;

                    secureBaseUrlProperty.SetValue(images, originalImageBaseUrl);
                    _originalImageBaseUrl = null;
                    _appliedImageBaseUrl = null;
                    Plugin.Instance.Logger.Info("MovieDb image base url is restored to {0}", originalImageBaseUrl);
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.ErrorException("MovieDbApi ApplyAlternativeImageBaseUrl", e);
            }
        }

        private static object FindTmdbSettings()
        {
            var provider = Plugin.Instance.ApplicationHost.GetExports<IMetadataProvider>()
                .FirstOrDefault(IsMovieDbProvider);

            if (provider is null) return null;

            for (var type = provider.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("_tmdbSettings",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (field != null) return field.GetValue(null);
            }

            return null;
        }

        private sealed class PreparedRequest
        {
            public string ResponseImageBaseUrl { get; set; }
        }

        private static PreparedRequest Prepare(HttpRequestOptions options)
        {
            if (options is null) return null;

            var rewrite = RewriteUrl(options.Url);

            options.Url = rewrite.Url;

            if (!rewrite.IsTmdbApiRequest) return null;

            return new PreparedRequest
            {
                ResponseImageBaseUrl =
                    rewrite.IsConfigurationRequest ? GetEffectiveImageBaseUrl(GetEffectiveSource()) : null
            };
        }

        private static async Task<HttpResponseInfo> PostProcessAsync(PreparedRequest prepared, HttpResponseInfo response)
        {
            if (prepared?.ResponseImageBaseUrl is null || response?.Content is null) return response;

            try
            {
                string content;

                using (var reader = new StreamReader(response.Content, Encoding.UTF8, true, 8192, true))
                {
                    content = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                content = content
                    .Replace("https://image.tmdb.org/t/p/", prepared.ResponseImageBaseUrl)
                    .Replace("http://image.tmdb.org/t/p/", prepared.ResponseImageBaseUrl)
                    .Replace("https://image.tmdb.org/t/p", prepared.ResponseImageBaseUrl.TrimEnd('/'))
                    .Replace("http://image.tmdb.org/t/p", prepared.ResponseImageBaseUrl.TrimEnd('/'));

                var bytes = Encoding.UTF8.GetBytes(content);

                response.Content.Dispose();
                response.Content = new MemoryStream(bytes);
                response.ContentLength = bytes.Length;

                Plugin.Instance.Logger.Debug("MovieDb configuration image base url is rewritten to {0}",
                    prepared.ResponseImageBaseUrl);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("MovieDbApi PostProcessAsync - " + e.Message);
            }

            return response;
        }

        private class MovieDbHttpClient : IHttpClient
        {
            private readonly IHttpClient _httpClient;

            public MovieDbHttpClient(IHttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            private async Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, Func<Task<HttpResponseInfo>> send)
            {
                var prepared = Prepare(options);
                var response = await send().ConfigureAwait(false);

                return await PostProcessAsync(prepared, response).ConfigureAwait(false);
            }

            public Task<HttpResponseInfo> GetResponse(HttpRequestOptions options)
            {
                return SendAsync(options, () => _httpClient.GetResponse(options));
            }

            public Task<Stream> Get(HttpRequestOptions options)
            {
                Prepare(options);
                return _httpClient.Get(options);
            }

            public Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod)
            {
                return SendAsync(options, () => _httpClient.SendAsync(options, httpMethod));
            }

            public Task<HttpResponseInfo> Post(HttpRequestOptions options)
            {
                return SendAsync(options, () => _httpClient.Post(options));
            }

            public Task<string> GetTempFile(HttpRequestOptions options)
            {
                Prepare(options);
                return _httpClient.GetTempFile(options);
            }

            public Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options)
            {
                return SendAsync(options, () => _httpClient.GetTempFileResponse(options));
            }

            public IDisposable GetConnectionContext(HttpRequestOptions options)
            {
                Prepare(options);
                return _httpClient.GetConnectionContext(options);
            }
        }
    }
}
