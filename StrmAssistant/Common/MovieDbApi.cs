using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class MovieDbApi
    {
        private const string MovieDbProviderBaseTypeName = "MovieDbProviderBase";
        private const string MovieDbApiHost = "api.themoviedb.org";

        // Emby uses zh-Hant as its Traditional Chinese code, which is not a TMDB request language.
        // TMDB exposes zh-TW for Traditional Chinese and zh-CN for Simplified Chinese.
        private const string PreferredTraditionalChineseLanguage = "zh-TW";

        private static readonly Regex LanguageParamRegex =
            new Regex(@"([?&]language=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ImageLanguageParamRegex =
            new Regex(@"([?&]include_image_language=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger _logger;

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

                    httpClientField.SetValue(provider, new MovieDbHttpClient(httpClient));
                    _logger.Info("MovieDb provider is patched: {0}", provider.GetType().Name);
                }
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

        private static bool IsChineseLanguage(string language)
        {
            if (string.IsNullOrEmpty(language)) return false;

            return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(language, "zho", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(language, "zhtw", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(language, "yue", StringComparison.OrdinalIgnoreCase);
        }

        private static string RewriteUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions
                    .PreferTraditionalChinese)
            {
                return url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, MovieDbApiHost, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            var rewrittenUrl = LanguageParamRegex.Replace(url, match =>
                match.Groups[1].Value + (IsChineseLanguage(match.Groups[2].Value)
                    ? PreferredTraditionalChineseLanguage
                    : match.Groups[2].Value));
            rewrittenUrl = ImageLanguageParamRegex.Replace(rewrittenUrl, match =>
                match.Groups[1].Value + string.Join(",",
                    match.Groups[2].Value.Split(',').Select(language =>
                        IsChineseLanguage(language) ? PreferredTraditionalChineseLanguage : language)));

            return rewrittenUrl;
        }

        private class MovieDbHttpClient : IHttpClient
        {
            private readonly IHttpClient _httpClient;

            public MovieDbHttpClient(IHttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            private static string Rewrite(HttpRequestOptions options)
            {
                if (options != null)
                {
                    options.Url = RewriteUrl(options.Url);
                }

                return options?.Url;
            }

            public Task<HttpResponseInfo> GetResponse(HttpRequestOptions options)
            {
                Rewrite(options);
                return _httpClient.GetResponse(options);
            }

            public Task<Stream> Get(HttpRequestOptions options)
            {
                Rewrite(options);
                return _httpClient.Get(options);
            }

            public Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod)
            {
                Rewrite(options);
                return _httpClient.SendAsync(options, httpMethod);
            }

            public Task<HttpResponseInfo> Post(HttpRequestOptions options)
            {
                Rewrite(options);
                return _httpClient.Post(options);
            }

            public Task<string> GetTempFile(HttpRequestOptions options)
            {
                Rewrite(options);
                return _httpClient.GetTempFile(options);
            }

            public Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options)
            {
                Rewrite(options);
                return _httpClient.GetTempFileResponse(options);
            }

            public IDisposable GetConnectionContext(HttpRequestOptions options)
            {
                Rewrite(options);
                return _httpClient.GetConnectionContext(options);
            }
        }
    }
}
