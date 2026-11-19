using System;
using System.Reflection;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Builds the argument list for Emby's <c>ThumbnailGenerator.RefreshThumbnailImages</c>.
    /// Emby changed this method's signature several times and a mismatch makes every reflective
    /// call throw <see cref="TargetParameterCountException"/>, so the runtime parameter count is
    /// used to pick the call shape instead of comparing Emby versions:
    ///   8 parameters  - item, videoStream, libraryOptions, directoryService, chapters,
    ///                   extractImages, saveChapters, cancellationToken
    ///   9 parameters  - adds mediaSource in front of videoStream (Emby 4.9.36+)
    ///   10 parameters - adds replaceExistingImages before saveChapters (Emby 4.10.2)
    /// Verified against Emby.Providers 4.10.2 metadata (10 parameters) and this plugin's
    /// historical Harmony stubs for the 8 and 9 parameter versions.
    /// </summary>
    internal static class ThumbnailRefreshSignature
    {
        internal const int MinimumParameterCount = 8;
        internal const int MaximumParameterCount = 10;

        /// <summary>
        /// Value for the 4.10.2-only <c>replaceExistingImages</c> argument. Emby's own
        /// ChapterImagesTask passes false (confirmed in Emby.Providers 4.10.2 IL); the flag opts
        /// into overwriting chapter images that already exist, and this plugin only processes
        /// items without chapter images, so false is both the native default and the behaviour
        /// of the 9-parameter method.
        /// </summary>
        internal const bool ReplaceExistingImages = false;

        internal static bool IsSupported(int parameterCount)
        {
            return parameterCount >= MinimumParameterCount && parameterCount <= MaximumParameterCount;
        }

        /// <summary>
        /// Returns the arguments in the order the method expects, or null when the signature is
        /// unknown. Callers must not invoke the method with a null result.
        /// </summary>
        internal static object[] Build(int parameterCount, object item, object mediaSource,
            object videoStream, object libraryOptions, object directoryService, object chapters,
            bool extractImages, bool saveChapters, object cancellationToken)
        {
            switch (parameterCount)
            {
                case 8:
                    return new[]
                    {
                        item, videoStream, libraryOptions, directoryService, chapters, extractImages,
                        saveChapters, cancellationToken
                    };
                case 9:
                    return new[]
                    {
                        item, mediaSource, videoStream, libraryOptions, directoryService, chapters,
                        extractImages, saveChapters, cancellationToken
                    };
                case 10:
                    return new[]
                    {
                        item, mediaSource, videoStream, libraryOptions, directoryService, chapters,
                        extractImages, ReplaceExistingImages, saveChapters, cancellationToken
                    };
                default:
                    return null;
            }
        }
    }
}
