using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// The standalone page for the per-library intro detection fingerprint length. The generic
    /// options page cannot render <see cref="StrmAssistant.Options.LibraryIntroDetectionFingerprintOptionCollection"/>
    /// on Emby 4.10 (stack overflow), so the settings live on this page instead.
    /// </summary>
    [Route("/StrmAssistant/IntroDetectionSettings", "GET", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetIntroDetectionSettingsPage
    {
    }

    [Route("/StrmAssistant/IntroDetectionSettings/Libraries", "GET", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetIntroDetectionSettings : IReturn<IntroDetectionSettingsResponse>
    {
    }

    [Route("/StrmAssistant/IntroDetectionSettings/Save", "POST", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class SaveIntroDetectionSettings : IReturn<SaveIntroDetectionSettingsResponse>
    {
        [ApiMember(Name = "Libraries", Description = "Per-library intro detection settings",
            IsRequired = true, DataType = "array", ParameterType = "body")]
        public List<IntroDetectionLibrarySetting> Libraries { get; set; }
    }

    public class IntroDetectionSettingsResponse
    {
        public int GlobalMinutes { get; set; }
        public bool IntroSkipEnabled { get; set; }
        public List<IntroDetectionLibrarySetting> Libraries { get; set; } =
            new List<IntroDetectionLibrarySetting>();
    }

    public class IntroDetectionLibrarySetting
    {
        public string LibraryId { get; set; }
        public string LibraryName { get; set; }
        public bool Override { get; set; }
        public int Minutes { get; set; }
    }

    public class SaveIntroDetectionSettingsResponse
    {
        public bool Success { get; set; }
    }
}
