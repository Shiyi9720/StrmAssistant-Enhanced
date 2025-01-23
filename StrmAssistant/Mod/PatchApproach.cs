namespace StrmAssistant.Mod
{
    public enum PatchApproach
    {
        None = 0,
        Reflection = 1,
        Harmony = 2,
    }

    public class PatchApproachTracker
    {
        public PatchApproachTracker(string name)
        {
            Name = name;

            PatchManager.PatchTrackerList.Add(this);
        }

        public string Name { get; set; }

        public PatchApproach FallbackPatchApproach { get; set; } = PatchApproach.Harmony;

        public bool IsSupported { get; set; } = true;
    }
}
