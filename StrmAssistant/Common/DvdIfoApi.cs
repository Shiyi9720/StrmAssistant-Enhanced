using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Minimal reader for DVD-Video IFO files (VIDEO_TS/VTS_xx_0.IFO). DVD chapters are programs
    /// inside a title's program chain (PGC); the chapter table (VTS_PTT_SRPT) maps every chapter
    /// to a PGC number and program number, and the cell playback table gives the duration of each
    /// cell, so a chapter starts when the cells before its program's first cell have played.
    /// The layout follows the public DVD-Video IFO documentation. All reads are bounds checked
    /// and every failure returns no chapters; a malformed IFO must never fail the scan.
    /// </summary>
    internal static class DvdIfoApi
    {
        private const int SectorSize = 2048;
        private const int PgcHeaderSize = 236;
        private const int CellPlaybackSize = 24;
        private const int SectorOfVtsPttSrpt = 0xC8;
        private const int SectorOfVtsPgcit = 0xCC;
        private const int MaxPgcCount = 10000;
        private const int MaxCellCount = 255;
        private const int MaxTitleCount = 100;
        private const int MaxChapterCount = 1000;
        private const long TicksPerSecond = 10000000L;

        internal sealed class PgcInfo
        {
            /// <summary>Total playback time of the chain.</summary>
            public long DurationTicks { get; set; }

            /// <summary>Start time of every program, in program order (index 0 is program 1).</summary>
            public List<long> ProgramStartTicks { get; } = new List<long>();
        }

        internal sealed class TitleInfo
        {
            public long DurationTicks { get; set; }
            public int PartCount { get; set; }
            public List<long> ChapterTicks { get; set; } = new List<long>();
        }

        private sealed class IfoInfo
        {
            public List<PgcInfo> Pgcs { get; set; } = new List<PgcInfo>();
            public List<TitleInfo> Titles { get; set; } = new List<TitleInfo>();
        }

        /// <summary>
        /// Picks the longest title across the given IFO files and converts its chapter table to
        /// chapters. When the number of the VTS that contains the extracted VOB is known, that
        /// VTS is preferred. Chapters at or beyond the probed duration are dropped, because a
        /// title split over several VOB files only contributes its first file to the probe.
        /// </summary>
        public static List<ChapterInfo> SelectChapters(IEnumerable<KeyValuePair<string, byte[]>> ifos,
            int? preferredVtsNumber, long runtimeTicks)
        {
            var chapters = new List<ChapterInfo>();

            if (ifos is null) return chapters;

            var candidates = new List<KeyValuePair<int, TitleInfo>>();

            foreach (var ifo in ifos)
            {
                try
                {
                    if (!TryGetVtsNumber(ifo.Key, out var vtsNumber)) continue;

                    var info = ParseIfo(ifo.Value);

                    if (info is null) continue;

                    candidates.AddRange(info.Titles.Select(t =>
                        new KeyValuePair<int, TitleInfo>(vtsNumber, t)));
                }
                catch
                {
                    // ignored: a malformed IFO must never break the extraction
                }
            }

            if (candidates.Count == 0) return chapters;

            if (preferredVtsNumber.HasValue &&
                candidates.Any(c => c.Key == preferredVtsNumber.Value))
            {
                candidates = candidates.Where(c => c.Key == preferredVtsNumber.Value).ToList();
            }

            var selected = candidates
                .Select(c => c.Value)
                .Where(t => t.ChapterTicks.Count > 0)
                .OrderByDescending(t => t.DurationTicks)
                .ThenByDescending(t => t.PartCount)
                .FirstOrDefault();

            if (selected is null) return chapters;

            foreach (var ticks in selected.ChapterTicks)
            {
                if (ticks < 0) continue;
                if (runtimeTicks > 0 && ticks >= runtimeTicks) continue;

                chapters.Add(new ChapterInfo
                {
                    Name = "Chapter " + (chapters.Count + 1),
                    StartPositionTicks = ticks,
                    MarkerType = MarkerType.Chapter
                });
            }

            return chapters;
        }

        /// <summary>
        /// Parses VTS_xx_0.IFO style file names (VTS_01_0.IFO) and returns the VTS number.
        /// </summary>
        internal static bool TryGetVtsNumber(string fileName, out int vtsNumber)
        {
            vtsNumber = 0;

            if (string.IsNullOrEmpty(fileName) ||
                !fileName.EndsWith(".ifo", StringComparison.OrdinalIgnoreCase)) return false;

            var upper = fileName.ToUpperInvariant();

            if (!upper.StartsWith("VTS_", StringComparison.Ordinal) || upper.Length < 6) return false;

            var high = upper[4];
            var low = upper[5];

            if (high < '0' || high > '9' || low < '0' || low > '9') return false;

            vtsNumber = (high - '0') * 10 + (low - '0');

            return vtsNumber > 0;
        }

        private static IfoInfo ParseIfo(byte[] data)
        {
            if (data is null || data.Length < 0x100) return null;

            if (!HasText(data, 0, "DVDVIDEO-VTS")) return null;

            var info = new IfoInfo
            {
                Pgcs = ReadPgcs(data)
            };

            if (!info.Pgcs.Any(p => p != null)) return null;

            info.Titles = ReadTitles(data, info.Pgcs);

            if (info.Titles.Count == 0)
            {
                // The PTT table is missing or unreadable on a few discs; every PGC program then
                // acts as a chapter, which is how the disc plays back anyway.
                info.Titles = info.Pgcs
                    .Where(pgc => pgc != null)
                    .Select(pgc => new TitleInfo
                    {
                        DurationTicks = pgc.DurationTicks,
                        PartCount = pgc.ProgramStartTicks.Count,
                        ChapterTicks = pgc.ProgramStartTicks.Distinct().OrderBy(t => t).ToList()
                    })
                    .Where(title => title.ChapterTicks.Count > 0)
                    .ToList();
            }

            return info;
        }

        private static List<PgcInfo> ReadPgcs(byte[] data)
        {
            var result = new List<PgcInfo>();

            var pgcitSector = ReadU32(data, SectorOfVtsPgcit);

            if (pgcitSector <= 0) return result;

            var pgcitOffset = (long)pgcitSector * SectorSize;

            if (pgcitOffset + 8 > data.Length) return result;

            var pgcCount = ReadU16(data, (int)pgcitOffset);

            if (pgcCount <= 0 || pgcCount > MaxPgcCount) return result;

            var lastByte = ReadU32(data, (int)pgcitOffset + 4);
            var entriesOffset = pgcitOffset + 8;

            if (entriesOffset + (long)pgcCount * 8 > data.Length) return result;

            for (var i = 0; i < pgcCount; i++)
            {
                PgcInfo pgc = null;
                var startByte = ReadU32(data, (int)entriesOffset + i * 8 + 4);

                // Both the search pointer offset and last_byte are relative to the table start.
                if (startByte > 0 && (lastByte <= 0 || startByte + PgcHeaderSize <= lastByte + 1))
                {
                    try
                    {
                        pgc = ReadPgc(data, pgcitOffset + startByte);
                    }
                    catch
                    {
                        // ignored: keep the remaining chains
                    }
                }

                // Entries stay aligned with their 1-based PGC number, so null marks an unreadable
                // chain instead of shifting the numbering.
                result.Add(pgc);
            }

            return result;
        }

        private static PgcInfo ReadPgc(byte[] data, long offset)
        {
            if (offset < 0 || offset + PgcHeaderSize > data.Length) return null;

            var programCount = data[offset + 2];
            var cellCount = data[offset + 3];

            if (programCount <= 0 || cellCount <= 0 || cellCount > MaxCellCount) return null;

            var programMapOffset = ReadU16(data, (int)offset + 0xE6);
            var cellPlaybackOffset = ReadU16(data, (int)offset + 0xE8);

            if (programMapOffset <= 0 || cellPlaybackOffset <= 0) return null;

            var programMapStart = offset + programMapOffset;
            var cellStart = offset + cellPlaybackOffset;

            if (programMapStart + programCount > data.Length) return null;
            if (cellStart + (long)cellCount * CellPlaybackSize > data.Length) return null;

            // before[cell] is the playback time of all cells that precede that cell.
            var before = new long[cellCount + 2];

            for (var cell = 1; cell <= cellCount; cell++)
            {
                var cellOffset = (int)(cellStart + (long)(cell - 1) * CellPlaybackSize);
                var flags = data[cellOffset];
                var blockMode = (flags >> 6) & 0x3;
                var blockType = (flags >> 4) & 0x3;

                // Angle blocks store one cell per angle; only the first cell is played by default
                // and therefore only that one belongs to the title timeline.
                var isAngleCopy = blockType == 1 && blockMode != 1;
                var duration = isAngleCopy ? 0 : ReadBcdTicks(data, cellOffset + 4);

                before[cell + 1] = before[cell] + duration;
            }

            var pgc = new PgcInfo
            {
                DurationTicks = before[cellCount + 1]
            };

            var previousCell = 1;

            for (var program = 0; program < programCount; program++)
            {
                var cell = data[programMapStart + program];

                if (cell < 1 || cell > cellCount) continue;
                if (cell < previousCell) continue;

                previousCell = cell;
                pgc.ProgramStartTicks.Add(before[cell]);
            }

            return pgc.ProgramStartTicks.Count == 0 ? null : pgc;
        }

        private static List<TitleInfo> ReadTitles(byte[] data, List<PgcInfo> pgcs)
        {
            var titles = new List<TitleInfo>();

            var tableSector = ReadU32(data, SectorOfVtsPttSrpt);

            if (tableSector <= 0) return titles;

            var tableOffset = (long)tableSector * SectorSize;

            if (tableOffset + 8 > data.Length) return titles;

            var titleCount = ReadU16(data, (int)tableOffset);

            if (titleCount <= 0 || titleCount > MaxTitleCount) return titles;

            var offsetsStart = tableOffset + 8;

            if (offsetsStart + (long)titleCount * 4 > data.Length) return titles;

            for (var i = 0; i < titleCount; i++)
            {
                // Offsets are relative to the start of the table.
                var ttuOffset = ReadU32(data, (int)offsetsStart + i * 4);

                if (ttuOffset <= 0 || tableOffset + ttuOffset + 2 > data.Length) continue;

                var ttu = (int)(tableOffset + ttuOffset);
                var partCount = ReadU16(data, ttu);

                if (partCount <= 0 || partCount > MaxChapterCount) continue;
                if (ttu + 2 + (long)partCount * 4 > data.Length) continue;

                var title = ReadTitle(data, ttu, partCount, pgcs);

                if (title != null) titles.Add(title);
            }

            return titles;
        }

        private static TitleInfo ReadTitle(byte[] data, int ttu, int partCount, List<PgcInfo> pgcs)
        {
            var chapterTicks = new List<long>();
            long duration = 0;

            for (var part = 0; part < partCount; part++)
            {
                var pgcn = ReadU16(data, ttu + 2 + part * 4);
                var pgn = ReadU16(data, ttu + 2 + part * 4 + 2);

                if (pgcn < 1 || pgcn > pgcs.Count) continue;

                var pgc = pgcs[pgcn - 1];

                if (pgc is null || pgn < 1 || pgn > pgc.ProgramStartTicks.Count) continue;

                if (part == 0) duration = pgc.DurationTicks;

                chapterTicks.Add(pgc.ProgramStartTicks[pgn - 1]);
            }

            if (chapterTicks.Count == 0) return null;

            var ordered = chapterTicks.Distinct().OrderBy(t => t).ToList();

            // The title always starts at zero even when its first chapter points at a later cell.
            if (ordered[0] != 0) ordered.Insert(0, 0);

            return new TitleInfo
            {
                DurationTicks = duration > 0 ? duration : ordered[ordered.Count - 1],
                PartCount = chapterTicks.Count,
                ChapterTicks = ordered
            };
        }

        private static long ReadBcdTicks(byte[] data, int offset)
        {
            if (offset < 0 || offset + 4 > data.Length) return 0;

            var hour = Bcd(data[offset]);
            var minute = Bcd(data[offset + 1]);
            var second = Bcd(data[offset + 2]);
            var frameByte = data[offset + 3];
            var frames = Bcd((byte)(frameByte & 0x3F));

            if (hour < 0 || minute < 0 || second < 0 || frames < 0) return 0;

            var ticks = ((long)hour * 3600 + minute * 60 + second) * TicksPerSecond;

            if (frames > 0)
            {
                // Frame rate code 1 is 25 fps (PAL), 3 is 30 fps (NTSC); others are undefined.
                var framesPerSecond = ((frameByte >> 6) & 0x3) == 1 ? 25 : 30;

                if (frames < framesPerSecond) ticks += frames * TicksPerSecond / framesPerSecond;
            }

            return ticks;
        }

        private static int Bcd(byte value)
        {
            var high = (value >> 4) & 0xF;
            var low = value & 0xF;

            return high <= 9 && low <= 9 ? high * 10 + low : -1;
        }

        private static bool HasText(byte[] data, int offset, string text)
        {
            if (offset < 0 || offset + text.Length > data.Length) return false;

            for (var i = 0; i < text.Length; i++)
            {
                if (data[offset + i] != (byte)text[i]) return false;
            }

            return true;
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
