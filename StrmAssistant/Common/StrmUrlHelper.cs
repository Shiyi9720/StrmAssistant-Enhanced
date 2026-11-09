using System;
using System.Text;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Normalizes the URL stored in a .strm file so ffmpeg, ffprobe and HttpClient receive a
    /// requestable URL. The part before the first '?' keeps its reserved characters and any
    /// existing %XX escapes. A suffix that starts with '/' is the shortcut target pattern used by
    /// the downloader in production: it is treated as path segments, so spaces, CJK text,
    /// brackets and other data characters are percent-encoded while '/' stays a separator.
    /// Ordinary query suffixes keep their structural characters and only get illegal characters
    /// encoded. Normalize is idempotent and leaves non-http(s) input untouched.
    /// </summary>
    public static class StrmUrlHelper
    {
        private const string Reserved = ":/?#[]@!$&'()*+,;=";

        public static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            var value = url.Trim();

            if (!IsHttpUrl(value)) return url;

            var question = value.IndexOf('?');

            if (question < 0) return EncodeComponent(value, false);

            var basis = EncodeComponent(value.Substring(0, question), false);
            var suffix = value.Substring(question + 1);

            // A leading '/' after '?' is the path-like shortcut target, not a query string.
            // Encode every segment as data so '#', '&' and '?' inside a file name cannot be
            // mistaken for URL structure. The '/' separators themselves are preserved.
            suffix = suffix.Length > 0 && suffix[0] == '/'
                ? EncodePathSuffix(suffix)
                : EncodeComponent(suffix, false);

            return basis + "?" + suffix;
        }

        private static string EncodePathSuffix(string suffix)
        {
            var builder = new StringBuilder(suffix.Length);
            var start = 0;

            while (start <= suffix.Length)
            {
                var slash = suffix.IndexOf('/', start);
                var end = slash < 0 ? suffix.Length : slash;

                builder.Append(EncodeComponent(suffix.Substring(start, end - start), true));

                if (slash < 0) break;

                builder.Append('/');
                start = slash + 1;
            }

            return builder.ToString();
        }

        private static string EncodeComponent(string value, bool encodeReserved)
        {
            var builder = new StringBuilder(value.Length);

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];

                if (c == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
                {
                    builder.Append(c).Append(value[i + 1]).Append(value[i + 2]);
                    i += 2;
                    continue;
                }

                if (c < 0x80)
                {
                    if (IsAllowedAscii(c, encodeReserved))
                    {
                        builder.Append(c);
                    }
                    else
                    {
                        AppendEncoded(builder, (byte)c);
                    }

                    continue;
                }

                var length = char.IsHighSurrogate(c) && i + 1 < value.Length &&
                             char.IsLowSurrogate(value[i + 1])
                    ? 2
                    : 1;
                var bytes = Encoding.UTF8.GetBytes(value.Substring(i, length));

                foreach (var b in bytes)
                {
                    AppendEncoded(builder, b);
                }

                i += length - 1;
            }

            return builder.ToString();
        }

        private static bool IsAllowedAscii(char c, bool encodeReserved)
        {
            if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' ||
                c == '-' || c == '.' || c == '_' || c == '~')
            {
                return true;
            }

            return !encodeReserved && Reserved.IndexOf(c) >= 0;
        }

        private static bool IsHex(char c)
        {
            return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
        }

        private static void AppendEncoded(StringBuilder builder, byte value)
        {
            const string hex = "0123456789ABCDEF";

            builder.Append('%').Append(hex[value >> 4]).Append(hex[value & 0x0F]);
        }

        private static bool IsHttpUrl(string value)
        {
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
