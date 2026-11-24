using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Guards the poster proxy. Douban's image CDN answers 418 to a request without a
    /// Referer/UA, so the plugin relays covers through its own endpoint; that endpoint must never
    /// become a generic SSRF surface, hence the strict host allow-list.
    /// </summary>
    public static class DiscoverImageProxy
    {
        public const string Route = "/StrmAssistant/DiscoverImage";

        /// <summary>Accepts only http(s) URLs on Douban's image/document hosts.</summary>
        public static bool TryNormalize(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

            var host = parsed.Host ?? string.Empty;
            if (host.Length == 0) return false;

            if (!host.Equals("douban.com", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".douban.com", StringComparison.OrdinalIgnoreCase) &&
                !host.Equals("doubanio.com", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".doubanio.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uri = parsed;
            return true;
        }
    }
}
