using System.IO;
using System.Text;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Applies the response-side advanced capabilities (certification fallback, then field-level
    /// patches) to a TMDB response on its way back to Emby. Certification runs first so a patch
    /// can still override the inferred rating.
    ///
    /// The enhanced bytes are a per-request projection: they are never written to the passive
    /// cache or the response cache.
    /// </summary>
    internal static class LocalMetadataEnhancements
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static byte[] Apply(byte[] content, LocalMetadataFileKind kind, string tmdbId,
            string root)
        {
            if (content is null || content.Length == 0) return content;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(tmdbId)) return content;

            var options = Plugin.Instance?.GetPluginOptions();

            if (options is null || !options.EnableLocalMetadataSource) return content;

            if (options.CertificationFallbackEnabled)
            {
                var fallback = CertificationFallback.Apply(content, kind,
                    options.CertificationFallbackCountries, options.CertificationFallbackMappings);

                if (fallback.Applied)
                {
                    Plugin.Instance?.Logger?.Debug(
                        "Local metadata certification fallback - {0}: {1}/{2} -> {3}",
                        tmdbId, fallback.SourceCountry, fallback.SourceRating, fallback.TargetRating);
                    content = fallback.Content;
                }
            }

            if (options.LocalPatchesEnabled)
            {
                var files = LocalMetadataFiles.FindPatchFiles(root, kind, tmdbId);

                if (files.Count > 0)
                {
                    var patched = LocalMetadataPatcher.Apply(content, files, ReadAllText);

                    if (patched.Changed)
                    {
                        Plugin.Instance?.Logger?.Debug(
                            "Local metadata patches applied - {0}: {1} file(s), {2}",
                            tmdbId, patched.AppliedCount, patched.Files.Count);
                        content = patched.Content;
                    }

                    if (!string.IsNullOrEmpty(patched.Error))
                    {
                        Plugin.Instance?.Logger?.Debug(
                            "Local metadata patches - {0}: {1}", tmdbId, patched.Error);
                    }
                }
            }

            return content;
        }

        public static string ReadAllText(string path)
        {
            return File.Exists(path) ? Utf8NoBom.GetString(File.ReadAllBytes(path)) : null;
        }
    }
}
