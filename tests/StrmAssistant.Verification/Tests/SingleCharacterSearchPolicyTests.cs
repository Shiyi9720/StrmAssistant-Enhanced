using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Bounds for the optional single-character contains tier. The switch is off by default;
    /// when it is on, only a single CJK character with a bounded posting is expanded, so common
    /// characters cannot blow up candidate collection.
    /// </summary>
    internal static class SingleCharacterSearchPolicyTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("single character search - switch and bounds");

            runner.Check("disabled switch never allows contains",
                !SingleCharacterSearchPolicy.AllowsContains(false, "蛛", true));
            runner.Check("disabled switch never uses a posting",
                !SingleCharacterSearchPolicy.MayUsePosting(false, "蛛", true, 10));

            runner.Check("enabled switch allows one CJK character",
                SingleCharacterSearchPolicy.AllowsContains(true, "蛛", true));
            runner.Check("enabled switch rejects two characters",
                !SingleCharacterSearchPolicy.AllowsContains(true, "蜘蛛", true));
            runner.Check("enabled switch rejects a non-CJK single character",
                !SingleCharacterSearchPolicy.AllowsContains(true, "a", false));

            runner.Check("posting within the cap is used",
                SingleCharacterSearchPolicy.MayUsePosting(true, "蛛", true, 10));
            runner.Check("empty posting is not used",
                !SingleCharacterSearchPolicy.MayUsePosting(true, "蛛", true, 0));
            runner.Check("posting at the cap is used",
                SingleCharacterSearchPolicy.MayUsePosting(true, "蛛", true,
                    SingleCharacterSearchPolicy.MaxCandidates));
            runner.Check("posting above the cap is ignored",
                !SingleCharacterSearchPolicy.MayUsePosting(true, "蛛", true,
                    SingleCharacterSearchPolicy.MaxCandidates + 1));

            runner.Check("unigram build cap is above the per-query cap",
                SingleCharacterSearchPolicy.MaxUnigramPostings >
                SingleCharacterSearchPolicy.MaxCandidates);
        }
    }
}
