using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Minimal read-only ISO9660/UDF reader used to pull the main video file (the largest
    /// BDMV/STREAM .m2ts or VIDEO_TS .vob) out of a disc image. Emby cannot probe inside an
    /// ISO, so the extracted file is cached on disk and registered as an additional media
    /// source. No mounting or elevated privileges are required.
    /// </summary>
    public static class IsoApi
    {
        private const int SectorSize = 2048;

        private static ILogger Logger => Plugin.Instance.Logger;

        private static readonly ConcurrentDictionary<string, ExtractionJob> Jobs =
            new ConcurrentDictionary<string, ExtractionJob>(StringComparer.OrdinalIgnoreCase);

        public static bool IsIsoPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the cached extraction path when it already exists, otherwise starts a
        /// background extraction and waits for it up to the configured timeout.
        /// </summary>
        public static async Task<string> GetOrExtractAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;

            if (!options.EnableIsoExtraction || item is null || !IsIsoPath(item.Path)) return null;

            var isoPath = item.Path;

            if (!File.Exists(isoPath)) return null;

            var job = Jobs.GetOrAdd(isoPath, path => new ExtractionJob(path));

            try
            {
                var completed = await Task.WhenAny(job.Task,
                        Task.Delay(TimeSpan.FromSeconds(Math.Max(10, options.IsoExtractionWaitSeconds)),
                            cancellationToken))
                    .ConfigureAwait(false);

                if (completed != job.Task)
                {
                    Logger.Info("IsoExtract - Still extracting in background: " + isoPath);
                    return null;
                }

                return await job.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                Logger.Debug("IsoExtract - Failed: " + e.Message);
                Jobs.TryRemove(isoPath, out _);
                return null;
            }
        }

        /// <summary>
        /// Extracts synchronously (used by the scheduled task) and returns the cached path.
        /// </summary>
        public static async Task<string> ExtractAsync(string isoPath, IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(isoPath)) return null;

            var job = Jobs.GetOrAdd(isoPath, path => new ExtractionJob(path));

            try
            {
                return await job.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                Logger.Info("IsoExtract - Failed: " + isoPath + " - " + e.Message);
                Logger.Debug(e.StackTrace);
                Jobs.TryRemove(isoPath, out _);
                return null;
            }
        }

        public static string GetCacheFolder()
        {
            var configured = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IsoExtractionCacheFolder;

            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            var dataFolder = Plugin.Instance.DataFolderPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                dataFolder = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                    "StrmAssistant");
            }

            return Path.Combine(dataFolder, "iso-cache");
        }

        private sealed class ExtractionJob
        {
            public Task<string> Task { get; }

            public ExtractionJob(string isoPath)
            {
                Task = System.Threading.Tasks.Task.Run(() => ExtractCore(isoPath));
            }
        }

        private static string ExtractCore(string isoPath)
        {
            var fileInfo = new FileInfo(isoPath);
            var cacheKey = ComputeCacheKey(isoPath, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
            var cacheFolder = Path.Combine(GetCacheFolder(), cacheKey);

            var manifestPath = Path.Combine(cacheFolder, "source.txt");
            if (File.Exists(manifestPath))
            {
                var cachedPath = File.ReadAllText(manifestPath).Trim();
                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath)) return cachedPath;
            }

            Directory.CreateDirectory(cacheFolder);

            using var reader = new IsoImageReader(isoPath);
            var mainVideo = reader.FindMainVideo();

            if (mainVideo is null)
            {
                Logger.Info("IsoExtract - No BDMV/STREAM or VIDEO_TS video found: " + isoPath);
                return null;
            }

            var fileName = SanitizeFileName(Path.GetFileNameWithoutExtension(isoPath)) + "." +
                           (mainVideo.Container == "vob" ? "vob" : "m2ts");
            var destination = Path.Combine(cacheFolder, fileName);
            var partPath = destination + ".part";

            Logger.Info("IsoExtract - Extracting " + mainVideo.Name + " (" + mainVideo.Size + " bytes) from " +
                        isoPath);

            var lastLoggedPercent = -1;
            var progress = new Progress<double>(value =>
            {
                var percent = (int)(value * 100);
                if (percent / 10 == lastLoggedPercent / 10) return;
                lastLoggedPercent = percent;
                Logger.Info("IsoExtract - Progress " + percent + "%: " + fileName);
            });

            reader.Extract(mainVideo, partPath, progress, CancellationToken.None);

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(partPath, destination);

            File.WriteAllText(manifestPath, destination);

            Logger.Info("IsoExtract - Extraction complete: " + destination);

            return destination;
        }

        private static string ComputeCacheKey(string path, long size, long lastWriteTicks)
        {
            using var sha1 = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(path + "|" + size + "|" + lastWriteTicks);
            return string.Concat(sha1.ComputeHash(bytes).Select(b => b.ToString("x2"))).Substring(0, 16);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "video" : name;
        }

        private sealed class MainVideo
        {
            public string Name { get; set; }
            public long Size { get; set; }
            public string Container { get; set; }
            public List<Extent> Extents { get; set; } = new List<Extent>();
            public byte[] EmbeddedData { get; set; }
        }

        private sealed class Extent
        {
            public long Offset { get; set; }
            public long Length { get; set; }
            public bool IsRecorded { get; set; } = true;
        }

        private sealed class IsoImageReader : IDisposable
        {
            private readonly FileStream _stream;
            private readonly long _sectorCount;

            public IsoImageReader(string path)
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                    FileOptions.RandomAccess);
                _sectorCount = _stream.Length / SectorSize;
            }

            public void Dispose()
            {
                _stream.Dispose();
            }

            public MainVideo FindMainVideo()
            {
                try
                {
                    var udf = FindUdfMainVideo();
                    if (udf != null) return udf;
                }
                catch (Exception e)
                {
                    Logger.Debug("IsoExtract - UDF parse failed: " + e.Message);
                }

                try
                {
                    return FindIso9660MainVideo();
                }
                catch (Exception e)
                {
                    Logger.Debug("IsoExtract - ISO9660 parse failed: " + e.Message);
                    return null;
                }
            }

            public void Extract(MainVideo file, string destination, IProgress<double> progress,
                CancellationToken cancellationToken)
            {
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 20, FileOptions.SequentialScan);

                if (file.EmbeddedData != null)
                {
                    output.Write(file.EmbeddedData, 0, file.EmbeddedData.Length);
                    progress?.Report(1.0);
                    return;
                }

                var buffer = new byte[1 << 20];
                long written = 0;
                var total = Math.Max(1, file.Size);

                foreach (var extent in file.Extents)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var remaining = extent.Length;

                    if (!extent.IsRecorded)
                    {
                        WriteZeros(output, remaining, buffer);
                        written += remaining;
                    }
                    else
                    {
                        _stream.Position = extent.Offset;

                        while (remaining > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var toRead = (int)Math.Min(buffer.Length, remaining);
                            var read = _stream.Read(buffer, 0, toRead);

                            if (read <= 0) break;

                            output.Write(buffer, 0, read);
                            remaining -= read;
                            written += read;
                        }
                    }

                    progress?.Report(Math.Min(1.0, (double)written / total));
                }

                progress?.Report(1.0);
            }

            private static void WriteZeros(Stream output, long count, byte[] buffer)
            {
                Array.Clear(buffer, 0, buffer.Length);

                while (count > 0)
                {
                    var toWrite = (int)Math.Min(buffer.Length, count);
                    output.Write(buffer, 0, toWrite);
                    count -= toWrite;
                }
            }

            private byte[] ReadSectors(long lba, int count)
            {
                if (lba < 0 || lba >= _sectorCount) throw new IOException("Sector out of range: " + lba);

                var available = (int)Math.Min(count, _sectorCount - lba);
                var buffer = new byte[available * SectorSize];

                _stream.Position = lba * SectorSize;
                var offset = 0;

                while (offset < buffer.Length)
                {
                    var read = _stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }

                return buffer;
            }

            // ---------------- UDF ----------------

            private MainVideo FindUdfMainVideo()
            {
                var context = ReadUdfContext();
                if (context is null) return null;

                var bdmv = FindUdfDirectory(context, context.RootFileEntry, "BDMV");
                if (bdmv != null)
                {
                    var stream = FindUdfDirectory(context, bdmv, "STREAM");
                    if (stream != null)
                    {
                        var video = ListUdfFiles(context, stream)
                            .Where(f => f.Name.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
                                        f.Name.EndsWith(".mts", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => f.Size)
                            .FirstOrDefault();

                        if (video != null)
                        {
                            video.Container = "m2ts";
                            return video;
                        }
                    }
                }

                var videoTs = FindUdfDirectory(context, context.RootFileEntry, "VIDEO_TS");
                if (videoTs != null)
                {
                    var vob = ListUdfFiles(context, videoTs)
                        .Where(f => f.Name.EndsWith(".vob", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.Size)
                        .FirstOrDefault();

                    if (vob != null)
                    {
                        vob.Container = "vob";
                        return vob;
                    }
                }

                return null;
            }

            private UdfContext ReadUdfContext()
            {
                const int anchorSector = 256;
                var anchor = TryReadAnchor(anchorSector) ?? TryReadAnchor(512) ??
                             TryReadAnchor((int)(_sectorCount - 256)) ?? TryReadAnchor((int)(_sectorCount - 1));

                if (anchor is null) return null;

                var sequenceLength = Le32(anchor, 16);
                var sequenceLocation = Le32(anchor, 20);
                var descriptorCount = Math.Min(64, Math.Max(1, sequenceLength / SectorSize));

                var context = new UdfContext();
                long fileSetLocation = -1;

                for (var i = 0; i < descriptorCount; i++)
                {
                    var descriptor = ReadSectors(sequenceLocation + i, 1);
                    var tag = Le16(descriptor, 0);

                    if (tag == 5)
                    {
                        context.PartitionStart = (int)Le32(descriptor, 188);
                    }
                    else if (tag == 6)
                    {
                        fileSetLocation = Le32(descriptor, 252);
                    }
                    else if (tag == 8)
                    {
                        break;
                    }
                }

                if (fileSetLocation < 0) return null;

                var fsd = ReadSectors(context.PartitionStart + fileSetLocation, 1);

                if (Le16(fsd, 0) != 256) return null;

                var rootLength = Le32(fsd, 400);
                var rootLocation = Le32(fsd, 404);

                context.RootFileEntry = ReadFileEntry(context.PartitionStart + rootLocation);

                return context.RootFileEntry is null ? null : context;
            }

            private byte[] TryReadAnchor(int lba)
            {
                if (lba < 0 || lba >= _sectorCount) return null;

                var buffer = ReadSectors(lba, 1);

                return Le16(buffer, 0) == 2 ? buffer : null;
            }

            private UdfFileEntry FindUdfDirectory(UdfContext context, UdfFileEntry directory, string name)
            {
                if (!directory.IsDirectory) return null;

                var data = ReadFileEntryData(context, directory);

                return ListUdfEntries(context, data)
                    .Select(e => e.Value)
                    .FirstOrDefault(e => e.IsDirectory &&
                                         string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            private List<MainVideo> ListUdfFiles(UdfContext context, UdfFileEntry directory)
            {
                var data = ReadFileEntryData(context, directory);

                return ListUdfEntries(context, data).Select(e => e.Value)
                    .Where(e => !e.IsDirectory)
                    .Select(e => new MainVideo
                    {
                        Name = e.Name,
                        Size = e.Size,
                        EmbeddedData = e.EmbeddedData,
                        Extents = ParseExtents(context, e).ToList()
                    })
                    .ToList();
            }

            private IEnumerable<KeyValuePair<string, UdfFileEntry>> ListUdfEntries(UdfContext context, byte[] data)
            {
                var entries = new List<KeyValuePair<string, UdfFileEntry>>();
                var offset = 0;

                while (offset + 38 <= data.Length)
                {
                    var tag = Le16(data, offset);

                    if (tag != 257) break;

                    var characteristics = data[offset + 18];
                    var nameLength = data[offset + 19];
                    var icbLocation = Le32(data, offset + 24);
                    var implementationUseLength = Le16(data, offset + 36);
                    var nameOffset = offset + 38 + implementationUseLength;

                    var recordLength = 38 + implementationUseLength + nameLength;
                    recordLength = (recordLength + 3) & ~3;

                    if (nameLength > 0 && nameOffset + nameLength <= data.Length)
                    {
                        if ((characteristics & 0x08) == 0)
                        {
                            var name = DecodeOstaName(data, nameOffset, nameLength);
                            var entry = ReadFileEntry(context.PartitionStart + icbLocation);

                            if (entry != null && !string.IsNullOrEmpty(name))
                            {
                                entry.Name = name;
                                entry.IsDirectory = (characteristics & 0x02) != 0 ||
                                                    (entry.FileType & 0x02) != 0;
                                entries.Add(new KeyValuePair<string, UdfFileEntry>(name, entry));
                            }
                        }
                    }

                    // A zero-length identifier is the parent entry or padding, not the end of
                    // the directory. Advance past it instead of stopping.
                    offset += recordLength;
                }

                return entries;
            }

            private static string DecodeOstaName(byte[] data, int offset, int length)
            {
                if (length <= 0) return string.Empty;

                var compressionId = data[offset];

                if (compressionId == 8 && length > 1)
                {
                    return Encoding.UTF8.GetString(data, offset + 1, length - 1);
                }

                if (compressionId == 16 && length > 1)
                {
                    return Encoding.BigEndianUnicode.GetString(data, offset + 1, length - 1);
                }

                return Encoding.UTF8.GetString(data, offset, length);
            }

            private UdfFileEntry ReadFileEntry(long lba)
            {
                var block = ReadSectors(lba, 1);
                var tag = Le16(block, 0);

                if (tag != 261 && tag != 266) return null;

                var entry = new UdfFileEntry
                {
                    IsExtended = tag == 266,
                    FileType = block[27],
                    Flags = Le16(block, 34),
                    InfoLength = Le64(block, 56)
                };

                var lengthOfExtendedAttributesOffset = entry.IsExtended ? 208 : 168;
                var lengthOfAllocationDescriptorsOffset = entry.IsExtended ? 212 : 172;
                var allocationDescriptorsOffset = entry.IsExtended ? 216 : 168;

                var extendedAttributesLength = Le32(block, lengthOfExtendedAttributesOffset);
                var allocationDescriptorsLength = Le32(block, lengthOfAllocationDescriptorsOffset);
                var start = allocationDescriptorsOffset + extendedAttributesLength;

                if ((entry.Flags & 0x7) == 3)
                {
                    // Embedded data follows the (empty) allocation descriptor area.
                    var embeddedLength = (int)Math.Min(entry.InfoLength, block.Length - start);
                    entry.EmbeddedData = new byte[Math.Max(0, embeddedLength)];
                    Array.Copy(block, start, entry.EmbeddedData, 0, entry.EmbeddedData.Length);
                    entry.AllocationDescriptors = Array.Empty<byte>();
                }
                else
                {
                    entry.AllocationDescriptors = new byte[Math.Max(0, allocationDescriptorsLength)];
                    Array.Copy(block, start, entry.AllocationDescriptors, 0,
                        Math.Min(entry.AllocationDescriptors.Length, block.Length - start));
                }

                return entry;
            }

            private byte[] ReadFileEntryData(UdfContext context, UdfFileEntry entry)
            {
                if ((entry.Flags & 0x7) == 3) return entry.EmbeddedData ?? Array.Empty<byte>();

                using var memory = new MemoryStream();
                var buffer = new byte[1 << 20];

                foreach (var extent in ParseExtents(context, entry))
                {
                    if (!extent.IsRecorded)
                    {
                        WriteZeros(memory, extent.Length, buffer);
                        continue;
                    }

                    _stream.Position = extent.Offset;
                    var remaining = extent.Length;

                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = _stream.Read(buffer, 0, toRead);
                        if (read <= 0) break;

                        memory.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }

                return memory.ToArray();
            }

            private static IEnumerable<Extent> ParseExtents(UdfContext context, UdfFileEntry entry)
            {
                var descriptorType = entry.Flags & 0x7;
                var offset = 0;
                var total = 0L;

                while (offset + 8 <= entry.AllocationDescriptors.Length && total < entry.InfoLength)
                {
                    long length;
                    long location;

                    if (descriptorType == 0)
                    {
                        var rawLength = Le32(entry.AllocationDescriptors, offset);
                        length = rawLength & 0x3FFFFFFF;
                        location = Le32(entry.AllocationDescriptors, offset + 4);
                        offset += 8;
                    }
                    else if (descriptorType == 1)
                    {
                        if (offset + 16 > entry.AllocationDescriptors.Length) break;

                        var rawLength = Le32(entry.AllocationDescriptors, offset);
                        length = rawLength & 0x3FFFFFFF;
                        location = Le32(entry.AllocationDescriptors, offset + 4);
                        offset += 16;
                    }
                    else if (descriptorType == 2)
                    {
                        if (offset + 20 > entry.AllocationDescriptors.Length) break;

                        var rawLength = Le32(entry.AllocationDescriptors, offset);
                        length = rawLength & 0x3FFFFFFF;
                        location = Le32(entry.AllocationDescriptors, offset + 8);
                        offset += 20;
                    }
                    else
                    {
                        break;
                    }

                    if (length == 0) break;

                    var raw = Le32(entry.AllocationDescriptors, offset - (descriptorType == 0 ? 8 : descriptorType == 1 ? 16 : 20));
                    var extentType = (raw >> 30) & 0x3;

                    length = Math.Min(length, entry.InfoLength - total);
                    if (length <= 0) break;

                    yield return new Extent
                    {
                        Offset = (context.PartitionStart + location) * SectorSize,
                        Length = length,
                        IsRecorded = extentType == 0
                    };

                    total += length;
                }
            }

            // ---------------- ISO9660 ----------------

            private MainVideo FindIso9660MainVideo()
            {
                var pvd = ReadSectors(16, 1);

                if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;

                var rootRecordOffset = 156;
                var rootExtent = Le32(pvd, rootRecordOffset + 2);
                var rootSize = Le32(pvd, rootRecordOffset + 10);

                var videoTs = FindIso9660Directory(rootExtent, rootSize, "VIDEO_TS");

                if (videoTs is null) return null;

                var vob = ListIso9660Files(videoTs.Extent, videoTs.Size)
                    .Where(f => f.Name.EndsWith(".vob", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.Size)
                    .FirstOrDefault();

                if (vob is null) return null;

                vob.Container = "vob";
                return vob;
            }

            private Iso9660Record FindIso9660Directory(long extent, long size, string name)
            {
                foreach (var record in ReadIso9660Records(extent, size))
                {
                    if (!record.IsDirectory) continue;

                    if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return record;
                    }
                }

                return null;
            }

            private List<MainVideo> ListIso9660Files(long extent, long size)
            {
                var results = new List<MainVideo>();

                foreach (var record in ReadIso9660Records(extent, size))
                {
                    if (record.IsDirectory) continue;

                    results.Add(new MainVideo
                    {
                        Name = record.Name,
                        Size = record.Size,
                        Extents = new List<Extent>
                        {
                            new Extent { Offset = record.Extent * SectorSize, Length = record.Size }
                        }
                    });
                }

                return results;
            }

            private IEnumerable<Iso9660Record> ReadIso9660Records(long extent, long size)
            {
                var data = new byte[Math.Min(size, 1 << 22)];
                _stream.Position = extent * SectorSize;
                var read = 0;

                while (read < data.Length)
                {
                    var count = _stream.Read(data, read, data.Length - read);
                    if (count <= 0) break;
                    read += count;
                }

                var offset = 0;

                while (offset + 33 < read)
                {
                    var recordLength = data[offset];

                    if (recordLength == 0)
                    {
                        // Records do not cross sector boundaries; skip to the next one.
                        offset = ((offset / SectorSize) + 1) * SectorSize;
                        continue;
                    }

                    if (offset + recordLength > read) break;

                    var recordExtent = Le32(data, offset + 2);
                    var recordSize = Le32(data, offset + 10);
                    var flags = data[offset + 25];
                    var nameLength = data[offset + 32];

                    if (nameLength > 0 && offset + 33 + nameLength <= read)
                    {
                        var name = Encoding.ASCII.GetString(data, offset + 33, nameLength);

                        if (name != "\u0000" && name != "\u0001")
                        {
                            name = name.Split(';')[0].TrimEnd('.');

                            yield return new Iso9660Record
                            {
                                Name = name,
                                Extent = recordExtent,
                                Size = recordSize,
                                IsDirectory = (flags & 0x02) != 0
                            };
                        }
                    }

                    offset += recordLength;
                }
            }

            private static ushort Le16(byte[] data, int offset)
            {
                return (ushort)(data[offset] | data[offset + 1] << 8);
            }

            private static uint Le32(byte[] data, int offset)
            {
                return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 |
                              data[offset + 3] << 24);
            }

            private static long Le64(byte[] data, int offset)
            {
                return (long)((ulong)Le32(data, offset) | (ulong)Le32(data, offset + 4) << 32);
            }
        }

        private sealed class UdfContext
        {
            public int PartitionStart { get; set; }
            public UdfFileEntry RootFileEntry { get; set; }
        }

        private sealed class UdfFileEntry
        {
            public string Name { get; set; }
            public bool IsDirectory { get; set; }
            public bool IsExtended { get; set; }
            public byte FileType { get; set; }
            public ushort Flags { get; set; }
            public long InfoLength { get; set; }
            public long Size => InfoLength;
            public byte[] AllocationDescriptors { get; set; }
            public byte[] EmbeddedData { get; set; }
        }

        private sealed class Iso9660Record
        {
            public string Name { get; set; }
            public long Extent { get; set; }
            public long Size { get; set; }
            public bool IsDirectory { get; set; }
        }
    }
}
