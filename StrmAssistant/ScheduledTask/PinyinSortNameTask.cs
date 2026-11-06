using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// Writes the pinyin initials of Chinese titles into the item sort title. Only movies,
    /// series, seasons and episodes are touched. A sort title is rewritten only when it is still
    /// untouched (blank or identical to the name) and its field is not locked, so a sort title
    /// configured by the user is never overwritten.
    /// </summary>
    public class PinyinSortNameTask : IScheduledTask
    {
        private const int BatchSize = 200;
        private const int BatchDelayMilliseconds = 100;

        private static readonly string[] ItemTypes =
        {
            nameof(Movie), nameof(Series), nameof(Season), nameof(Episode)
        };

        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly ILibraryManager _libraryManager;
        private ILogger _logger => Plugin.Instance?.Logger;

        public PinyinSortNameTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.GetPluginOptions().ExperienceEnhanceOptions?.PinyinSortOptions;

            if (options?.EnablePinyinSortName != true)
            {
                _logger?.Info("PinyinSort - Disabled, skipped");
                progress?.Report(100);
                return;
            }

            var scope = options.LibraryScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(l => !LibraryApi.ExcludedCollectionTypes.Contains(l.CollectionType))
                .Where(l => scope is null || scope.Count == 0 ||
                            scope.Contains(l.ItemId) || scope.Contains(l.Id) || scope.Contains(l.Guid))
                .ToList();

            var scopeDescription = scope is null || scope.Count == 0
                ? "ALL"
                : libraries.Any()
                    ? string.Join(", ", libraries.Select(l => l.Name))
                    : "NONE";
            _logger?.Info("PinyinSort - LibraryScope: " + scopeDescription);

            var itemIds = new List<long>();

            foreach (var library in libraries)
            {
                BaseItem libraryFolder = null;

                if (Guid.TryParse(library.Guid, out var libraryGuid))
                {
                    libraryFolder = _libraryManager.GetItemById(libraryGuid);
                }

                if (libraryFolder is null && !string.IsNullOrEmpty(library.ItemId))
                {
                    libraryFolder = _libraryManager.GetItemById(library.ItemId);
                }

                if (libraryFolder is null)
                {
                    _logger?.Warn("PinyinSort - Library not found: " + library.Name);
                    continue;
                }

                var ids = _libraryManager.GetInternalItemIds(new InternalItemsQuery
                {
                    AncestorIds = new[] { libraryFolder.InternalId },
                    Recursive = true,
                    IncludeItemTypes = ItemTypes
                });

                _logger?.Info($"PinyinSort - {library.Name}: {ids.Length} item(s)");
                itemIds.AddRange(ids);
            }

            itemIds = itemIds.Distinct().ToList();
            var total = itemIds.Count;

            if (total == 0)
            {
                _logger?.Info("PinyinSort - No items in scope");
                progress?.Report(100);
                return;
            }

            _logger?.Info("PinyinSort - Number of items: " + total);

            var processed = 0;
            var updated = 0;
            var skippedLocked = 0;
            var skippedNonChinese = 0;
            var skippedCustomized = 0;
            var skippedUnchanged = 0;
            var skippedMissing = 0;
            var failed = 0;

            for (var offset = 0; offset < itemIds.Count; offset += BatchSize)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.Info("PinyinSort - Scheduled Task Cancelled");
                    return;
                }

                var batchIds = itemIds.Skip(offset).Take(BatchSize).ToArray();
                var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = batchIds });

                processed += batchIds.Length;
                skippedMissing += Math.Max(0, batchIds.Length - items.Length);

                var changed = new List<BaseItem>();

                foreach (var item in items)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger?.Info("PinyinSort - Scheduled Task Cancelled");
                        return;
                    }

                    try
                    {
                        if (item.IsFieldLocked(MetadataFields.SortName))
                        {
                            skippedLocked++;
                            continue;
                        }

                        var name = item.Name;

                        if (string.IsNullOrWhiteSpace(name) || !SearchTextHelper.HasChinese(name))
                        {
                            skippedNonChinese++;
                            continue;
                        }

                        var currentSortName = item.SortName;

                        // A sort title that differs from the name was set by the user (or by
                        // another tool); leave it alone.
                        if (!string.IsNullOrEmpty(currentSortName) &&
                            !string.Equals(currentSortName, name, StringComparison.Ordinal))
                        {
                            skippedCustomized++;
                            continue;
                        }

                        var sortName = BuildSortName(name);

                        if (string.IsNullOrEmpty(sortName) ||
                            string.Equals(sortName, name, StringComparison.Ordinal) ||
                            string.Equals(sortName, currentSortName, StringComparison.Ordinal))
                        {
                            skippedUnchanged++;
                            continue;
                        }

                        item.SortName = sortName;
                        changed.Add(item);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        _logger?.Error($"PinyinSort - Item failed: {item?.Name}");
                        _logger?.Error(e.Message);
                        _logger?.Debug(e.StackTrace);
                    }
                }

                if (changed.Count > 0)
                {
                    try
                    {
                        _libraryManager.UpdateItems(changed, null, ItemUpdateType.MetadataEdit, false,
                            false, null, cancellationToken);
                        updated += changed.Count;
                        _logger?.Info($"PinyinSort - Updated {updated}/{total}");
                    }
                    catch (Exception e)
                    {
                        failed += changed.Count;
                        _logger?.Error("PinyinSort - Saving batch failed");
                        _logger?.Error(e.Message);
                        _logger?.Debug(e.StackTrace);
                    }
                }

                progress?.Report(Math.Min(100.0, processed * 100.0 / total));

                if (offset + BatchSize < itemIds.Count)
                {
                    await Task.Delay(BatchDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger?.Info("PinyinSort - Scheduled Task Complete");
            _logger?.Info($"PinyinSort - Updated: {updated}, Locked: {skippedLocked}, " +
                          $"Customized: {skippedCustomized}, Non-Chinese: {skippedNonChinese}, " +
                          $"Unchanged: {skippedUnchanged}, Missing: {skippedMissing}, Failed: {failed}");

            progress?.Report(100.0);
        }

        /// <summary>
        /// Chinese runs become pinyin initials (lower case, the helper's default reading for
        /// polyphonic characters); other letters and digits keep their original form. All other
        /// punctuation is dropped and whitespace is collapsed.
        /// </summary>
        private static string BuildSortName(string name)
        {
            var builder = new StringBuilder(name.Length);
            var chinese = new StringBuilder();

            foreach (var c in name)
            {
                if (SearchTextHelper.IsCjk(c))
                {
                    chinese.Append(c);
                    continue;
                }

                if (chinese.Length > 0)
                {
                    builder.Append(SearchTextHelper.ToPinyinInitials(chinese.ToString()));
                    chinese.Clear();
                }

                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)) builder.Append(c);
            }

            if (chinese.Length > 0)
            {
                builder.Append(SearchTextHelper.ToPinyinInitials(chinese.ToString()));
            }

            return WhitespaceRegex.Replace(builder.ToString(), " ").Trim();
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "PinyinSortNameTask";

        public string Description => Resources.ResourceManager.GetString(
            "PinyinSortNameTask_Description_Convert_Chinese_titles_of_movies_series_seasons_and_episodes_to_pinyin_initials_and_write_them_to_the_sort_title",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString(
            "PinyinSortNameTask_Name_Generate_Pinyin_Sort_Titles", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
