using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Dolby Vision Profile 5 has no HDR10 compatible base layer. Emby's own ffmpeg ignores the
    /// DOVI reshaping metadata, so extracted chapter images get a strong green cast. A custom
    /// ffmpeg build with Vulkan + libplacebo applies the reshaping before tone mapping.
    /// This helper extracts the requested frames with that build instead of Emby's extractors.
    /// </summary>
    internal static class DvP5Screenshot
    {
        private const string LibplaceboFilter =
            "libplacebo=tonemapping=auto:colorspace=bt709:color_primaries=bt709:color_trc=bt709:format=yuv420p";

        private const string ZscaleFilter =
            "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709," +
            "tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

        private const int FrameTimeoutMs = 120000;

        internal static bool IsDolbyVisionProfile5(MediaStream videoStream)
        {
            return videoStream != null && videoStream.Type == MediaStreamType.Video &&
                   videoStream.ExtendedVideoSubType == ExtendedVideoSubTypes.DoviProfile50;
        }

        internal static bool CanHandle(IFfmpegManager ffmpegManager, MediaStream videoStream)
        {
            return IsDolbyVisionProfile5(videoStream) && IsAvailable(ffmpegManager);
        }

        /// <summary>
        /// True when a custom ffmpeg build is configured and passes the Vulkan + libplacebo
        /// self check. The worker uses this before extracting any frame so a node without Vulkan
        /// fails cleanly instead of writing a wrongly tonemapped (green) image.
        /// </summary>
        internal static bool IsAvailable(IFfmpegManager ffmpegManager)
        {
            return !string.IsNullOrEmpty(FfmpegApi.GetCustomFfmpegPath()) &&
                   FfmpegApi.CheckCustomFfmpeg(ffmpegManager);
        }

        internal static async Task<string> ExtractSingleImageAsync(IFfmpegManager ffmpegManager, string inputPath,
            TimeSpan offset, CancellationToken cancellationToken)
        {
            var targetPath = Path.Combine(Path.GetTempPath(),
                "strmassistant-dvp5-" + Guid.NewGuid().ToString("N") + ".jpg");

            var success = await ExtractFrameAsync(ffmpegManager, inputPath, offset, targetPath, cancellationToken)
                .ConfigureAwait(false);

            if (success && File.Exists(targetPath)) return targetPath;

            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            catch
            {
                // ignored
            }

            return null;
        }

        internal static async Task<List<TimeSpan>> ExtractImagesFromListAsync(IFfmpegManager ffmpegManager,
            string inputPath, IEnumerable<TimeSpan> extractionTimes, string targetDirectory, string targetFilename,
            CancellationToken cancellationToken)
        {
            var times = extractionTimes?.Distinct().OrderBy(t => t).ToList() ?? new List<TimeSpan>();

            if (times.Count == 0) return times;

            Directory.CreateDirectory(targetDirectory);

            var extracted = new List<TimeSpan>();

            for (var i = 0; i < times.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ThumbnailGenerator maps the n-th distinct chapter time to the file whose name
                // ends with the zero padded (n + 2) number.
                var targetPath = Path.Combine(targetDirectory, BuildFileName(targetFilename, i + 2));

                var success = await ExtractFrameAsync(ffmpegManager, inputPath, times[i], targetPath,
                    cancellationToken).ConfigureAwait(false);

                if (success && File.Exists(targetPath))
                {
                    extracted.Add(times[i]);
                }
                else
                {
                    try
                    {
                        if (File.Exists(targetPath)) File.Delete(targetPath);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            return extracted;
        }

        private static string BuildFileName(string targetFilename, int number)
        {
            if (string.IsNullOrEmpty(targetFilename)) return number.ToString("D5") + ".jpg";

            var formatted = targetFilename.Replace("%05d", number.ToString("D5"))
                .Replace("%d", number.ToString(CultureInfo.InvariantCulture));

            return formatted;
        }

        private static async Task<bool> ExtractFrameAsync(IFfmpegManager ffmpegManager, string inputPath,
            TimeSpan offset, string targetPath, CancellationToken cancellationToken)
        {
            var exePath = FfmpegApi.GetCustomFfmpegPath();

            if (string.IsNullOrEmpty(exePath)) return false;

            inputPath = StrmUrlHelper.Normalize(inputPath);

            var seconds = Math.Max(0, offset.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

            var filter = LibplaceboFilter;

            var arguments = string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -nostdin -ss {0} -i \"{1}\" -map 0:v:0 -an -sn -frames:v 1 -vf \"{2}\" -q:v 2 -y \"{3}\"",
                seconds, inputPath, filter, targetPath);

            var logger = Plugin.Instance?.Logger;

            var success = await FfmpegApi.RunAsync(ffmpegManager, exePath, arguments, "DvP5Screenshot",
                FrameTimeoutMs, cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                logger?.Debug("DvP5Screenshot - libplacebo extraction failed, retrying with zscale: " + inputPath);

                arguments = string.Format(CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error -nostdin -ss {0} -i \"{1}\" -map 0:v:0 -an -sn -frames:v 1 -vf \"{2}\" -q:v 2 -y \"{3}\"",
                    seconds, inputPath, ZscaleFilter, targetPath);

                success = await FfmpegApi.RunAsync(ffmpegManager, exePath, arguments, "DvP5Screenshot",
                    FrameTimeoutMs, cancellationToken).ConfigureAwait(false);
            }

            return success;
        }
    }
}
