using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Decides whether the external audio files found next to a media file differ from the
    /// external audio streams the item currently carries. Paths are compared by file name,
    /// because Emby may store a resolved stream without the folder (and the JSON sidecar only
    /// keeps file names anyway). Empty paths never participate.
    /// </summary>
    public static class ExternalAudioPlan
    {
        public static bool HasChanged(IEnumerable<string> currentPaths, IEnumerable<string> scannedPaths)
        {
            var current = Normalize(currentPaths);
            var scanned = Normalize(scannedPaths);
            return !current.SetEquals(scanned);
        }

        private static HashSet<string> Normalize(IEnumerable<string> paths)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                var name = FileName(path);
                if (name.Length > 0) result.Add(name);
            }

            return result;
        }

        private static string FileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetFileName(path) ?? string.Empty;
            }
            catch
            {
                return path;
            }
        }
    }
}
