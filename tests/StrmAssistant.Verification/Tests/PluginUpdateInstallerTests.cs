using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Self-update safety rules: the release API digest is the only thing standing between a
    /// truncated/CDN-corrupted download and a broken plugin, and the backup must survive a
    /// failed swap. These cases pin both, plus the retention rule that keeps backups bounded.
    /// </summary>
    internal static class PluginUpdateInstallerTests
    {
        private const string PluginDll = "StrmAssistantLite.dll";

        public static void Run(TestRunner runner)
        {
            runner.Section("plugin update - sha256 digest parsing");

            runner.EqualString("sha256 prefix is stripped and lower-cased",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                PluginUpdateInstaller.NormalizeSha256Digest(
                    "sha256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
            runner.EqualString("bare 64 hex characters are accepted",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                PluginUpdateInstaller.NormalizeSha256Digest(
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            runner.Check("null digest is rejected",
                PluginUpdateInstaller.NormalizeSha256Digest(null) == null);
            runner.Check("empty digest is rejected",
                PluginUpdateInstaller.NormalizeSha256Digest("   ") == null);
            runner.Check("md5 digest is rejected",
                PluginUpdateInstaller.NormalizeSha256Digest("md5:9d9f0738947c69edf3c17b6e91ade7ed") == null);
            runner.Check("truncated digest is rejected",
                PluginUpdateInstaller.NormalizeSha256Digest("sha256:9d9f0738947c69ed") == null);
            runner.Check("non-hex digest is rejected",
                PluginUpdateInstaller.NormalizeSha256Digest(
                    "sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz") == null);

            runner.Section("plugin update - sha256 verification");

            runner.Check("matching hashes pass regardless of case",
                PluginUpdateInstaller.MatchesSha256(
                    "sha256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            runner.Check("different hashes fail",
                !PluginUpdateInstaller.MatchesSha256(
                    "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    "0000000000000000000000000000000000000000000000000000000000000000"));
            runner.Check("a missing expected hash never passes",
                !PluginUpdateInstaller.MatchesSha256(null, "anything"));

            var tempRoot = NewTempDirectory();

            try
            {
                var abcPath = Path.Combine(tempRoot, "abc.txt");
                File.WriteAllText(abcPath, "abc");
                runner.EqualString("file hash matches the known sha256 of 'abc'",
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    PluginUpdateInstaller.ComputeSha256(abcPath));

                var changedPath = Path.Combine(tempRoot, "abcd.txt");
                File.WriteAllText(changedPath, "abd");
                runner.Check("file hash changes when the bytes change",
                    !PluginUpdateInstaller.MatchesSha256(
                        PluginUpdateInstaller.ComputeSha256(abcPath),
                        PluginUpdateInstaller.ComputeSha256(changedPath)));
            }
            finally
            {
                DeleteDirectory(tempRoot);
            }

            runner.Section("plugin update - backup naming");

            runner.EqualString("backup name carries version and UTC timestamp",
                "StrmAssistantLite.dll.bak-1.0.15-20261121T101530123Z",
                PluginUpdateInstaller.BuildBackupFileName(PluginDll, "1.0.15",
                    new DateTimeOffset(2026, 11, 21, 10, 15, 30, 123, TimeSpan.Zero)));
            runner.EqualString("build metadata is sanitised out of the version",
                "StrmAssistantLite.dll.bak-1.0.15-emby.4.10.2.0-20261121T101530123Z",
                PluginUpdateInstaller.BuildBackupFileName(PluginDll, "1.0.15+emby.4.10.2.0",
                    new DateTimeOffset(2026, 11, 21, 10, 15, 30, 123, TimeSpan.Zero)));
            runner.EqualString("path separators cannot escape the backup name",
                "StrmAssistantLite.dll.bak-1.0.15-foo-20261121T101530123Z",
                PluginUpdateInstaller.BuildBackupFileName(PluginDll, "1.0.15/foo",
                    new DateTimeOffset(2026, 11, 21, 10, 15, 30, 123, TimeSpan.Zero)));
            runner.EqualString("missing version falls back to unknown",
                "StrmAssistantLite.dll.bak-unknown-20261121T101530123Z",
                PluginUpdateInstaller.BuildBackupFileName(PluginDll, " ",
                    new DateTimeOffset(2026, 11, 21, 10, 15, 30, 123, TimeSpan.Zero)));

            runner.Section("plugin update - backup retention");

            var names = new List<string>
            {
                PluginDll,
                PluginDll + ".bak-1.0.11-20261101T000000000Z",
                PluginDll + ".bak-1.0.12-20261110T000000000Z",
                PluginDll + ".bak-1.0.13-20261115T000000000Z",
                PluginDll + ".bak-1.0.14-20261118T000000000Z",
                PluginDll + ".bak-1.0.15-20261121T000000000Z",
                PluginDll + ".bak-1.0.15-not-a-timestamp",
                PluginDll + ".bak-1.0.15-garbage-2026",
                "Other.dll.bak-1.0.0-20260101T000000000Z"
            };

            var stale = PluginUpdateInstaller.FindBackupsToDelete(names, PluginDll, 3);
            runner.Check("retention keeps three and drops two oldest",
                stale.Count == 2 && stale.Contains(PluginDll + ".bak-1.0.11-20261101T000000000Z") &&
                stale.Contains(PluginDll + ".bak-1.0.12-20261110T000000000Z"),
                string.Join(", ", stale));
            runner.Check("the live assembly is never treated as a backup",
                !stale.Contains(PluginDll));
            runner.Check("unparsable names are left alone",
                !stale.Any(name => name.Contains("not-a-timestamp") || name.EndsWith("-2026")));
            runner.Check("other plugins' backups are left alone",
                !stale.Contains("Other.dll.bak-1.0.0-20260101T000000000Z"));
            runner.Check("retention zero removes every known backup",
                PluginUpdateInstaller.FindBackupsToDelete(
                    names.Where(name => name.StartsWith(PluginDll + ".bak-1.0.1", StringComparison.Ordinal)),
                    PluginDll, 0).Count == 5);

            runner.Section("plugin update - install and rollback");

            var installRoot = NewTempDirectory();

            try
            {
                var target = Path.Combine(installRoot, PluginDll);
                var backup = Path.Combine(installRoot, PluginDll + ".bak-1.0.15-20261121T000000000Z");
                var download = Path.Combine(installRoot, PluginDll + ".download");

                File.WriteAllText(target, "old-assembly");
                File.WriteAllText(download, "verified-new-assembly");

                runner.Check("verified download replaces the live assembly",
                    PluginUpdateInstaller.TryInstall(download, target, backup, out var installError),
                    installError);
                runner.EqualString("live assembly is the new bytes", "verified-new-assembly",
                    File.ReadAllText(target));
                runner.EqualString("backup holds the old bytes", "old-assembly", File.ReadAllText(backup));
                runner.Check("download file is moved away, not copied", !File.Exists(download));

                var missingDownload = Path.Combine(installRoot, "missing.download");
                File.WriteAllText(target, "still-old");
                runner.Check("a missing download does not touch the live assembly",
                    !PluginUpdateInstaller.TryInstall(missingDownload, target,
                        Path.Combine(installRoot, "missing.bak"), out _));
                runner.EqualString("live assembly unchanged after missing download", "still-old",
                    File.ReadAllText(target));

                var freshTarget = Path.Combine(installRoot, "Fresh.dll");
                var freshDownload = Path.Combine(installRoot, "fresh.download");
                var freshBackup = Path.Combine(installRoot, "fresh.bak");
                File.WriteAllText(freshDownload, "first-install");
                runner.Check("a fresh install needs no backup",
                    PluginUpdateInstaller.TryInstall(freshDownload, freshTarget, freshBackup, out var freshError),
                    freshError);
                runner.EqualString("fresh install writes the verified bytes", "first-install",
                    File.ReadAllText(freshTarget));
                runner.Check("fresh install creates no backup file", !File.Exists(freshBackup));

                var moveFailureTarget = Path.Combine(installRoot, "MoveFail.dll");
                var moveFailureDownload = Path.Combine(installRoot, "movefail.download");
                var moveFailureBackup = Path.Combine(installRoot, "movefail.bak");
                File.WriteAllText(moveFailureTarget, "old-movefail");
                File.WriteAllText(moveFailureDownload, "new-movefail");

                var moveFailed = !PluginUpdateInstaller.TryInstall(moveFailureDownload, moveFailureTarget,
                    moveFailureBackup, (source, destination) => throw new IOException("simulated move failure"),
                    out var moveError);
                runner.Check("a failed move reports failure", moveFailed);
                runner.Check("a failed move reports why", !string.IsNullOrEmpty(moveError));
                runner.EqualString("a failed move leaves the live assembly untouched", "old-movefail",
                    File.ReadAllText(moveFailureTarget));

                var halfMoveTarget = Path.Combine(installRoot, "HalfMove.dll");
                var halfMoveDownload = Path.Combine(installRoot, "halfmove.download");
                var halfMoveBackup = Path.Combine(installRoot, "halfmove.bak");
                File.WriteAllText(halfMoveTarget, "old-halfmove");
                File.WriteAllText(halfMoveDownload, "new-halfmove");

                var halfMoveFailed = !PluginUpdateInstaller.TryInstall(halfMoveDownload, halfMoveTarget,
                    halfMoveBackup, (source, destination) =>
                    {
                        File.Delete(destination);
                        throw new IOException("simulated failure after deleting the target");
                    }, out var halfMoveError);
                runner.Check("a move that destroys the target reports failure", halfMoveFailed);
                runner.EqualString("rollback restores the old assembly", "old-halfmove",
                    File.ReadAllText(halfMoveTarget));
                runner.Check("rollback is mentioned in the error",
                    halfMoveError != null && halfMoveError.Contains("rolled back"));

                var collisionTarget = Path.Combine(installRoot, "Collision.dll");
                var collisionDownload = Path.Combine(installRoot, "collision.download");
                var collisionBackup = Path.Combine(installRoot, "collision.bak");
                File.WriteAllText(collisionTarget, "old-collision");
                File.WriteAllText(collisionDownload, "new-collision");
                File.WriteAllText(collisionBackup, "existing-backup");

                runner.Check("an existing backup name aborts the install",
                    !PluginUpdateInstaller.TryInstall(collisionDownload, collisionTarget,
                        collisionBackup, out _));
                runner.EqualString("a backup collision leaves the live assembly untouched", "old-collision",
                    File.ReadAllText(collisionTarget));
                runner.EqualString("a backup collision does not overwrite the existing backup",
                    "existing-backup", File.ReadAllText(collisionBackup));
                runner.Check("a backup collision keeps the verified download for inspection",
                    File.Exists(collisionDownload));
            }
            finally
            {
                DeleteDirectory(installRoot);
            }

            runner.Section("plugin update - pruning on disk");

            var pruneRoot = NewTempDirectory();

            try
            {
                File.WriteAllText(Path.Combine(pruneRoot, PluginDll), "live");
                File.WriteAllText(Path.Combine(pruneRoot, "unrelated.txt"), "keep me");

                for (var i = 1; i <= 5; i++)
                {
                    File.WriteAllText(Path.Combine(pruneRoot,
                        PluginDll + ".bak-1.0.1" + i + "-20261101T00000000" + i + "Z"), "backup");
                }

                var removed = PluginUpdateInstaller.PruneBackups(pruneRoot, PluginDll, 3, out var pruneError);
                runner.Check("pruning reports no error", pruneError == null, pruneError);
                runner.EqualLong("pruning removes the two oldest backups", 2, removed);

                var remaining = Directory.GetFiles(pruneRoot).Select(Path.GetFileName).ToList();
                runner.EqualLong("three backups remain", 3,
                    remaining.Count(name => name.StartsWith(PluginDll + ".bak-", StringComparison.Ordinal)));
                runner.Check("the live assembly survives pruning",
                    File.Exists(Path.Combine(pruneRoot, PluginDll)));
                runner.Check("unrelated files survive pruning",
                    File.Exists(Path.Combine(pruneRoot, "unrelated.txt")));
                runner.Check("the newest backups survive pruning",
                    remaining.Contains(PluginDll + ".bak-1.0.13-20261101T000000003Z") &&
                    remaining.Contains(PluginDll + ".bak-1.0.14-20261101T000000004Z") &&
                    remaining.Contains(PluginDll + ".bak-1.0.15-20261101T000000005Z"),
                    string.Join(", ", remaining));
            }
            finally
            {
                DeleteDirectory(pruneRoot);
            }
        }

        private static string NewTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "strm-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception)
            {
                // Test cleanup only.
            }
        }
    }
}
