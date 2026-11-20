using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Emby changed RefreshThumbnailImages from 8 to 9 to 10 parameters. A wrong argument count
    /// made every reflective call throw TargetParameterCountException, so these cases pin the
    /// argument order for each signature Emby has shipped. The "caller" section exercises the
    /// same BuildCall / ResolveMediaSource functions VideoThumbnailApi invokes, so a regression
    /// at the call site fails here even though the Emby-dependent class cannot be linked.
    /// </summary>
    internal static class ThumbnailRefreshSignatureTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("thumbnail refresh - parameter count support");

            runner.Check("8 parameters supported", ThumbnailRefreshSignature.IsSupported(8));
            runner.Check("9 parameters supported", ThumbnailRefreshSignature.IsSupported(9));
            runner.Check("10 parameters supported", ThumbnailRefreshSignature.IsSupported(10));
            runner.Check("7 parameters unsupported", !ThumbnailRefreshSignature.IsSupported(7));
            runner.Check("11 parameters unsupported", !ThumbnailRefreshSignature.IsSupported(11));
            runner.Check("0 parameters unsupported", !ThumbnailRefreshSignature.IsSupported(0));

            runner.Section("thumbnail refresh - 8 parameter argument order");

            var item = new object();
            var mediaSource = new object();
            var videoStream = new object();
            var libraryOptions = new object();
            var directoryService = new object();
            var chapters = new object();
            var cancellationToken = new CancellationTokenSource().Token;

            var eight = ThumbnailRefreshSignature.Build(8, item, mediaSource, videoStream,
                libraryOptions, directoryService, chapters, true, true, cancellationToken);

            CheckSignature(runner, "8 parameters", eight, typeof(FakeSignatures), "Eight",
                new (string Name, object Value)[]
                {
                    ("item", item), ("videoStream", videoStream), ("libraryOptions", libraryOptions),
                    ("directoryService", directoryService), ("chapters", chapters),
                    ("extractImages", true), ("saveChapters", true),
                    ("cancellationToken", cancellationToken)
                });

            runner.Section("thumbnail refresh - 9 parameter argument order");

            var nine = ThumbnailRefreshSignature.Build(9, item, mediaSource, videoStream,
                libraryOptions, directoryService, chapters, true, true, cancellationToken);

            CheckSignature(runner, "9 parameters", nine, typeof(FakeSignatures), "Nine",
                new (string Name, object Value)[]
                {
                    ("item", item), ("mediaSource", mediaSource), ("videoStream", videoStream),
                    ("libraryOptions", libraryOptions), ("directoryService", directoryService),
                    ("chapters", chapters), ("extractImages", true), ("saveChapters", true),
                    ("cancellationToken", cancellationToken)
                });

            runner.Section("thumbnail refresh - 10 parameter argument order");

            var ten = ThumbnailRefreshSignature.Build(10, item, mediaSource, videoStream,
                libraryOptions, directoryService, chapters, true, true, cancellationToken);

            CheckSignature(runner, "10 parameters", ten, typeof(FakeSignatures), "Ten",
                new (string Name, object Value)[]
                {
                    ("item", item), ("mediaSource", mediaSource), ("videoStream", videoStream),
                    ("libraryOptions", libraryOptions), ("directoryService", directoryService),
                    ("chapters", chapters), ("extractImages", true),
                    ("replaceExistingImages", false), ("saveChapters", true),
                    ("cancellationToken", cancellationToken)
                });

            runner.Section("thumbnail refresh - media source resolution follows the signature");

            var mediaSourceResolutions = 0;

            Func<object> resolveMediaSource = () =>
            {
                mediaSourceResolutions++;
                return mediaSource;
            };

            runner.Check("8 parameters do not resolve a media source",
                ThumbnailRefreshSignature.ResolveMediaSource(8, resolveMediaSource) is null &&
                mediaSourceResolutions == 0);

            var nineMediaSource = ThumbnailRefreshSignature.ResolveMediaSource(9, resolveMediaSource);

            runner.Check("9 parameters resolve the media source once",
                ReferenceEquals(nineMediaSource, mediaSource) && mediaSourceResolutions == 1);

            var tenMediaSource = ThumbnailRefreshSignature.ResolveMediaSource(10, resolveMediaSource);

            runner.Check("10 parameters resolve the media source once",
                ReferenceEquals(tenMediaSource, mediaSource) && mediaSourceResolutions == 2);

            runner.Section("thumbnail refresh - caller argument array (VideoThumbnailApi)");

            var eightCall = ThumbnailRefreshSignature.BuildCall(8, item, mediaSource, videoStream,
                libraryOptions, directoryService, chapters, true, false, cancellationToken);

            CheckSignature(runner, "caller 8 parameters", eightCall, typeof(FakeSignatures), "Eight",
                new (string Name, object Value)[]
                {
                    ("item", item), ("videoStream", videoStream), ("libraryOptions", libraryOptions),
                    ("directoryService", directoryService), ("chapters", chapters),
                    ("extractImages", true), ("saveChapters", false),
                    ("cancellationToken", cancellationToken)
                });

            var nineCall = ThumbnailRefreshSignature.BuildCall(9, item, mediaSource, videoStream,
                libraryOptions, directoryService, chapters, true, false, cancellationToken);

            CheckSignature(runner, "caller 9 parameters", nineCall, typeof(FakeSignatures), "Nine",
                new (string Name, object Value)[]
                {
                    ("item", item), ("mediaSource", mediaSource), ("videoStream", videoStream),
                    ("libraryOptions", libraryOptions), ("directoryService", directoryService),
                    ("chapters", chapters), ("extractImages", true), ("saveChapters", false),
                    ("cancellationToken", cancellationToken)
                });

            var tenCall = ThumbnailRefreshSignature.BuildCall(10, item, mediaSource, videoStream,
                libraryOptions, directoryService, chapters, true, false, cancellationToken);

            CheckSignature(runner, "caller 10 parameters", tenCall, typeof(FakeSignatures), "Ten",
                new (string Name, object Value)[]
                {
                    ("item", item), ("mediaSource", mediaSource), ("videoStream", videoStream),
                    ("libraryOptions", libraryOptions), ("directoryService", directoryService),
                    ("chapters", chapters), ("extractImages", true),
                    ("replaceExistingImages", false), ("saveChapters", false),
                    ("cancellationToken", cancellationToken)
                });

            runner.Check("caller 8 parameters ignore a non-null media source",
                eightCall != null && eightCall.Length == 8 &&
                !eightCall.Contains(mediaSource));

            runner.Check("caller build returns null for 7 parameters",
                ThumbnailRefreshSignature.BuildCall(7, item, mediaSource, videoStream, libraryOptions,
                    directoryService, chapters, true, false, cancellationToken) is null);
            runner.Check("caller build returns null for 11 parameters",
                ThumbnailRefreshSignature.BuildCall(11, item, mediaSource, videoStream, libraryOptions,
                    directoryService, chapters, true, false, cancellationToken) is null);

            runner.Section("thumbnail refresh - unknown signatures are refused");

            foreach (var count in new[] { 7, 11, 0 })
            {
                var built = ThumbnailRefreshSignature.Build(count, item, mediaSource, videoStream,
                    libraryOptions, directoryService, chapters, true, true, cancellationToken);

                runner.Check($"build returns null for {count} parameters", built is null);
            }
        }

        private static void CheckSignature(TestRunner runner, string label, object[] arguments,
            Type type, string methodName, (string Name, object Value)[] expected)
        {
            var parameters = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetParameters();

            if (parameters is null)
            {
                runner.Check(label + " fake signature exists", false);
                return;
            }

            runner.Check(label + " argument count", arguments != null &&
                                                  arguments.Length == parameters.Length,
                $"expected {parameters.Length}, actual {arguments?.Length}");

            if (arguments is null || arguments.Length != parameters.Length) return;

            foreach (var pair in expected)
            {
                var index = Array.FindIndex(parameters, p => p.Name == pair.Name);

                if (index < 0)
                {
                    runner.Check(label + " parameter " + pair.Name + " exists", false);
                    continue;
                }

                runner.Check(label + " argument " + pair.Name,
                    Equals(arguments[index], pair.Value),
                    "expected " + Format(pair.Value) + ", actual " + Format(arguments[index]));
            }

            var known = expected.Select(e => e.Name).ToArray();
            var extra = parameters.Where(p => !known.Contains(p.Name)).Select(p => p.Name).ToArray();

            runner.Check(label + " fake signature has no uncovered parameter", extra.Length == 0,
                string.Join(", ", extra));
        }

        private static string Format(object value)
        {
            return value is bool b ? b.ToString() : value?.ToString() ?? "<null>";
        }

        private static class FakeSignatures
        {
            private static void Eight(object item, object videoStream, object libraryOptions,
                object directoryService, object chapters, bool extractImages, bool saveChapters,
                CancellationToken cancellationToken)
            {
            }

            private static void Nine(object item, object mediaSource, object videoStream,
                object libraryOptions, object directoryService, object chapters, bool extractImages,
                bool saveChapters, CancellationToken cancellationToken)
            {
            }

            private static void Ten(object item, object mediaSource, object videoStream,
                object libraryOptions, object directoryService, object chapters, bool extractImages,
                bool replaceExistingImages, bool saveChapters, CancellationToken cancellationToken)
            {
            }
        }
    }
}
