using System;
using System.Collections.Generic;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Classifies a raw library-scope option value. Kept free of Emby types so the corruption
    /// distinction (unset vs. every segment unparsable) can be unit tested.
    /// </summary>
    internal enum LibraryScopeKind
    {
        /// <summary>No scope configured: the documented "all libraries" value.</summary>
        Empty,

        /// <summary>At least one segment parsed as a library id.</summary>
        WellFormed,

        /// <summary>Non-empty scope where every segment failed to parse (corrupt option file).</summary>
        Malformed
    }

    internal static class LibraryScopeParser
    {
        private static readonly char[] Separators = { ',' };

        internal static LibraryScopeKind Classify(string scope, out long[] parsedIds)
        {
            parsedIds = Array.Empty<long>();

            if (string.IsNullOrWhiteSpace(scope)) return LibraryScopeKind.Empty;

            var parts = scope.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return LibraryScopeKind.Empty;

            var ids = new List<long>(parts.Length);

            foreach (var part in parts)
            {
                if (long.TryParse(part.Trim(), out var id)) ids.Add(id);
            }

            parsedIds = ids.ToArray();

            // A non-empty value where nothing parses must not read as "all libraries": that
            // would silently widen a corrupt scope to the whole server.
            return ids.Count == 0 ? LibraryScopeKind.Malformed : LibraryScopeKind.WellFormed;
        }
    }
}