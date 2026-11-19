using System;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Locates the playable main title inside a local BDMV/VIDEO_TS folder. A disc folder can be
    /// a pure shortcut structure: the STREAM folder then contains a .strm file instead of the
    /// m2ts clip, and the actual title lives behind the URL written in that file. Keeping this
    /// filesystem-only logic separate makes the resolution testable without Emby types.
    /// </summary>
    internal static class DiscMainVideoResolver
    {
        /// <summary>
        /// Returns the largest m2ts/mts clip, or - when the folder only carries shortcuts - the
        /// target URL of the largest .strm file. Null when neither exists or the shortcut is
        /// unreadable, so callers can report "no playable video" instead of retrying a broken
        /// structure.
        /// </summary>
        internal static string FindStreamSource(string streamFolder)
        {
            if (string.IsNullOrEmpty(streamFolder) || !Directory.Exists(streamFolder)) return null;

            var clip = GetLargestFile(streamFolder, ".m2ts", ".mts");

            if (clip != null) return clip;

            var shortcut = GetLargestFile(streamFolder, ".strm");

            return shortcut is null ? null : ReadStrmTarget(shortcut);
        }

        internal static string FindDvdSource(string videoTsFolder)
        {
            return string.IsNullOrEmpty(videoTsFolder) || !Directory.Exists(videoTsFolder)
                ? null
                : GetLargestFile(videoTsFolder, ".vob");
        }

        /// <summary>
        /// Reads the first usable line of a .strm file, mirroring Emby's StrmMounter: tabs and
        /// carriage returns are removed, blank lines and '#' comments are skipped. Returns null
        /// when the file is missing, empty or unreadable.
        /// </summary>
        internal static string ReadStrmTarget(string strmPath)
        {
            try
            {
                if (string.IsNullOrEmpty(strmPath) || !File.Exists(strmPath)) return null;

                foreach (var line in File.ReadLines(strmPath))
                {
                    var value = line.Replace("\t", string.Empty).Replace("\r", string.Empty).Trim();

                    if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal)) continue;

                    return StrmUrlHelper.Normalize(value);
                }
            }
            catch (Exception)
            {
                // Unreadable shortcut: treated as "no playable video" by the caller.
            }

            return null;
        }

        private static string GetLargestFile(string folder, params string[] extensions)
        {
            try
            {
                return Directory.EnumerateFiles(folder)
                    .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Exists)
                    .OrderByDescending(f => f.Length)
                    .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                // Missing or unreadable folder: no source.
                return null;
            }
        }
    }
}
