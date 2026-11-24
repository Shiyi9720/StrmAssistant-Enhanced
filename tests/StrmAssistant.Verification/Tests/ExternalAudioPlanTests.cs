using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// External audio comparison drives when extraction re-merges sibling audio tracks. It must
    /// be path-insensitive (the sidecar only keeps file names) and ignore empty entries.
    /// </summary>
    internal static class ExternalAudioPlanTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("EA1. External audio change detection");

            runner.Check("same names in another folder are unchanged",
                !ExternalAudioPlan.HasChanged(
                    new[] { "/media/movie/Movie.eng.flac" },
                    new[] { "/other/movie/Movie.eng.flac" }));

            runner.Check("new file is a change",
                ExternalAudioPlan.HasChanged(
                    new[] { "/media/movie/Movie.eng.flac" },
                    new[] { "/media/movie/Movie.eng.flac", "/media/movie/Movie.chi.dtshd" }));

            runner.Check("deleted file is a change",
                ExternalAudioPlan.HasChanged(
                    new[] { "/media/movie/Movie.eng.flac", "/media/movie/Movie.chi.dtshd" },
                    new[] { "/media/movie/Movie.eng.flac" }));

            runner.Check("case differences are ignored",
                !ExternalAudioPlan.HasChanged(
                    new[] { "/media/movie/MOVIE.ENG.FLAC" },
                    new[] { "/media/movie/movie.eng.flac" }));

            runner.Check("empty paths are ignored",
                !ExternalAudioPlan.HasChanged(new[] { null, "" }, new string[0]));

            runner.Check("both empty is unchanged",
                !ExternalAudioPlan.HasChanged(null, null));

            runner.Check("first scan is a change",
                ExternalAudioPlan.HasChanged(null, new[] { "/media/movie/Movie.eng.flac" }));
        }
    }
}
