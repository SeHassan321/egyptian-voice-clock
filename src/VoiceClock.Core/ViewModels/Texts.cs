using System.Globalization;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Catalog;
using VoiceClock.Core.Resources;
using VoiceClock.Core.Storage;

namespace VoiceClock.Core.ViewModels;

/// <summary>Maps domain values to the localized UI text.</summary>
internal static class Texts
{
    public static string Format(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    public static string Category(AssetCategory category) => category switch
    {
        AssetCategory.Hour => Strings.Category_Hour,
        AssetCategory.RoundMinute => Strings.Category_RoundMinute,
        AssetCategory.ExactMinute => Strings.Category_ExactMinute,
        AssetCategory.DayPeriod => Strings.Category_DayPeriod,
        AssetCategory.Feedback => Strings.Category_Feedback,
        _ => Strings.Category_FullPhrase,
    };

    public static string Status(AssetStatus status, bool isRequired) => status.State switch
    {
        AssetState.Ready when status.Source == AssetSource.User => Strings.Status_User,
        AssetState.Ready => Strings.Status_Bundled,
        AssetState.Invalid => Strings.Status_Invalid,
        _ when !isRequired => Strings.Status_Optional,
        _ => Strings.Status_Missing,
    };

    /// <summary>The most important message about a fresh take.</summary>
    public static string Take(ProcessedClip clip)
    {
        var issues = clip.Issues;
        if (issues.Contains(ClipIssue.Empty))
        {
            return Strings.Recorder_Take_Empty;
        }

        if (issues.Contains(ClipIssue.NoSpeech))
        {
            return Strings.Recorder_Take_NoSpeech;
        }

        if (issues.Contains(ClipIssue.TooShort))
        {
            return Strings.Recorder_Take_TooShort;
        }

        if (issues.Contains(ClipIssue.TooLong))
        {
            return Strings.Recorder_Take_TooLong;
        }

        if (issues.Contains(ClipIssue.Clipped))
        {
            return Strings.Recorder_Take_Clipped;
        }

        if (issues.Contains(ClipIssue.TooQuiet))
        {
            return Strings.Recorder_Take_TooQuiet;
        }

        return Strings.Recorder_Take_Ok;
    }

    /// <summary>Times for the setup person, e.g. "1:43".</summary>
    public static string Clock(TimeOnly time)
    {
        var hour = time.Hour % 12 == 0 ? 12 : time.Hour % 12;
        return string.Create(CultureInfo.InvariantCulture, $"{hour}:{time.Minute:00}");
    }
}
