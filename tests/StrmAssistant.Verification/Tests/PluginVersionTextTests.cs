using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The settings-page update button compares the injected running version with the GitHub
    /// release tag. Assembly versions are four-part and Emby builds carry a non-numeric tag, so
    /// the text has to be normalized before it is injected into the client script.
    /// </summary>
    internal static class PluginVersionTextTests
    {
        public static void Run(TestRunner runner)
        {
            NumericVersions(runner);
            NonNumericTagsStayUnchanged(runner);
        }

        private static void NumericVersions(TestRunner runner)
        {
            runner.Section("V1. Numeric assembly versions render three-part release text");

            runner.EqualString("4-part trailing zero is dropped", "1.1.0",
                PluginVersionText.Normalize("1.1.0.0"));
            runner.EqualString("3-part version is kept", "1.0.16",
                PluginVersionText.Normalize("1.0.16"));
            runner.EqualString("leading v is stripped", "1.1.0",
                PluginVersionText.Normalize("v1.1.0"));
            runner.EqualString("meaningful fourth part is kept", "2.0.1.5",
                PluginVersionText.Normalize("2.0.1.5"));
            runner.EqualString("empty version stays empty", string.Empty,
                PluginVersionText.Normalize(null));
        }

        private static void NonNumericTagsStayUnchanged(TestRunner runner)
        {
            runner.Section("V2. Emby build tags are not mistaken for release versions");

            runner.EqualString("public-ip tag is untouched", "1.1.0.5",
                PluginVersionText.Normalize("1.1.0.5"));
        }
    }
}
