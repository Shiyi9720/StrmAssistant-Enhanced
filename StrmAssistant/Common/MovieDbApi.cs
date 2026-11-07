using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

        private static readonly Regex LanguageParamRegex =
            new Regex(@"([?&]language=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ImageLanguageParamRegex =
            new Regex(@"([?&]include_image_language=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OriginalLanguageRegex =
            new Regex(@"""original_language""\s*:\s*""([a-zA-Z_-]+)""", RegexOptions.Compiled);

        private static readonly Regex ApiKeyParamRegex =
            new Regex(@"([?&]api_key=)([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TmdbApiTargetRegex =
            new Regex(@"^/3/(movie|tv|collection|person)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger _logger;

        private static ILibraryManager _libraryManager;
        private static string _originalImageBaseUrl;
        private static string _appliedImageBaseUrl;
        private static string _defaultApiKey;
        private static LruCache _itemLanguageCache = new LruCache(200);
        private static LruCache _originalLanguageCache = new LruCache(300);

        public MovieDbApi(ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
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
                    _logger?.Info("MovieDb provider is patched: {0}", provider.GetType().Name);
                }

                _itemLanguageCache = new LruCache(200);
                _originalLanguageCache = new LruCache(300);
                ApplyAlternativeImageBaseUrl();
            }
            catch (Exception e)
            {
                _logger?.ErrorException("MovieDbApi Initialize", e);
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

        public static string GetEffectiveImageDownloadRoot()
        {
            var source = GetEffectiveSource();

            return string.IsNullOrEmpty(source?.ImageRoot)
                ? DefaultMovieDbImageRoot.TrimEnd('/') + "/t/p/"
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
            public bool IsCacheable { get; set; }
            public bool IsConfigurationRequest { get; set; }
            public bool RequestedSimplifiedChinese { get; set; }
            public string EffectiveLanguage { get; set; }
            public bool Rewritten { get; set; }
        }

        private static bool IsCacheableApiPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            if (string.Equals(path.TrimEnd('/'), "/3/configuration", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return path.StartsWith("/3/search/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/3/movie/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/3/tv/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/3/person/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/3/collection/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// TMDB only knows zh-CN, zh-TW and zh-HK. Script subtags used by Emby (zh-Hans,
        /// zh-Hant) are mapped onto the closest TMDB language so the request is understood.
        /// </summary>
        private static string NormalizeChineseLanguage(string language)
        {
            if (string.IsNullOrEmpty(language)) return language;

            var code = language.Trim().Replace('_', '-');

            if (code.IndexOf("hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(code, "zhtw", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-TW";
            }

            if (code.IndexOf("hans", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "zho", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-CN";
            }

            return language.Trim();
        }

        private static string SetImageLanguageParam(string url, string preferredImageLanguages)
        {
            if (ImageLanguageParamRegex.IsMatch(url))
            {
                return ImageLanguageParamRegex.Replace(url, match =>
                    match.Groups[1].Value + MergeImageLanguages(match.Groups[2].Value, preferredImageLanguages));
            }

            return url + (url.IndexOf('?') >= 0 ? "&" : "?") + "include_image_language=" +
                   preferredImageLanguages + ",null";
        }

        private static string MergeImageLanguages(string existing, string preferredImageLanguages)
        {
            var existingParts = (existing ?? string.Empty).Split(',')
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrEmpty(part))
                .ToList();

            var merged = new List<string>();

            merged.AddRange((preferredImageLanguages ?? string.Empty).Split(',')
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrEmpty(part)));
            merged.AddRange(existingParts.Where(part =>
                !string.Equals(part, "null", StringComparison.OrdinalIgnoreCase)));
            merged.Add("null");

            return string.Join(",", merged
                .GroupBy(part => part, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()));
        }

        private static string ResolveItemLanguage(Uri uri)
        {
            if (_libraryManager is null) return null;

            var match = TmdbApiTargetRegex.Match(uri.AbsolutePath);

            if (!match.Success) return null;

            var kind = match.Groups[1].Value.ToLowerInvariant();
            var tmdbId = match.Groups[2].Value;
            var cacheKey = kind + ":" + tmdbId;

            if (_itemLanguageCache.TryGetFromCache<string>(cacheKey, out var cached) &&
                !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            string itemType;

            switch (kind)
            {
                case "movie":
                    itemType = nameof(Movie);
                    break;
                case "tv":
                    itemType = nameof(Series);
                    break;
                case "collection":
                    itemType = nameof(BoxSet);
                    break;
                case "person":
                    itemType = nameof(Person);
                    break;
                default:
                    return null;
            }

            try
            {
                var item = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    Limit = 1,
                    IncludeItemTypes = new[] { itemType },
                    AnyProviderIdEquals = new[]
                    {
                        new KeyValuePair<string, string>("Tmdb", tmdbId)
                    }
                }).FirstOrDefault();

                if (item is null) return null;

                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var language = item.GetPreferredImageLanguage(libraryOptions);

                if (!string.IsNullOrEmpty(language))
                {
                    _itemLanguageCache.AddOrUpdateCache(cacheKey, language);
                }

                return language;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("MovieDbApi ResolveItemLanguage - " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolves the original language of the TMDB item behind an image request. The
        /// original_language value captured from a previous detail response is preferred; when it
        /// is not available yet the original title is used as a best-effort fallback.
        /// </summary>
        private static string ResolveItemOriginalLanguage(Uri uri)
        {
            if (_libraryManager is null) return null;

            var match = TmdbApiTargetRegex.Match(uri.AbsolutePath);

            if (!match.Success) return null;

            var kind = match.Groups[1].Value.ToLowerInvariant();

            if (kind != "movie" && kind != "tv") return null;

            var tmdbId = match.Groups[2].Value;
            var cacheKey = kind + ":" + tmdbId;

            if (_originalLanguageCache.TryGetFromCache<string>(cacheKey, out var cached) &&
                !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            try
            {
                var itemType = kind == "movie" ? nameof(Movie) : nameof(Series);
                var item = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    Limit = 1,
                    IncludeItemTypes = new[] { itemType },
                    AnyProviderIdEquals = new[]
                    {
                        new KeyValuePair<string, string>("Tmdb", tmdbId)
                    }
                }).FirstOrDefault();

                if (item is null) return null;

                var language = LanguageUtility.GetLanguageByTitle(item.OriginalTitle) ??
                               LanguageUtility.GetLanguageByTitle(item.Name);

                if (string.IsNullOrEmpty(language)) return null;

                _originalLanguageCache.AddOrUpdateCache(cacheKey, language);

                return language;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("MovieDbApi ResolveItemOriginalLanguage - " + e.Message);
                return null;
            }
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
                result.IsCacheable = IsCacheableApiPath(uri.AbsolutePath);
                result.IsConfigurationRequest = string.Equals(uri.AbsolutePath.TrimEnd('/'), "/3/configuration",
                    StringComparison.OrdinalIgnoreCase);

                var options = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions;
                var language = GetQueryParam(uri, "language");
                var effectiveLanguage = language;
                var order = options.GetEffectiveChineseMetadataOrder();

                if (LanguageUtility.IsChineseLanguageCode(language))
                {
                    var targetLanguage =
                        order == ChineseMetadataOrderOption.TraditionalFirst
                            ? LanguageUtility.IsTraditionalChineseLanguageCode(language)
                                ? NormalizeChineseLanguage(language)
                                : "zh-TW"
                            : order == ChineseMetadataOrderOption.SimplifiedFirst
                                ? LanguageUtility.IsSimplifiedChineseLanguageCode(language)
                                    ? NormalizeChineseLanguage(language)
                                    : "zh-CN"
                                : NormalizeChineseLanguage(language);

                    if (!string.IsNullOrEmpty(targetLanguage) &&
                        !string.Equals(targetLanguage, language, StringComparison.OrdinalIgnoreCase))
                    {
                        rewrittenUrl = LanguageParamRegex.Replace(rewrittenUrl,
                            match => match.Groups[1].Value + targetLanguage);
                    }

                    effectiveLanguage = targetLanguage ?? language;
                }

                var preferredImageLanguages = new List<string>();

                if (options.PreferOriginalPoster)
                {
                    var originalLanguage = ResolveItemOriginalLanguage(uri);

                    if (!string.IsNullOrEmpty(originalLanguage))
                    {
                        preferredImageLanguages.Add(originalLanguage);
                    }

                    // Keep the requested language available as well; original-language images
                    // normally have a language tag that the default request would exclude.
                    if (!string.IsNullOrEmpty(effectiveLanguage))
                    {
                        preferredImageLanguages.Add(effectiveLanguage);
                    }
                }

                var chineseImageLanguages = (string)null;

                if (options.DistinguishChinesePosters)
                {
                    var imageLanguage = LanguageUtility.IsChineseLanguageCode(effectiveLanguage)
                        ? effectiveLanguage
                        : string.IsNullOrEmpty(effectiveLanguage)
                            ? ResolveItemLanguage(uri)
                            : null;
                    chineseImageLanguages = LanguageUtility.GetChineseImageLanguages(imageLanguage);
                }
                else if (order != ChineseMetadataOrderOption.LibraryDefault &&
                         LanguageUtility.IsChineseLanguageCode(effectiveLanguage) &&
                         ImageLanguageParamRegex.IsMatch(rewrittenUrl))
                {
                    chineseImageLanguages = LanguageUtility.GetChineseImageLanguages(effectiveLanguage);
                }

                if (!string.IsNullOrEmpty(chineseImageLanguages))
                {
                    preferredImageLanguages.AddRange(chineseImageLanguages.Split(',')
                        .Select(part => part.Trim())
                        .Where(part => !string.IsNullOrEmpty(part)));
                }

                if (preferredImageLanguages.Count > 0 &&
                    (options.PreferOriginalPoster || options.DistinguishChinesePosters ||
                     (order != ChineseMetadataOrderOption.LibraryDefault &&
                      LanguageUtility.IsChineseLanguageCode(effectiveLanguage) &&
                      ImageLanguageParamRegex.IsMatch(rewrittenUrl))))
                {
                    rewrittenUrl = SetImageLanguageParam(rewrittenUrl,
                        string.Join(",", preferredImageLanguages));
                }

                result.RequestedSimplifiedChinese =
                    LanguageUtility.IsSimplifiedChineseLanguageCode(effectiveLanguage);
                result.EffectiveLanguage = effectiveLanguage;

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
            result.Rewritten = !string.Equals(rewrittenUrl, url, StringComparison.Ordinal);

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
            public bool ConvertSimplifiedToTraditional { get; set; }
            public string OriginalLanguageCacheKey { get; set; }
            public string CacheableUrl { get; set; }
            public string EffectiveLanguage { get; set; }
            public bool Rewritten { get; set; }
            public LocalMetadataSource.LocalMetadataTarget LocalMetadataTarget { get; set; }
            public LocalEpisodeGroupMapping LocalEpisodeGroupMapping { get; set; }

            public LocalMetadataSource.LocalMetadataCaptureContext CreateCaptureContext()
            {
                return new LocalMetadataSource.LocalMetadataCaptureContext
                {
                    source = "online",
                    request = LocalMetadataTarget is null
                        ? CacheableUrl
                        : LocalMetadataTarget.Kind + ":" + LocalMetadataTarget.TmdbId,
                    url = MovieDbCache.SanitizeUrl(CacheableUrl),
                    language = EffectiveLanguage,
                    converted = ConvertSimplifiedToTraditional || LocalEpisodeGroupMapping != null,
                    rewritten = Rewritten
                };
            }

            public MovieDbCacheMeta CreateCacheMetadata()
            {
                return new MovieDbCacheMeta
                {
                    source = "online",
                    request = LocalMetadataTarget is null
                        ? CacheableUrl
                        : LocalMetadataTarget.Kind + ":" + LocalMetadataTarget.TmdbId,
                    url = MovieDbCache.SanitizeUrl(CacheableUrl),
                    language = EffectiveLanguage,
                    converted = ConvertSimplifiedToTraditional || LocalEpisodeGroupMapping != null,
                    rewritten = Rewritten,
                    capturedAtUtc = DateTime.UtcNow
                };
            }
        }

        private static string GetOriginalLanguageCacheKey(string url)
        {
            if (string.IsNullOrEmpty(url) ||
                !Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.PreferOriginalPoster)
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

            var match = TmdbApiTargetRegex.Match(uri.AbsolutePath);

            if (!match.Success) return null;

            var kind = match.Groups[1].Value.ToLowerInvariant();

            if (kind != "movie" && kind != "tv") return null;

            if (uri.AbsolutePath.IndexOf("/images", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            return kind + ":" + match.Groups[2].Value;
        }

        private static PreparedRequest Prepare(HttpRequestOptions options)
        {
            if (options is null) return null;

            var episodeGroupMapping = LocalEpisodeGroup.TryMapRequest(options.Url);

            if (episodeGroupMapping != null)
            {
                options.Url = episodeGroupMapping.Url;
            }

            var localMetadataTarget = LocalMetadataSource.TryParseTarget(options.Url);

            if (localMetadataTarget != null && episodeGroupMapping != null)
            {
                localMetadataTarget.LocalSeasonNumber = episodeGroupMapping.LocalSeasonNumber;
                localMetadataTarget.LocalEpisodeNumber = episodeGroupMapping.LocalEpisodeNumber;
            }

            var originalLanguageCacheKey = GetOriginalLanguageCacheKey(options.Url);
            var rewrite = RewriteUrl(options.Url);

            options.Url = rewrite.Url;

            if (!rewrite.IsTmdbApiRequest) return null;

            return new PreparedRequest
            {
                ResponseImageBaseUrl =
                    rewrite.IsConfigurationRequest ? GetEffectiveImageBaseUrl(GetEffectiveSource()) : null,
                ConvertSimplifiedToTraditional =
                    rewrite.RequestedSimplifiedChinese &&
                    Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.ConvertSimplifiedToTraditional,
                OriginalLanguageCacheKey = originalLanguageCacheKey,
                CacheableUrl = rewrite.IsCacheable ? rewrite.Url : null,
                EffectiveLanguage = rewrite.EffectiveLanguage,
                Rewritten = rewrite.Rewritten || episodeGroupMapping != null,
                LocalMetadataTarget = localMetadataTarget,
                LocalEpisodeGroupMapping = episodeGroupMapping
            };
        }

        private static bool IsJsonResponse(HttpResponseInfo response)
        {
            if (response.Content is null) return false;

            var contentType = response.ContentType;

            return string.IsNullOrEmpty(contentType) ||
                   contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   contentType.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<HttpResponseInfo> PostProcessAsync(PreparedRequest prepared, HttpResponseInfo response)
        {
            if (prepared is null || response?.Content is null) return response;

            var rewriteImageBaseUrl = !string.IsNullOrEmpty(prepared.ResponseImageBaseUrl);
            var convertToTraditional = prepared.ConvertSimplifiedToTraditional;
            var captureOriginalLanguage = !string.IsNullOrEmpty(prepared.OriginalLanguageCacheKey);
            var restoreLocalEpisodeGroup = prepared.LocalEpisodeGroupMapping != null;

            if (!rewriteImageBaseUrl && !convertToTraditional && !captureOriginalLanguage &&
                !restoreLocalEpisodeGroup) return response;

            if ((convertToTraditional || captureOriginalLanguage || restoreLocalEpisodeGroup) &&
                !IsJsonResponse(response)) return response;

            try
            {
                string content;

                using (var reader = new StreamReader(response.Content, Encoding.UTF8, true, 8192, true))
                {
                    content = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                if (rewriteImageBaseUrl)
                {
                    content = content
                        .Replace("https://image.tmdb.org/t/p/", prepared.ResponseImageBaseUrl)
                        .Replace("http://image.tmdb.org/t/p/", prepared.ResponseImageBaseUrl)
                        .Replace("https://image.tmdb.org/t/p", prepared.ResponseImageBaseUrl.TrimEnd('/'))
                        .Replace("http://image.tmdb.org/t/p", prepared.ResponseImageBaseUrl.TrimEnd('/'));

                    Plugin.Instance.Logger.Debug("MovieDb configuration image base url is rewritten to {0}",
                        prepared.ResponseImageBaseUrl);
                }

                if (convertToTraditional)
                {
                    content = LanguageUtility.ConvertSimplifiedToTraditional(content);
                }

                if (captureOriginalLanguage)
                {
                    var match = OriginalLanguageRegex.Match(content);

                    if (match.Success)
                    {
                        _originalLanguageCache.AddOrUpdateCache(prepared.OriginalLanguageCacheKey,
                            match.Groups[1].Value.ToLowerInvariant());
                    }
                }

                if (restoreLocalEpisodeGroup)
                {
                    var mapping = prepared.LocalEpisodeGroupMapping;

                    content = Regex.Replace(content, @"(""season_number""\s*:\s*)\d+",
                        match => match.Groups[1].Value + mapping.LocalSeasonNumber,
                        RegexOptions.IgnoreCase);

                    if (mapping.IsEpisodeRequest && mapping.LocalEpisodeNumber.HasValue)
                    {
                        content = Regex.Replace(content, @"(""episode_number""\s*:\s*)\d+",
                            match => match.Groups[1].Value + mapping.LocalEpisodeNumber.Value,
                            RegexOptions.IgnoreCase);
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(content);

                response.Content.Dispose();
                response.Content = new MemoryStream(bytes);
                response.ContentLength = bytes.Length;
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

            private async Task<HttpResponseInfo> SendAsync(HttpRequestOptions options,
                Func<Task<HttpResponseInfo>> send, bool isGet)
            {
                var prepared = Prepare(options);
                var localResponse = isGet
                    ? LocalMetadataSource.TryCreateResponse(prepared?.LocalMetadataTarget)
                    : null;

                if (localResponse != null)
                {
                    return await PostProcessAsync(prepared, localResponse).ConfigureAwait(false);
                }

                var cacheKey = isGet && !string.IsNullOrEmpty(prepared?.CacheableUrl)
                    ? MovieDbCache.Instance.GetKey(prepared.CacheableUrl)
                    : null;

                if (!string.IsNullOrEmpty(cacheKey) &&
                    MovieDbCache.Instance.TryGet(cacheKey, false, out var cached, out _))
                {
                    return await FinalizeAsync(prepared, CreateCachedResponse(cached, false), isGet)
                        .ConfigureAwait(false);
                }

                HttpResponseInfo response;

                try
                {
                    if (prepared != null)
                    {
                        LocalMetadataSource.RecordOnlineRequest();
                    }

                    response = await send().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (!string.IsNullOrEmpty(cacheKey) && !options.CancellationToken.IsCancellationRequested &&
                        MovieDbCache.Instance.TryGet(cacheKey, true, out var stale, out _))
                    {
                        LogStaleFallback(prepared.CacheableUrl, e.Message);

                        return await FinalizeAsync(prepared, CreateCachedResponse(stale, true), isGet)
                            .ConfigureAwait(false);
                    }

                    throw;
                }

                if (!string.IsNullOrEmpty(cacheKey) && !IsSuccessful(response) &&
                    MovieDbCache.Instance.TryGet(cacheKey, true, out var staleResponse, out _))
                {
                    LogStaleFallback(prepared.CacheableUrl,
                        response is null ? "no response" : ((int)response.StatusCode).ToString());

                    try
                    {
                        response?.Content?.Dispose();
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }

                    return await FinalizeAsync(prepared, CreateCachedResponse(staleResponse, true), isGet)
                        .ConfigureAwait(false);
                }

                return await FinalizeAsync(prepared, response, isGet, cacheKey).ConfigureAwait(false);
            }

            private static async Task<HttpResponseInfo> FinalizeAsync(PreparedRequest prepared,
                HttpResponseInfo response, bool capture, string cacheKey = null)
            {
                var processed = await PostProcessAsync(prepared, response).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(cacheKey) && IsSuccessful(processed) && IsJsonResponse(processed))
                {
                    var body = await ReadBodyAsync(processed).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(body))
                    {
                        MovieDbCache.Instance.Store(cacheKey, prepared.CacheableUrl, body,
                            processed.ContentType, prepared.CreateCacheMetadata());
                    }
                }

                if (capture)
                {
                    await LocalMetadataSource.CaptureAsync(prepared?.LocalMetadataTarget, processed,
                        prepared?.CreateCaptureContext()).ConfigureAwait(false);
                }

                return processed;
            }

            private static bool IsSuccessful(HttpResponseInfo response)
            {
                if (response is null) return false;

                var statusCode = (int)response.StatusCode;
                return statusCode >= 200 && statusCode < 300;
            }

            private static async Task<string> ReadBodyAsync(HttpResponseInfo response)
            {
                if (response?.Content is null) return null;

                var buffer = new MemoryStream();
                var completed = true;

                try
                {
                    await response.Content.CopyToAsync(buffer).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    completed = false;
                    Plugin.Instance?.Logger?.Debug("MovieDb cache - Response read failed: " + e.Message);
                }

                var bytes = buffer.ToArray();

                try
                {
                    response.Content.Dispose();
                }
                catch
                {
                    // Best effort cleanup only.
                }

                response.Content = new MemoryStream(bytes);
                response.ContentLength = bytes.Length;

                if (!completed || bytes.Length == 0) return null;

                var body = Encoding.UTF8.GetString(bytes);

                return body.Length > 0 && body[0] == '\uFEFF' ? body.Substring(1) : body;
            }

            private static HttpResponseInfo CreateCachedResponse(MovieDbCacheEntry entry, bool stale)
            {
                var bytes = Encoding.UTF8.GetBytes(entry.Body);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-StrmAssistant-Cache"] = stale ? "stale" : "hit"
                };

                if (stale) headers["X-StrmAssistant-Stale"] = "true";

                return new HttpResponseInfo
                {
                    StatusCode = HttpStatusCode.OK,
                    ContentType = string.IsNullOrEmpty(entry.ContentType)
                        ? "application/json; charset=utf-8"
                        : entry.ContentType,
                    Content = new MemoryStream(bytes),
                    ContentLength = bytes.Length,
                    ResponseUrl = entry.Url,
                    Headers = headers
                };
            }

            private static void LogStaleFallback(string url, string reason)
            {
                Plugin.Instance?.Logger?.Debug("MovieDb cache stale fallback ({0}) - {1}",
                    reason, MovieDbCache.SanitizeUrl(url));
            }

            public Task<HttpResponseInfo> GetResponse(HttpRequestOptions options)
            {
                return SendAsync(options, () => _httpClient.GetResponse(options), true);
            }

            public Task<Stream> Get(HttpRequestOptions options)
            {
                Prepare(options);
                return _httpClient.Get(options);
            }

            public Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod)
            {
                return SendAsync(options, () => _httpClient.SendAsync(options, httpMethod),
                    string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase));
            }

            public Task<HttpResponseInfo> Post(HttpRequestOptions options)
            {
                return SendAsync(options, () => _httpClient.Post(options), false);
            }

            public Task<string> GetTempFile(HttpRequestOptions options)
            {
                Prepare(options);
                return _httpClient.GetTempFile(options);
            }

            public Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options)
            {
                return SendAsync(options, () => _httpClient.GetTempFileResponse(options), true);
            }

            public IDisposable GetConnectionContext(HttpRequestOptions options)
            {
                Prepare(options);
                return _httpClient.GetConnectionContext(options);
            }
        }
    }
}
