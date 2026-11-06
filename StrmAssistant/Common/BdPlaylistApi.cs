using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Minimal reader for Blu-ray playlists (BDMV/PLAYLIST/*.mpls). Blu-ray chapters are stored
    /// as marks in the playlist, not inside the m2ts stream, so ffprobe alone cannot see them.
    /// All reads are bounds checked and every failure returns an empty result; a malformed
    /// playlist must never fail the media info extraction.
    /// </summary>
    internal static class BdPlaylistApi
    {
        private const double TicksPerMplsTick = 10000000.0 / 45000.0;
        private const int MaxPlaylistCount = 200;
        private const long MaxPlaylistSize = 8 * 1024 * 1024;

        internal sealed class PlaylistInfo
        {
            /// <summary>Total duration in 45 kHz units.</summary>
            public long Duration { get; set; }

            /// <summary>Playback start of the first play item, in 45 kHz units.</summary>
            public long FirstInTime { get; set; }

            /// <summary>Clip identifier (five characters, e.g. 00001) of the first play item.</summary>
            public string FirstClip { get; set; }

            /// <summary>Chapter times relative to the first play item, in 45 kHz units.</summary>
            public List<long> ChapterTimes { get; } = new List<long>();
        }

        /// <summary>
        /// Returns the stream file referenced by the longest playlist, or null when no playlist
        /// can be parsed. The caller falls back to the largest stream file.
        /// </summary>
        public static string GetMainClip(string streamFolder, string playlistFolder)
        {
            try
            {
                if (!Directory.Exists(streamFolder) || !Directory.Exists(playlistFolder)) return null;

                PlaylistInfo best = null;

                foreach (var file in EnumeratePlaylists(playlistFolder))
                {
                    PlaylistInfo info;

                    try
                    {
                        info = Parse(File.ReadAllBytes(file));
                    }
                    catch
                    {
                        continue;
                    }

                    if (info is null || string.IsNullOrEmpty(info.FirstClip)) continue;

                    if (best is null || info.Duration > best.Duration) best = info;
                }

                if (best is null) return null;

                foreach (var extension in new[] { ".m2ts", ".mts" })
                {
                    var candidate = Path.Combine(streamFolder, best.FirstClip + extension);

                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        /// <summary>
        /// Picks the longest playlist that carries chapter marks and converts them to chapters.
        /// </summary>
        public static List<ChapterInfo> SelectChapters(IEnumerable<byte[]> playlists)
        {
            var chapters = new List<ChapterInfo>();

            if (playlists is null) return chapters;

            PlaylistInfo selected = null;

            foreach (var playlist in playlists)
            {
                PlaylistInfo info;

                try
                {
                    info = Parse(playlist);
                }
                catch
                {
                    continue;
                }

                if (info is null || info.ChapterTimes.Count == 0) continue;

                if (selected is null || info.Duration > selected.Duration) selected = info;
            }

            if (selected is null) return chapters;

            foreach (var time in selected.ChapterTimes.Where(t => t >= 0).Distinct().OrderBy(t => t))
            {
                var ticks = (long)Math.Round(time * TicksPerMplsTick);

                if (ticks < 0) continue;

                chapters.Add(new ChapterInfo
                {
                    Name = "Chapter " + (chapters.Count + 1),
                    StartPositionTicks = ticks,
                    MarkerType = MarkerType.Chapter
                });
            }

            return chapters;
        }

        private static IEnumerable<string> EnumeratePlaylists(string folder)
        {
            return Directory.EnumerateFiles(folder, "*.mpls")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(MaxPlaylistCount)
                .Where(f =>
                {
                    try
                    {
                        var info = new FileInfo(f);
                        return info.Exists && info.Length > 0 && info.Length <= MaxPlaylistSize;
                    }
                    catch
                    {
                        return false;
                    }
                });
        }

        public static PlaylistInfo Parse(byte[] data)
        {
            if (data is null || data.Length < 20) return null;

            // Header: "MPLS", version, PlayListStart, PlayListMarkStart, ExtensionDataStart.
            if (data[0] != 'M' || data[1] != 'P' || data[2] != 'L' || data[3] != 'S') return null;

            var playlistStart = ReadU32(data, 8);
            var markStart = ReadU32(data, 12);

            if (playlistStart <= 0 || playlistStart + 10 > data.Length) return null;

            var info = new PlaylistInfo();
            var playItemCount = ReadU16(data, (int)playlistStart + 6);
            var offset = (int)playlistStart + 10;
            var playItems = 0;

            for (var i = 0; i < playItemCount && offset + 2 <= data.Length; i++)
            {
                var length = ReadU16(data, offset);

                // Clip id (5) + codec id (4) + flags (3) + in/out times (8) = 20 bytes minimum.
                if (length < 20 || offset + 2 + length > data.Length) break;

                var inTime = ReadU32(data, offset + 14);
                var outTime = ReadU32(data, offset + 18);

                if (i == 0)
                {
                    info.FirstInTime = inTime;
                    info.FirstClip = ReadClipId(data, offset + 2);
                }

                if (outTime > inTime) info.Duration += outTime - inTime;

                playItems++;
                offset += 2 + length;
            }

            if (playItems == 0) return null;

            // Section header: length (4) + number of marks (2), then fixed 14 byte entries.
            if (markStart <= 0 || markStart + 6 > data.Length) return info;

            var markCount = ReadU16(data, (int)markStart + 4);
            offset = (int)markStart + 6;

            for (var i = 0; i < markCount && offset + 14 <= data.Length; i++)
            {
                // reserved (1) + mark type (1) + play item ref (2) + timestamp (4) +
                // ES pid (2) + duration (4) = 14 bytes.
                var markType = data[offset + 1];
                var refPlayItem = ReadU16(data, offset + 2);
                var markTime = ReadU32(data, offset + 4);

                // Mark timestamps are relative to the playlist start, which equals the first
                // play item's in-time. Only chapter marks inside the first play item line up
                // with the single extracted stream file.
                if (markType == 1 && refPlayItem == 0 && markTime >= info.FirstInTime)
                {
                    info.ChapterTimes.Add(markTime - info.FirstInTime);
                }

                offset += 14;
            }

            return info;
        }

        private static string ReadClipId(byte[] data, int offset)
        {
            if (offset + 5 > data.Length) return null;

            var chars = new char[5];

            for (var i = 0; i < chars.Length; i++)
            {
                var value = data[offset + i];

                if (value == 0) return null;

                chars[i] = (char)value;
            }

            return new string(chars);
        }

        private static int ReadU16(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        private static long ReadU32(byte[] data, int offset)
        {
            return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) |
                   ((long)data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
