using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Filesystem side of the self-update: normalise the sha256 published by the release API,
    /// keep a small set of backups of the live plugin assembly, and swap a verified download in
    /// with a rollback path. Deliberately free of Emby types so the rules can be covered by the
    /// verification project.
    /// </summary>
    internal static class PluginUpdateInstaller
    {
        internal const int DefaultBackupRetention = 3;

        private const string DigestPrefix = "sha256:";
        private const string BackupMarker = ".bak-";
        private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

        /// <summary>
        /// Accepts "sha256:&lt;hex&gt;" (what the GitHub release asset API returns) as well as a
        /// bare 64-character hex digest. Anything else - a different algorithm, a truncated
        /// value, non-hex characters - is treated as "no checksum available" so callers can
        /// refuse to install instead of silently skipping verification.
        /// </summary>
        internal static string NormalizeSha256Digest(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;

            var value = digest.Trim();

            if (value.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(DigestPrefix.Length);
            }

            if (value.Length != 64) return null;

            foreach (var c in value)
            {
                if (!Uri.IsHexDigit(c)) return null;
            }

            return value.ToLowerInvariant();
        }

        internal static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return ToHex(sha256.ComputeHash(stream));
            }
        }

        internal static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static bool MatchesSha256(string expectedDigest, string actualHex)
        {
            var expected = NormalizeSha256Digest(expectedDigest);
            var actual = NormalizeSha256Digest(actualHex);

            return expected != null && actual != null &&
                   string.Equals(expected, actual, StringComparison.Ordinal);
        }

        /// <summary>
        /// Backup names carry the version that was replaced and the UTC timestamp, e.g.
        /// <c>StrmAssistantLite.dll.bak-1.0.15-20261121T101530123Z</c>. The timestamp is last so
        /// pruning can recover the age from the name alone.
        /// </summary>
        internal static string BuildBackupFileName(string pluginFileName, string version,
            DateTimeOffset timestampUtc)
        {
            return pluginFileName + BackupMarker + SanitizeVersion(version) + "-" +
                   timestampUtc.UtcDateTime.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns the backups older than the newest <paramref name="keep"/>. Only files that
        /// follow this class's own naming scheme are considered, so unrelated files (the live
        /// DLL, the .deps folder, hands-off custom backups) are never deleted.
        /// </summary>
        internal static List<string> FindBackupsToDelete(IEnumerable<string> fileNames,
            string pluginFileName, int keep)
        {
            var prefix = pluginFileName + BackupMarker;
            var parsed = new List<KeyValuePair<DateTime, string>>();

            foreach (var name in fileNames)
            {
                if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryParseBackupTimestamp(name.Substring(prefix.Length), out var timestamp))
                {
                    continue;
                }

                parsed.Add(new KeyValuePair<DateTime, string>(timestamp, name));
            }

            parsed.Sort((left, right) => right.Key.CompareTo(left.Key));

            if (keep < 0) keep = 0;

            return parsed.Skip(keep).Select(pair => pair.Value).ToList();
        }

        /// <summary>
        /// Installs an already verified file. The current assembly is copied to
        /// <paramref name="backupFilePath"/> first; only when that copy succeeds is the verified
        /// download moved over it. A failure in the move restores the backup, so the live path
        /// is never left missing or half-written (the download lives in its own file until the
        /// rename). Returns false with a message instead of throwing so the caller can log,
        /// notify and mark the task failed.
        /// </summary>
        internal static bool TryInstall(string downloadedFilePath, string targetFilePath,
            string backupFilePath, out string error)
        {
            return TryInstall(downloadedFilePath, targetFilePath, backupFilePath,
                ReplaceVerifiedFile, out error);
        }

        /// <summary>
        /// <see cref="File.Replace"/> renames the verified download over the live assembly in one
        /// step on Unix; it is the atomic primitive here. A fresh install (no target yet) is a
        /// plain move.
        /// </summary>
        private static void ReplaceVerifiedFile(string source, string destination)
        {
            if (File.Exists(destination))
            {
                File.Replace(source, destination, null);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        internal static bool TryInstall(string downloadedFilePath, string targetFilePath,
            string backupFilePath, Action<string, string> moveVerifiedFile, out string error)
        {
            error = null;
            string backupCreated = null;

            try
            {
                if (!File.Exists(downloadedFilePath))
                {
                    throw new FileNotFoundException("Verified download is missing", downloadedFilePath);
                }

                if (File.Exists(targetFilePath))
                {
                    // No overwrite: a colliding backup name aborts the install rather than
                    // destroying the only copy of the version we are about to replace.
                    File.Copy(targetFilePath, backupFilePath, false);
                    backupCreated = backupFilePath;
                }

                moveVerifiedFile(downloadedFilePath, targetFilePath);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;

                if (backupCreated != null && !File.Exists(targetFilePath))
                {
                    try
                    {
                        File.Copy(backupCreated, targetFilePath, true);
                        error += " (rolled back from backup)";
                    }
                    catch (Exception rollbackError)
                    {
                        error += " (rollback failed: " + rollbackError.Message + ")";
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Deletes backups beyond the retention limit. Returns the number removed; a failure is
        /// reported through <paramref name="error"/> and is never fatal - the update already
        /// succeeded by then.
        /// </summary>
        internal static int PruneBackups(string pluginsPath, string pluginFileName, int keep,
            out string error)
        {
            error = null;

            try
            {
                if (!Directory.Exists(pluginsPath)) return 0;

                var names = Directory.EnumerateFiles(pluginsPath).Select(Path.GetFileName);
                var stale = FindBackupsToDelete(names, pluginFileName, keep);

                foreach (var name in stale)
                {
                    File.Delete(Path.Combine(pluginsPath, name));
                }

                return stale.Count;
            }
            catch (Exception e)
            {
                error = e.Message;
                return 0;
            }
        }

        private static string SanitizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "unknown";

            var chars = version.Trim().Select(c =>
                char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '-').ToArray();
            var sanitized = new string(chars).Trim('-');

            return sanitized.Length == 0 ? "unknown" : sanitized;
        }

        private static bool TryParseBackupTimestamp(string suffix, out DateTime timestamp)
        {
            timestamp = default;

            var separator = suffix.LastIndexOf('-');
            if (separator < 0 || separator == suffix.Length - 1) return false;

            return DateTime.TryParseExact(suffix.Substring(separator + 1), BackupTimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }
    }
}
