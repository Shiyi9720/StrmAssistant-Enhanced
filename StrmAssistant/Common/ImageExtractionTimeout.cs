using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby's image extractors use fixed timeouts (10 minutes for chapter images and
    /// 30 minutes for BIF on the quick path). This wraps the image extraction manager
    /// and scales those timeouts by the user configured multiplier, so slow network
    /// storages get enough time to finish.
    /// </summary>
    public static class ImageExtractionTimeout
    {
        private const string ImageExtractionAssembly = "Emby.Server.MediaEncoding";
        private const string ImageExtractionNamespace = "Emby.Server.MediaEncoding.ImageExtraction.";

        public static IImageExtractionManager Wrap(IImageExtractionManager inner, IFfmpegManager ffmpegManager,
            IServerApplicationHost applicationHost)
        {
            if (inner is null || inner is ImageExtractionManagerWithTimeout) return inner;

            return new ImageExtractionManagerWithTimeout(inner, ffmpegManager, applicationHost);
        }

        private sealed class ImageExtractionManagerWithTimeout : IImageExtractionManager
        {
            private readonly IImageExtractionManager _inner;
            private readonly IFfmpegManager _ffmpegManager;
            private readonly IServerApplicationHost _applicationHost;

            public ImageExtractionManagerWithTimeout(IImageExtractionManager inner, IFfmpegManager ffmpegManager,
                IServerApplicationHost applicationHost)
            {
                _inner = inner;
                _ffmpegManager = ffmpegManager;
                _applicationHost = applicationHost;
            }

            public Task<string> ExtractAudioImage(MediaSourceInfo mediaSource, string inputPath, int? streamIndex,
                CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                return _inner.ExtractAudioImage(mediaSource, inputPath, streamIndex, cancellationToken);
            }

            public Task<string> ExtractAudioImage(MediaSourceInfo mediaSource, string inputPath,
                MediaContainers? container, int? streamIndex, CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                return _inner.ExtractAudioImage(mediaSource, inputPath, container, streamIndex, cancellationToken);
            }

            public Task<string> ExtractVideoImage(MediaSourceInfo mediaSource, string inputPath,
                MediaContainers? container, MediaProtocol protocol, MediaStream videoStream,
                Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                return _inner.ExtractVideoImage(mediaSource, inputPath, container, protocol, videoStream, threedFormat,
                    offset, cancellationToken);
            }

            public Task<string> ExtractVideoImage(MediaSourceInfo mediaSource, string inputPath,
                MediaContainers? container, MediaProtocol protocol, MediaStream imageStream, int? streamIndex,
                CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                return _inner.ExtractVideoImage(mediaSource, inputPath, container, protocol, imageStream, streamIndex,
                    cancellationToken);
            }

            public Task ExtractVideoImagesOnInterval(MediaSourceInfo mediaSource, string inputPath,
                MediaContainers? container, MediaStream videoStream, MediaProtocol protocol,
                Video3DFormat? threedFormat, TimeSpan interval, string targetDirectory, string filenamePrefix,
                int? maxWidth, CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                try
                {
                    var extractorType = EnableQuickImageSeriesExtractor(threedFormat, videoStream, interval, maxWidth)
                        ? "QuickImageSeriesExtractor"
                        : "ImageSeriesExtractor";
                    var extractor = CreateExtractor(extractorType);

                    if (extractor != null)
                    {
                        var method = FindRunExtraction(extractor.GetType(), 14);

                        if (method != null)
                        {
                            return (Task)method.Invoke(extractor, new object[]
                            {
                                mediaSource, inputPath, container, videoStream, protocol, null, null, null, interval,
                                targetDirectory, filenamePrefix + "%05d.jpg", maxWidth, false, cancellationToken
                            });
                        }
                    }
                }
                catch (Exception e)
                {
                    LogDebug(e);
                }

                return _inner.ExtractVideoImagesOnInterval(mediaSource, inputPath, container, videoStream, protocol,
                    threedFormat, interval, targetDirectory, filenamePrefix, maxWidth, cancellationToken);
            }

            [Obsolete("Use ExtractVideoImagesFromList2() instead.")]
            public async Task ExtractVideoImagesFromList(MediaSourceInfo mediaSource, string inputPath,
                MediaContainers? container, MediaStream videoStream, MediaProtocol protocol,
                Video3DFormat? threedFormat, TimeSpan[] extractionTimes, string targetDirectory,
                string filenamePrefix, int? maxWidth, CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                await ExtractVideoImagesFromList2(mediaSource, inputPath, container, videoStream, protocol,
                    threedFormat, extractionTimes, targetDirectory, filenamePrefix, maxWidth, cancellationToken)
                    .ConfigureAwait(false);
            }

            public async Task<List<TimeSpan>> ExtractVideoImagesFromList2(MediaSourceInfo mediaSource,
                string inputPath, MediaContainers? container, MediaStream videoStream, MediaProtocol protocol,
                Video3DFormat? threedFormat, TimeSpan[] extractionTimes, string targetDirectory,
                string filenamePrefix, int? maxWidth, CancellationToken cancellationToken)
            {
                inputPath = StrmUrlHelper.Normalize(inputPath);

                try
                {
                    var extractor = CreateExtractor("MultiImageExtractor");

                    if (extractor != null)
                    {
                        var method = FindRunExtraction(extractor.GetType(), 13);

                        if (method != null)
                        {
                            var task = (Task)method.Invoke(extractor, new object[]
                            {
                                mediaSource, inputPath, container, videoStream, protocol, null, null, extractionTimes,
                                targetDirectory, filenamePrefix + "%05d.jpg", maxWidth, false, cancellationToken
                            });

                            await task.ConfigureAwait(false);

                            return task.GetType().GetProperty("Result")?.GetValue(task) as List<TimeSpan> ??
                                   new List<TimeSpan>();
                        }
                    }
                }
                catch (Exception e)
                {
                    LogDebug(e);
                }

                return await _inner.ExtractVideoImagesFromList2(mediaSource, inputPath, container, videoStream,
                    protocol, threedFormat, extractionTimes, targetDirectory, filenamePrefix, maxWidth,
                    cancellationToken).ConfigureAwait(false);
            }

            private object CreateExtractor(string typeName)
            {
                if (_applicationHost is null) return null;

                var extractorType = Assembly.Load(ImageExtractionAssembly)
                    .GetType(ImageExtractionNamespace + typeName, false);

                if (extractorType is null) return null;

                var extractor = Activator.CreateInstance(extractorType, _applicationHost);
                var timeoutProperty = extractorType.BaseType?.GetProperty("TotalTimeoutMs",
                    BindingFlags.Public | BindingFlags.Instance);

                if (timeoutProperty?.CanRead == true && timeoutProperty.CanWrite)
                {
                    var defaultTimeout = Convert.ToInt32(timeoutProperty.GetValue(extractor));
                    timeoutProperty.SetValue(extractor,
                        ChapterApi.ApplyImageExtractionTimeoutMultiplier(defaultTimeout));
                }

                return extractor;
            }

            private static MethodInfo FindRunExtraction(Type extractorType, int parameterCount)
            {
                return extractorType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "RunExtraction" &&
                                         m.GetParameters().Length == parameterCount);
            }

            private bool EnableQuickImageSeriesExtractor(Video3DFormat? threedFormat, MediaStream videoStream,
                TimeSpan interval, int? maxWidth)
            {
                if (threedFormat.HasValue && threedFormat.Value != Video3DFormat.MVC) return false;

                return _ffmpegManager?.FfmpegCapabilities?.SupportsSkipInterval == true &&
                       interval.TotalSeconds >= 3 &&
                       (!maxWidth.HasValue ||
                        videoStream != null && videoStream.Width.HasValue && videoStream.Height.HasValue);
            }

            private static void LogDebug(Exception exception)
            {
                if (Plugin.Instance?.DebugMode != true) return;

                Plugin.Instance.Logger.Debug("ImageExtractionTimeout - " + exception.Message);
                Plugin.Instance.Logger.Debug(exception.StackTrace);
            }
        }
    }
}
