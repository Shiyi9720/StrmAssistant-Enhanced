using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Common
{
    internal sealed class LocalPatchResult
    {
        public byte[] Content { get; set; }
        public int AppliedCount { get; set; }
        public IReadOnlyList<string> Files { get; set; } = Array.Empty<string>();
        public string Error { get; set; }

        public bool Changed => AppliedCount > 0;
    }

    /// <summary>
    /// Field-level merge of patches/*.json into a TMDB detail response, applied only to the
    /// response handed back to Emby (never written to the passive cache).
    ///
    /// Rules: objects merge recursively (only keys in the patch change); arrays and scalars
    /// replace the base value; a null patch value removes the key. Files are applied in file-name
    /// order, so 01-base.json can be overridden by 02-local.json. A malformed patch is skipped and
    /// reported; it never breaks metadata.
    /// </summary>
    internal static class LocalMetadataPatcher
    {
        public const int MaxPatchBytes = 8 * 1024 * 1024;
        public const int MaxPatchFiles = 50;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static LocalPatchResult Apply(byte[] content, IReadOnlyList<string> patchFiles,
            Func<string, string> readAllText)
        {
            var result = new LocalPatchResult
            {
                Content = content,
                Files = patchFiles ?? Array.Empty<string>()
            };

            if (content is null || content.Length == 0 || patchFiles is null || patchFiles.Count == 0)
                return result;
            if (readAllText is null) return result;

            var documents = new List<JsonDocument>();

            try
            {
                JsonDocument baseDocument;

                try
                {
                    baseDocument = JsonDocument.Parse(Utf8NoBom.GetString(content));
                }
                catch (Exception e)
                {
                    result.Error = "base response is not valid JSON: " + e.GetType().Name;
                    return result;
                }

                documents.Add(baseDocument);

                var root = MiniJson.Parse(baseDocument.RootElement);
                var applied = 0;
                var errors = new List<string>();

                foreach (var path in patchFiles)
                {
                    if (applied + errors.Count >= MaxPatchFiles)
                    {
                        errors.Add("too many patch files (max " + MaxPatchFiles + ")");
                        break;
                    }

                    string text;

                    try
                    {
                        text = readAllText(path);
                    }
                    catch (Exception e)
                    {
                        errors.Add(Path.GetFileName(path) + ": read failed (" + e.GetType().Name + ")");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (text.Length > MaxPatchBytes)
                    {
                        errors.Add(Path.GetFileName(path) + ": larger than " + MaxPatchBytes + " bytes");
                        continue;
                    }

                    if (text.Trim() == "{}") continue;

                    try
                    {
                        JsonDocument patchDocument;

                        try
                        {
                            patchDocument = JsonDocument.Parse(text);
                        }
                        catch (Exception e)
                        {
                            errors.Add(Path.GetFileName(path) + ": invalid patch (" +
                                       e.GetType().Name + ")");
                            continue;
                        }

                        documents.Add(patchDocument);

                        var patch = MiniJson.Parse(patchDocument.RootElement);

                        if (!(root is MiniJsonObject) && !(patch is MiniJsonObject))
                        {
                            root = patch;
                            applied++;
                            continue;
                        }

                        if (!(patch is MiniJsonObject patchObject))
                        {
                            errors.Add(Path.GetFileName(path) + ": a patch must be a JSON object");
                            continue;
                        }

                        if (!(root is MiniJsonObject rootObject))
                        {
                            errors.Add(Path.GetFileName(path) + ": base response is not an object");
                            continue;
                        }

                        MergeObject(rootObject, patchObject);
                        applied++;
                    }
                    catch (Exception e)
                    {
                        errors.Add(Path.GetFileName(path) + ": invalid patch (" + e.GetType().Name + ")");
                    }
                }

                result.AppliedCount = applied;

                if (errors.Count > 0) result.Error = string.Join("; ", errors);
                if (applied == 0) return result;

                try
                {
                    result.Content = MiniJson.Serialize(root);
                }
                catch (Exception e)
                {
                    result.Content = content;
                    result.AppliedCount = 0;
                    result.Error = "serialize failed: " + e.GetType().Name;
                }

                return result;
            }
            finally
            {
                foreach (var document in documents)
                {
                    document.Dispose();
                }
            }
        }

        /// <summary>Applies an already-parsed patch object; exposed for tests and diagnostics.</summary>
        public static void MergeObject(MiniJsonObject target, MiniJsonObject patch)
        {
            if (target is null || patch is null) return;

            foreach (var pair in patch.Members)
            {
                if (pair.Value is MiniJsonNullValue)
                {
                    target.Members.Remove(pair.Key);
                    continue;
                }

                if (pair.Value is MiniJsonObject patchObject &&
                    target.Members.TryGetValue(pair.Key, out var existing) &&
                    existing is MiniJsonObject targetObject)
                {
                    MergeObject(targetObject, patchObject);
                    continue;
                }

                target.Members[pair.Key] = pair.Value;
            }
        }
    }
}
