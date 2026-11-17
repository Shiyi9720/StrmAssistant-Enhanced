using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    /// <summary>
    /// The single implementation of the "library scope" logic that used to be copy-pasted into the
    /// media info, intro skip, fingerprint, video thumbnail and playback monitor code paths.
    ///
    /// Semantics that are deliberately preserved (check every caller before changing them):
    ///  - A scope value is a comma separated list of library ids; an empty or absent value means
    ///    "all libraries".
    ///  - "-1" is the favorites pseudo library. It matches no virtual folder, so callers deal with
    ///    favorites themselves, see <see cref="IncludesFavorites"/>.
    ///  - Scope ids are validated against the item database first (<c>GetValidLibraryIds</c>),
    ///    because the saved option can still point at a library that was removed afterwards.
    ///  - Every scope path keeps a trailing directory separator, so "/media/TV" cannot match
    ///    "/media/TVShows".
    ///  - A null or empty item path is "not in scope" rather than an exception.
    ///  - Which libraries are accepted stays with the caller (media info skips books/photos/...,
    ///    intro skip takes TV libraries, video thumbnail takes libraries with chapter images).
    ///  - The de-duplication key is *not* shared on purpose: the media info and video thumbnail
    ///    queues de-duplicate by InternalId, the fingerprint queues by full path with
    ///    OrdinalIgnoreCase. Those two keys keep different items when a shortcut and its target,
    ///    or two versions, share a path, so unifying them would change which items are processed.
    /// </summary>
    internal static class LibraryScopeResolver
    {
        /// <summary>Scope id of the favorites pseudo library.</summary>
        internal const string FavoritesScopeId = "-1";

        // A corrupt scope must select nothing. Query callers treat an empty path list as "no
        // restriction", so the resolver returns one path that no real item path can start with
        // (scope paths are absolute, this one is not).
        internal const string MalformedScopePath = "<malformed-scope>";

        private static readonly char[] ScopeSeparators = { ',' };

        /// <summary>
        /// Splits a scope option value into its non-empty parts. Never returns null: the copies
        /// that used the scope directly would have thrown on a null option.
        /// </summary>
        internal static string[] ParseScopeIds(string scope) =>
            scope?.Split(ScopeSeparators, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

        /// <summary>
        /// True when the scope explicitly asks for favorites. An empty scope does not: the callers
        /// that also want favorites for "all libraries" add the empty check themselves.
        /// </summary>
        internal static bool IncludesFavorites(ICollection<string> scopeIds) =>
            scopeIds != null && scopeIds.Contains(FavoritesScopeId);

        /// <summary>True when the scope selects at least one real library.</summary>
        internal static bool SelectsLibraries(ICollection<string> scopeIds) =>
            scopeIds != null && scopeIds.Any(id => id != FavoritesScopeId);

        /// <summary>
        /// True when the scope is a non-empty list of nothing but the favorites pseudo library,
        /// i.e. favorites only. An empty scope ("all libraries") is not favorites only.
        /// </summary>
        internal static bool IsFavoritesOnly(ICollection<string> scopeIds) =>
            scopeIds != null && scopeIds.Count > 0 && !SelectsLibraries(scopeIds);

        internal static string EnsureTrailingSeparator(string path)
        {
            if (path is null) return null;

            return path.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>Turns libraries into their scoped paths (all locations, trailing separator).</summary>
        internal static List<string> ToScopePaths(IEnumerable<VirtualFolderInfo> libraries) =>
            libraries.SelectMany(l => l.Locations).Select(EnsureTrailingSeparator).ToList();

        /// <summary>
        /// Resolves the scoped paths of the virtual folders accepted by
        /// <paramref name="libraryFilter"/>. An empty <paramref name="scope"/> selects every
        /// accepted library; ids that no longer exist are ignored.
        /// </summary>
        /// <param name="includeAllLibrariesWhenFavoritesOnly">
        /// The intro skip marker scope is the one caller that treated a favorites-only scope as
        /// "all libraries", because a favorite can live in any library.
        /// </param>
        internal static List<string> ResolveScopePaths(ILibraryManager libraryManager, string scope,
            Func<VirtualFolderInfo, bool> libraryFilter, bool includeAllLibrariesWhenFavoritesOnly = false)
        {
            var validLibraryIds = GetValidLibraryIds(scope, out var malformed);

            if (malformed)
            {
                return new List<string> { MalformedScopePath };
            }

            var allLibraries = !validLibraryIds.Any() ||
                               includeAllLibrariesWhenFavoritesOnly &&
                               validLibraryIds.All(id => id == FavoritesScopeId);

            return ToScopePaths(libraryManager.GetVirtualFolders()
                .Where(f => libraryFilter(f) && (allLibraries || validLibraryIds.Contains(f.Id))));
        }

        /// <summary>
        /// The one path prefix test used for "is this item inside the scoped libraries". The
        /// comparison is ordinal-ignore-case: the per-caller copies used the single argument
        /// StartsWith, which is culture sensitive (slower, and wrong under some locales).
        /// </summary>
        internal static bool IsPathInScope(string path, ICollection<string> scopePaths) =>
            !string.IsNullOrEmpty(path) && scopePaths != null && scopePaths.Count > 0 &&
            scopePaths.Any(scopePath => !string.IsNullOrEmpty(scopePath) &&
                                        path.StartsWith(scopePath, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Drops the items whose folder path is outside <paramref name="scopePaths"/>. Items with a
        /// null path are dropped as well (one copy of this filter would have thrown instead).
        /// </summary>
        internal static List<T> FilterInScope<T>(IEnumerable<T> items, Func<T, string> pathSelector,
            ICollection<string> scopePaths) =>
            items.Where(item => IsPathInScope(pathSelector(item), scopePaths)).ToList();
    }
}
