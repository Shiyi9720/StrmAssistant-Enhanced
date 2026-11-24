using System;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Renders the running assembly version as the text shown in the UI. The release manifest and
    /// tags use three parts ("1.1.0") while an assembly version has four ("1.1.0.0"),
    /// so trailing zero components are dropped. A non-numeric build tag (Emby builds such as
    /// "public-ip-xxxx") is returned unchanged: it cannot be compared, and callers must treat it
    /// as unreadable rather than as the newest version.
    /// </summary>
    internal static class PluginVersionText
    {
        public static string Normalize(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return string.Empty;

            var text = version.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);

            var parts = text.Split('.');
            if (parts.Length < 4 || parts.Any(part => !part.All(char.IsDigit))) return text;

            var keep = parts.Length;
            while (keep > 3 && parts[keep - 1] == "0") keep--;

            return string.Join(".", parts.Take(keep));
        }
    }
}
