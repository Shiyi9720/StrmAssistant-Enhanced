using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Emby deletes only the .strm file itself. Deep Delete additionally removes the media file
    /// or remote URL the .strm points at, which is what users expect for a strm library
    /// ("deleted in Emby but still on disk" otherwise).
    ///
    /// The hook is attached to the core delete overload, which every other LibraryManager
    /// delete overload funnels through. It captures the strm targets in a prefix (the files
    /// still exist at that point) and performs the actual source deletion in a postfix, so a
    /// failed Emby delete never leaves the source file missing while the item stays in Emby.
    /// All source deletion work is wrapped in try/catch and can never affect Emby's own flow.
    /// </summary>
    public class DeepDelete : PatchBase<DeepDelete>
    {
        private static MethodInfo _deleteItem;
        private static IFileSystem _fileSystem;
        private static IHttpClient _httpClient;

        private sealed class DeleteBatch
        {
            public DateTime StartedUtc { get; } = DateTime.UtcNow;
            public HashSet<string> Targets { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Keeps the per-delete-batch deduplication across the several DeleteItem calls of one
        // Emby DeleteItems request, without leaking forever in long-lived background flows.
        private static readonly AsyncLocal<DeleteBatch> Batch = new AsyncLocal<DeleteBatch>();

        public DeepDelete()
        {
            Initialize();

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                Plugin.Instance.Logger.Warn("DeepDelete - unavailable: " + PatchTracker.FailureReason);

                return;
            }

            if (Plugin.Instance.GetPluginOptions().GeneralOptions.DeepDelete)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var libraryManager =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Library.LibraryManager");

            _deleteItem = libraryManager?.GetMethod("DeleteItem",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) }, null);

            if (_deleteItem is null)
            {
                throw new MissingMethodException(
                    "Emby.Server.Implementations.Library.LibraryManager.DeleteItem(BaseItem, DeleteOptions, BaseItem, bool)");
            }

            _fileSystem = Plugin.Instance.FileSystem;
            _httpClient = ProxyApi.Wrap(Plugin.Instance.HttpClient);
        }

        protected override void Prepare(bool apply)
        {
            var specs = new[]
            {
                new PatchSpec("DeleteItem", _deleteItem, prefix: nameof(DeleteItemPrefix),
                    postfix: nameof(DeleteItemPostfix))
            };

            if (apply)
            {
                PatchAll(PatchTracker, specs);
                return;
            }

            foreach (var spec in specs)
            {
                if (spec.Target is null) continue;

                PatchUnpatch(PatchTracker, false, spec.Target, spec.Prefix, spec.Postfix, spec.Transpiler,
                    spec.Finalizer, true);
            }
        }

        [HarmonyPrefix]
        private static void DeleteItemPrefix(BaseItem item, DeleteOptions options, ref List<string> __state)
        {
            __state = null;

            try
            {
                if (item is null || options is null || !options.DeleteFileLocation) return;

                if (Plugin.Instance?.GetPluginOptions()?.GeneralOptions.DeepDelete != true) return;

                var candidates = GetStrmItems(item);

                if (candidates.Count == 0) return;

                var batch = GetBatchTargets();
                var targets = new List<string>();

                foreach (var candidate in candidates)
                {
                    var strmPath = candidate.Path;
                    var target = ReadStrmTarget(strmPath);

                    if (string.IsNullOrWhiteSpace(target))
                    {
                        Plugin.Instance.Logger.Warn(
                            $"DeepDelete - Empty or unreadable strm, skipped: {strmPath}");

                        continue;
                    }

                    var resolved = ResolveTarget(target, strmPath);

                    if (string.IsNullOrEmpty(resolved)) continue;

                    if (!batch.Targets.Add(resolved))
                    {
                        Plugin.Instance.Logger.Info(
                            $"DeepDelete - Duplicate target in the same delete batch, skipped: {resolved}");

                        continue;
                    }

                    targets.Add(resolved);

                    var rawTarget = target.Trim();
                    var targetDetail = string.Equals(resolved, rawTarget, StringComparison.Ordinal)
                        ? resolved
                        : $"{resolved} (raw: {rawTarget})";

                    Plugin.Instance.Logger.Info(
                        $"DeepDelete - Item: {candidate.Name ?? candidate.InternalId.ToString()} | " +
                        $"Strm: {strmPath} | Target: {targetDetail}");
                }

                if (targets.Count > 0)
                {
                    __state = targets;
                }
            }
            catch (Exception e)
            {
                // Reading a strm or enumerating a folder must never break Emby's delete.
                LogFailure("capture", e);
            }
        }

        [HarmonyPostfix]
        private static void DeleteItemPostfix(List<string> __state)
        {
            if (__state is null || __state.Count == 0) return;

            foreach (var target in __state)
            {
                try
                {
                    if (TryGetHttpUrl(target, out var url))
                    {
                        DeleteRemote(url);
                    }
                    else
                    {
                        DeleteLocal(target);
                    }
                }
                catch (Exception e)
                {
                    LogFailure("delete " + target, e);
                }
            }
        }

        private static List<BaseItem> GetStrmItems(BaseItem item)
        {
            if (item is Video && IsStrmPath(item.Path))
            {
                return new List<BaseItem> { item };
            }

            // Deleting a Series/Season deletes the folder, but Emby never calls DeleteItem for
            // each episode. Cascade explicitly so every episode's source is removed as well.
            if (item is Series || item is Season)
            {
                return ((Folder)item)
                    .GetItemList(new InternalItemsQuery { Recursive = true, HasPath = true })
                    .Where(i => i is Video && IsStrmPath(i.Path))
                    .ToList();
            }

            return new List<BaseItem>();
        }

        private static bool IsStrmPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadStrmTarget(string strmPath)
        {
            // Read through Emby's file system abstraction so SMB/credential backed libraries
            // resolve the same way they do for Emby's own scanner.
            var lines = _fileSystem.ReadAllLines(strmPath);

            if (lines is null) return null;

            foreach (var line in lines)
            {
                var target = line.Trim().Trim('\uFEFF');

                if (target.Length > 0) return target;
            }

            return null;
        }

        private static string ResolveTarget(string target, string strmPath)
        {
            var value = target.Trim();

            if (TryGetHttpUrl(value, out var url))
            {
                return url;
            }

            if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out var fileUri))
            {
                return fileUri.LocalPath;
            }

            // A relative target would otherwise be resolved against Emby's working directory,
            // which could point at an unrelated file. Resolve it next to the strm instead.
            var isUncPath = value.StartsWith("\\\\", StringComparison.Ordinal);

            if (!isUncPath && !Path.IsPathRooted(value))
            {
                var strmDirectory = Path.GetDirectoryName(strmPath);

                if (!string.IsNullOrEmpty(strmDirectory))
                {
                    value = Path.GetFullPath(Path.Combine(strmDirectory, value));
                }
            }

            return value;
        }

        private static bool TryGetHttpUrl(string target, out string url)
        {
            url = null;

            if (string.IsNullOrWhiteSpace(target)) return false;

            if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            // Some WebDAV/strm setups append routing data after a '?'. Keep only the path
            // (and the fragment is never sent to a server either).
            var cut = target.IndexOfAny(new[] { '?', '#' });

            url = cut >= 0 ? target.Substring(0, cut) : target;

            return url.Length > 0;
        }

        private static void DeleteLocal(string path)
        {
            try
            {
                if (!_fileSystem.FileExists(path))
                {
                    Plugin.Instance.Logger.Info($"DeepDelete - Local target already absent: {path}");

                    return;
                }

                _fileSystem.DeleteFile(path, false);

                Plugin.Instance.Logger.Info($"DeepDelete - Local target deleted: {path}");
            }
            catch (FileNotFoundException)
            {
                Plugin.Instance.Logger.Info($"DeepDelete - Local target already absent: {path}");
            }
            catch (DirectoryNotFoundException)
            {
                Plugin.Instance.Logger.Info($"DeepDelete - Local target directory already absent: {path}");
            }
            catch (Exception e)
            {
                LogFailure("local target " + path, e);
            }
        }

        private static void DeleteRemote(string url)
        {
            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = CancellationToken.None,
                    TimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds,
                    UserAgent = Plugin.Instance.UserAgent
                };

                var response = _httpClient.SendAsync(options, "DELETE")
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                var statusCode = (int)response.StatusCode;

                // 404/410 mean the remote side is already gone, which is the desired end state.
                if ((statusCode >= 200 && statusCode < 300) || statusCode == 404 || statusCode == 410)
                {
                    Plugin.Instance.Logger.Info(
                        $"DeepDelete - Remote target deleted: {url} (HTTP {statusCode})");
                }
                else
                {
                    Plugin.Instance.Logger.Error(
                        $"DeepDelete - Remote delete failed: {url} (HTTP {statusCode})");
                }
            }
            catch (Exception e)
            {
                LogFailure("remote target " + url, e);
            }
        }

        private static DeleteBatch GetBatchTargets()
        {
            var batch = Batch.Value;

            if (batch is null || DateTime.UtcNow - batch.StartedUtc > TimeSpan.FromMinutes(5))
            {
                batch = new DeleteBatch();
                Batch.Value = batch;
            }

            return batch;
        }

        private static void LogFailure(string action, Exception e)
        {
            try
            {
                var logger = Plugin.Instance?.Logger;

                logger?.Error($"DeepDelete - Failed to {action}: {e.GetType().Name}: {e.Message}");

                if (Plugin.Instance?.DebugMode == true)
                {
                    logger?.Debug(e.StackTrace);
                }
            }
            catch
            {
                // Never let logging break the delete path.
            }
        }
    }
}
