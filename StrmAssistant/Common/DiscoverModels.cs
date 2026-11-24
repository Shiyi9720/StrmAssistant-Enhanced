using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Common
{
    /// <summary>
    /// One configured Douban collection line: <c>标识|名称|数量</c>. The identifier is the
    /// rexxar <c>subject_collection</c> id (for example <c>ECMNABFUI</c>); name and count are
    /// optional so <c>ECMNABFUI</c> alone is a valid line.
    /// </summary>
    public sealed class DiscoverCollectionSpec
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Count { get; set; } = 20;

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }

    public static class DiscoverCollectionSpecParser
    {
        public const int DefaultCount = 20;
        public const int MaxCount = 100;

        /// <summary>
        /// Parses the newline-separated collection setting. Blank lines, <c>#</c> comments and
        /// duplicate ids are ignored; a malformed line is skipped rather than failing the whole
        /// channel, because the setting is edited by hand in the settings page.
        /// </summary>
        public static List<DiscoverCollectionSpec> Parse(string text)
        {
            var result = new List<DiscoverCollectionSpec>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var parts = line.Split('|');
                var id = (parts[0] ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                if (!seen.Add(id)) continue;

                var name = parts.Length > 1 ? (parts[1] ?? string.Empty).Trim() : string.Empty;
                var count = DefaultCount;
                if (parts.Length > 2)
                {
                    var countText = (parts[2] ?? string.Empty).Trim();
                    if (int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var parsed))
                    {
                        count = Math.Max(1, Math.Min(MaxCount, parsed));
                    }
                }

                result.Add(new DiscoverCollectionSpec
                {
                    Id = id,
                    Name = name,
                    Count = count
                });
            }

            return result;
        }
    }

    /// <summary>Stable channel item ids. Emby only sees the string id; the prefixes let a later
    /// request (child listing or media-source callback) recover the collection/entry.</summary>
    public static class DiscoverChannelIds
    {
        public const string Root = "sa-discover-root";
        public const string CollectionPrefix = "sa-discover-col:";
        public const string EntryPrefix = "sa-discover-item:";

        public static string Collection(string collectionId) => CollectionPrefix + collectionId;

        /// <summary>
        /// The kind is part of the id because Emby's media-source callback only receives the id,
        /// and Douban's trailer endpoint differs for movie and tv subjects.
        /// </summary>
        public static string Entry(string doubanId, string kind) =>
            EntryPrefix + (string.Equals(kind, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie") +
            ":" + doubanId;

        public static bool TryParseCollection(string id, out string collectionId)
        {
            collectionId = null;
            if (id == null || !id.StartsWith(CollectionPrefix, StringComparison.Ordinal)) return false;
            collectionId = id.Substring(CollectionPrefix.Length);
            return collectionId.Length > 0;
        }

        public static bool TryParseEntry(string id, out string kind, out string doubanId)
        {
            kind = null;
            doubanId = null;
            if (id == null || !id.StartsWith(EntryPrefix, StringComparison.Ordinal)) return false;

            var value = id.Substring(EntryPrefix.Length);
            var separator = value.IndexOf(':');
            if (separator <= 0 || separator >= value.Length - 1) return false;

            kind = value.Substring(0, separator);
            doubanId = value.Substring(separator + 1);
            return doubanId.Length > 0;
        }
    }

    /// <summary>Provider-id keys written on channel entries. The favorite hook uses them to
    /// recognise a 探索发现 item without guessing from its type or parent.</summary>
    public static class DiscoverMarker
    {
        public const string ProviderKey = "StrmAssistantDiscover";
        public const string KindKey = "StrmAssistantDiscoverKind";
    }

    /// <summary>One library item as seen by the channel's "in library" matcher.</summary>
    public sealed class DiscoverLibraryEntry
    {
        public long ItemId { get; set; }
        public string Name { get; set; }
        public string OriginalTitle { get; set; }
        public int? Year { get; set; }
        public string DoubanId { get; set; }
        public string Kind { get; set; }
    }

    public sealed class DiscoverMatch
    {
        public static readonly DiscoverMatch Missing = new DiscoverMatch();

        public bool InLibrary { get; set; }
        public long ItemId { get; set; }
        public string ItemName { get; set; }
        public int Score { get; set; }

        public string StatusText => InLibrary ? "已在库" : "缺失";
    }

    /// <summary>
    /// Matches a Douban entry against the local library. The Douban id is authoritative when a
    /// library item carries one; otherwise an exact normalized title (+ year) wins. Contains
    /// matching is deliberately excluded so a channel never reports "已在库" for a sequel with a
    /// similar name.
    /// </summary>
    public sealed class DiscoverLibraryIndex
    {
        private readonly Dictionary<string, List<DiscoverLibraryEntry>> _byDouban =
            new Dictionary<string, List<DiscoverLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DiscoverLibraryEntry>> _byTitle =
            new Dictionary<string, List<DiscoverLibraryEntry>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DiscoverLibraryEntry>> _byOriginalTitle =
            new Dictionary<string, List<DiscoverLibraryEntry>>(StringComparer.Ordinal);

        public int Count { get; private set; }

        public DiscoverLibraryIndex(IEnumerable<DiscoverLibraryEntry> entries)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                Count++;

                if (!string.IsNullOrWhiteSpace(entry.DoubanId))
                {
                    Add(_byDouban, entry.DoubanId.Trim(), entry);
                }

                var title = DiscoverText.Normalize(entry.Name);
                if (title.Length > 0) Add(_byTitle, title, entry);

                var original = DiscoverText.Normalize(entry.OriginalTitle);
                if (original.Length > 0 && original != title) Add(_byOriginalTitle, original, entry);
            }
        }

        private static void Add(Dictionary<string, List<DiscoverLibraryEntry>> map, string key,
            DiscoverLibraryEntry entry)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<DiscoverLibraryEntry>();
                map[key] = list;
            }

            list.Add(entry);
        }

        public DiscoverMatch Match(string doubanId, string title, int? year, string kind)
        {
            if (!string.IsNullOrWhiteSpace(doubanId) &&
                _byDouban.TryGetValue(doubanId.Trim(), out var byId))
            {
                var best = Pick(byId, year, kind, 100);
                if (best != null) return ToMatch(best, 100);
            }

            var normalized = DiscoverText.Normalize(title);
            if (normalized.Length == 0) return DiscoverMatch.Missing;

            var candidates = new List<DiscoverLibraryEntry>();
            if (_byTitle.TryGetValue(normalized, out var exact)) candidates.AddRange(exact);
            if (_byOriginalTitle.TryGetValue(normalized, out var original)) candidates.AddRange(original);
            if (candidates.Count == 0) return DiscoverMatch.Missing;

            var match = Pick(candidates, year, kind, 70);
            return match == null ? DiscoverMatch.Missing : ToMatch(match, 70);
        }

        private static DiscoverLibraryEntry Pick(List<DiscoverLibraryEntry> candidates, int? year,
            string kind, int baseScore)
        {
            DiscoverLibraryEntry best = null;
            var bestScore = int.MinValue;

            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;

                var score = baseScore;
                if (!string.Equals(candidate.Kind ?? string.Empty, kind ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase))
                {
                    score -= 30;
                }

                if (year.HasValue && candidate.Year.HasValue)
                {
                    if (year.Value == candidate.Year.Value) score += 40;
                    else if (Math.Abs(year.Value - candidate.Year.Value) <= 1) score += 15;
                    else score -= 30;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return bestScore >= 50 ? best : null;
        }

        private static DiscoverMatch ToMatch(DiscoverLibraryEntry entry, int score)
        {
            return new DiscoverMatch
            {
                InLibrary = true,
                ItemId = entry.ItemId,
                ItemName = entry.Name,
                Score = score
            };
        }
    }

    /// <summary>Presentation rules for a channel entry (tagline / overview / year).</summary>
    public static class DiscoverEntryProjection
    {
        public static string BuildTagline(DiscoverMatch match) =>
            match != null && match.InLibrary ? "已在库" : "缺失";

        public static string BuildOverview(string description, string cardSubtitle,
            DiscoverMatch match)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(description)) lines.Add(description.Trim());
            if (!string.IsNullOrWhiteSpace(cardSubtitle)) lines.Add(cardSubtitle.Trim());
            lines.Add("状态：" + BuildTagline(match));
            return string.Join("\n", lines);
        }
    }

    public static class DiscoverText
    {
        /// <summary>Title comparison ignores case, whitespace, punctuation and symbols.</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim().ToLowerInvariant())
            {
                if (char.IsWhiteSpace(character) || char.IsPunctuation(character) ||
                    char.IsSymbol(character))
                {
                    continue;
                }

                builder.Append(character);
            }

            return builder.ToString();
        }

        /// <summary>"2026 / 罗马尼亚 …" → 2026. Collections sometimes omit a year field.</summary>
        public static int? YearFromSubtitle(string subtitle)
        {
            if (string.IsNullOrWhiteSpace(subtitle)) return null;

            var value = subtitle.Trim();
            if (value.Length < 4) return null;
            if (!int.TryParse(value.Substring(0, 4), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var year)) return null;
            if (year < 1880 || year > 2200) return null;
            return year;
        }

        /// <summary>mm:ss (occasionally h:mm:ss) → ticks. Unparsable values stay unknown.</summary>
        public static long? ParseRuntimeTicks(string runtime)
        {
            if (string.IsNullOrWhiteSpace(runtime)) return null;

            var parts = runtime.Trim().Split(':');
            if (parts.Length < 2 || parts.Length > 3) return null;

            var seconds = 0L;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var value) || value < 0)
                {
                    return null;
                }

                seconds = seconds * 60 + value;
            }

            return seconds <= 0 ? (long?)null : seconds * TimeSpan.TicksPerSecond;
        }
    }

    public sealed class DiscoverSubscribeResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int StatusCode { get; set; }
        public string RequestUrl { get; set; }
        public string RequestBody { get; set; }
    }

    /// <summary>
    /// Builds the two MoviePilot subscription shapes:
    /// <list type="bullet">
    /// <item><c>webhook</c> — a generic <c>library.subscribe</c> JSON event posted to the
    /// configured URL, for MoviePilot webhook receivers;</item>
    /// <item><c>api</c> — MoviePilot <c>POST /api/v1/subscribe/?apikey=…</c> with a Douban media
    /// identity, so MoviePilot resolves the exact subject instead of fuzzy-searching the title.</item>
    /// </list>
    /// </summary>
    public static class DiscoverSubscribe
    {
        public const string WebhookEvent = "library.subscribe";

        public static string BuildWebhookUrl(string url, string token)
        {
            var root = (url ?? string.Empty).Trim();
            if (root.Length == 0) return string.Empty;
            if (string.IsNullOrWhiteSpace(token)) return root;

            return root + (root.IndexOf('?') >= 0 ? "&" : "?") +
                   "token=" + Uri.EscapeDataString(token.Trim());
        }

        public static string BuildApiUrl(string baseUrl, string token)
        {
            var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (root.Length == 0) return string.Empty;

            var url = root + "/api/v1/subscribe/";
            if (!string.IsNullOrWhiteSpace(token))
            {
                url += "?apikey=" + Uri.EscapeDataString(token.Trim());
            }

            return url;
        }

        public static string BuildWebhookPayload(string doubanId, string title, string kind,
            int? year, string poster, double? rating)
        {
            var payload = new Dictionary<string, object>
            {
                ["event"] = WebhookEvent,
                ["source"] = "StrmAssistant",
                ["douban_id"] = doubanId ?? string.Empty,
                ["title"] = title ?? string.Empty,
                ["media_type"] = KindText(kind)
            };

            if (year.HasValue) payload["year"] = year.Value;
            if (!string.IsNullOrWhiteSpace(poster)) payload["poster"] = poster;
            if (rating.HasValue) payload["rating"] = Math.Round(rating.Value, 1);

            return JsonSerializer.Serialize(payload);
        }

        public static string BuildApiPayload(string doubanId, string title, string kind,
            int? year, string poster, double? rating)
        {
            var payload = new Dictionary<string, object>
            {
                ["name"] = title ?? string.Empty,
                ["type"] = string.Equals(KindText(kind), "tv", StringComparison.Ordinal)
                    ? "电视剧"
                    : "电影",
                ["media_source"] = "douban",
                ["media_id"] = doubanId ?? string.Empty
            };

            if (year.HasValue) payload["year"] = year.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(poster)) payload["poster"] = poster;
            if (rating.HasValue) payload["vote"] = Math.Round(rating.Value, 1);

            return JsonSerializer.Serialize(payload);
        }

        private static string KindText(string kind) =>
            string.Equals(kind, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";

        /// <summary>Extracts MoviePilot's <c>{"success":true,"message":"…"}</c> envelope.</summary>
        public static DiscoverSubscribeResult ParseResponse(int statusCode, string body)
        {
            var result = new DiscoverSubscribeResult
            {
                StatusCode = statusCode,
                Success = statusCode >= 200 && statusCode < 300,
                Message = body
            };

            if (string.IsNullOrWhiteSpace(body)) return result;

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return result;

                if (root.TryGetProperty("success", out var success))
                {
                    result.Success = result.Success &&
                                     (success.ValueKind == JsonValueKind.True ||
                                      (success.ValueKind == JsonValueKind.String &&
                                       bool.TryParse(success.GetString(), out var parsed) && parsed));
                }

                if (root.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    result.Message = message.GetString();
                }
            }
            catch (JsonException)
            {
                // Non-JSON webhook receivers are allowed; the HTTP status is the verdict.
            }

            return result;
        }
    }
}
