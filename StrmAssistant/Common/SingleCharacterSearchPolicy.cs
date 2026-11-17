using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Bounds for the optional single-character contains tier. Very common characters have huge
    /// postings; matching them would add a lot of work for little value, so a posting larger than
    /// <see cref="MaxCandidates"/> is ignored and the query falls back to exact/prefix only.
    /// </summary>
    internal static class SingleCharacterSearchPolicy
    {
        /// <summary>Total unigram postings above which the unigram layer is not built at all.</summary>
        internal const long MaxUnigramPostings = 2000000;

        /// <summary>A single character with more candidates than this is not expanded.</summary>
        internal const int MaxCandidates = 4000;

        internal static bool AllowsContains(bool enabled, string text, bool textHasChinese)
        {
            return enabled && textHasChinese && text != null && text.Length == 1;
        }

        internal static bool MayUsePosting(bool enabled, string text, bool textHasChinese, int postingLength)
        {
            return AllowsContains(enabled, text, textHasChinese) &&
                   postingLength > 0 && postingLength <= MaxCandidates;
        }
    }
}
