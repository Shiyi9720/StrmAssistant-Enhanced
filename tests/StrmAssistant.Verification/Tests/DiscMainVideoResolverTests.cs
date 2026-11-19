using System;
using System.IO;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// BDMV/STREAM folders in the production library contain a .strm instead of an m2ts. These
    /// cases pin the resolution order (playlist is handled by the caller, then a real clip, then
    /// the shortcut target) and the .strm parsing rules that mirror Emby's StrmMounter.
    /// </summary>
    internal static class DiscMainVideoResolverTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("disc main video - strm target parsing");

            var root = CreateTempRoot();
            try
            {
                var strm = Path.Combine(root, "00000.strm");
                File.WriteAllText(strm,
                    "# generated shortcut\r\n\r\n\t \nhttp://172.17.0.1:9527/d/abc.m2ts?/00000.m2ts\nhttp://ignored/");

                runner.EqualString("first usable line wins and is trimmed",
                    "http://172.17.0.1:9527/d/abc.m2ts?/00000.m2ts",
                    DiscMainVideoResolver.ReadStrmTarget(strm));

                var spaced = Path.Combine(root, "spaced.strm");
                File.WriteAllText(spaced, "http://host/d/我的 电影.m2ts?/00000.m2ts");

                runner.EqualString("CJK and spaces in the target are percent encoded",
                    "http://host/d/%E6%88%91%E7%9A%84%20%E7%94%B5%E5%BD%B1.m2ts?/00000.m2ts",
                    DiscMainVideoResolver.ReadStrmTarget(spaced));

                var empty = Path.Combine(root, "empty.strm");
                File.WriteAllText(empty, "# only a comment\n\n");

                runner.Check("comment-only shortcut has no target",
                    DiscMainVideoResolver.ReadStrmTarget(empty) is null);

                runner.Check("missing shortcut has no target",
                    DiscMainVideoResolver.ReadStrmTarget(Path.Combine(root, "missing.strm")) is null);
                runner.Check("empty path has no target", DiscMainVideoResolver.ReadStrmTarget(null) is null);
            }
            finally
            {
                Cleanup(root);
            }

            runner.Section("disc main video - BDMV/STREAM resolution");

            root = CreateTempRoot();
            try
            {
                var stream = Path.Combine(root, "BDMV", "STREAM");
                Directory.CreateDirectory(stream);

                File.WriteAllText(Path.Combine(stream, "00000.strm"),
                    "http://172.17.0.1:9527/d/abc.m2ts?/00000.m2ts");

                runner.EqualString("shortcut-only structure resolves to the URL",
                    "http://172.17.0.1:9527/d/abc.m2ts?/00000.m2ts",
                    DiscMainVideoResolver.FindStreamSource(stream));

                File.WriteAllText(Path.Combine(stream, "poster.jpg"), new string('x', 4096));

                runner.EqualString("unrelated images are ignored",
                    "http://172.17.0.1:9527/d/abc.m2ts?/00000.m2ts",
                    DiscMainVideoResolver.FindStreamSource(stream));

                var smallClip = Path.Combine(stream, "00001.m2ts");
                var largeClip = Path.Combine(stream, "00002.M2TS");
                File.WriteAllText(smallClip, new string('s', 8));
                File.WriteAllText(largeClip, new string('l', 16));

                runner.EqualString("a real clip is preferred over the shortcut",
                    largeClip, DiscMainVideoResolver.FindStreamSource(stream));

                var biggerClip = Path.Combine(stream, "00003.mts");
                File.WriteAllText(biggerClip, new string('b', 64));

                runner.EqualString("largest clip wins",
                    biggerClip, DiscMainVideoResolver.FindStreamSource(stream));
            }
            finally
            {
                Cleanup(root);
            }

            root = CreateTempRoot();
            try
            {
                var stream = Path.Combine(root, "BDMV", "STREAM");
                Directory.CreateDirectory(stream);

                File.WriteAllText(Path.Combine(stream, "empty.strm"), "# no target\n");
                File.WriteAllText(Path.Combine(stream, "poster.jpg"), "image");

                runner.Check("a broken shortcut in STREAM resolves to nothing",
                    DiscMainVideoResolver.FindStreamSource(stream) is null);
                runner.Check("missing STREAM folder resolves to nothing",
                    DiscMainVideoResolver.FindStreamSource(Path.Combine(root, "nope")) is null);
            }
            finally
            {
                Cleanup(root);
            }

            runner.Section("disc main video - VIDEO_TS resolution");

            root = CreateTempRoot();
            try
            {
                var videoTs = Path.Combine(root, "VIDEO_TS");
                Directory.CreateDirectory(videoTs);
                var vob = Path.Combine(videoTs, "VTS_01_1.VOB");
                File.WriteAllText(vob, new string('v', 16));
                File.WriteAllText(Path.Combine(videoTs, "VIDEO_TS.IFO"), "ifo");

                runner.EqualString("DVD main title is the VOB",
                    vob, DiscMainVideoResolver.FindDvdSource(videoTs));
                runner.Check("empty VIDEO_TS resolves to nothing",
                    DiscMainVideoResolver.FindDvdSource(Path.Combine(root, "missing")) is null);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static string CreateTempRoot()
        {
            var path = Path.Combine(Path.GetTempPath(), "strmassistant-disc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Cleanup(string root)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception)
            {
                // best effort cleanup
            }
        }
    }
}
