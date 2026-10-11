using VoiceClock.Core.Catalog;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.Catalog;

public class RecordingCatalogTests
{
    private readonly TimeSpeechEngine _engine = new(SpeechRules.Default);
    private readonly RecordingCatalog _catalog;

    public RecordingCatalogTests()
    {
        _catalog = new RecordingCatalog(_engine);
    }

    [Fact]
    public void Catalog_has_twelve_hours_fifty_nine_minutes_two_periods_and_one_feedback_clip()
    {
        Assert.Equal(74, _catalog.Entries.Count);
        Assert.Equal(12, _catalog.Entries.Count(e => e.Category == AssetCategory.Hour));
        Assert.Equal(11, _catalog.Entries.Count(e => e.Category == AssetCategory.RoundMinute));
        Assert.Equal(48, _catalog.Entries.Count(e => e.Category == AssetCategory.ExactMinute));
        Assert.Equal(2, _catalog.Entries.Count(e => e.Category == AssetCategory.DayPeriod));
        Assert.Equal(1, _catalog.Entries.Count(e => e.Category == AssetCategory.Feedback));
    }

    [Fact]
    public void Required_counts_depend_on_the_day_period_setting()
    {
        Assert.Equal(71, _catalog.RequiredEntries(includeDayPeriod: false).Count());
        Assert.Equal(73, _catalog.RequiredEntries(includeDayPeriod: true).Count());
    }

    [Fact]
    public void Ids_are_unique_well_formed_and_ordered()
    {
        Assert.Equal(_catalog.Entries.Count, _catalog.Entries.Select(e => e.Id).Distinct().Count());
        Assert.All(_catalog.Entries, e => Assert.True(AssetIds.IsWellFormed(e.Id), e.Id));
        Assert.Equal(Enumerable.Range(0, _catalog.Entries.Count), _catalog.Entries.Select(e => e.Order));
    }

    [Fact]
    public void Every_asset_any_plan_can_request_is_in_the_catalog()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var includeDayPeriod in new[] { false, true })
        {
            for (var minuteOfDay = 0; minuteOfDay < 1440; minuteOfDay++)
            {
                var plan = _engine.BuildPlan(new TimeOnly(minuteOfDay / 60, minuteOfDay % 60), new TimeSpeechSettings(includeDayPeriod));
                foreach (var segment in plan.Segments)
                {
                    used.Add(segment.AssetId);
                    var entry = _catalog.Find(segment.AssetId);
                    Assert.NotNull(entry);
                    Assert.Equal(segment.Text, entry!.Text);
                }
            }
        }

        // Conversely, every required clip is actually used by some time (no wasted recordings).
        Assert.All(_catalog.RequiredEntries(includeDayPeriod: true), e => Assert.True(used.Contains(e.Id), e.Id));
        Assert.Equal(73, used.Count);
    }

    [Theory]
    [InlineData("minute.05", AssetCategory.RoundMinute)]
    [InlineData("minute.25", AssetCategory.RoundMinute)]
    [InlineData("minute.55", AssetCategory.RoundMinute)]
    [InlineData("minute.43", AssetCategory.ExactMinute)]
    [InlineData("minute.01", AssetCategory.ExactMinute)]
    [InlineData("hour.07", AssetCategory.Hour)]
    [InlineData("period.pm", AssetCategory.DayPeriod)]
    [InlineData("feedback.error", AssetCategory.Feedback)]
    public void Categories(string id, AssetCategory expected)
    {
        Assert.Equal(expected, _catalog.Find(id)!.Category);
    }

    [Fact]
    public void Full_phrase_entries_are_generated_on_demand_with_their_sentence()
    {
        var entry = _catalog.Find("phrase.01.43");

        Assert.NotNull(entry);
        Assert.Equal(AssetCategory.FullPhrase, entry!.Category);
        Assert.Equal("الساعة واحدة وتلاتة وأربعين دقيقة", entry.Text);
        Assert.Equal(AssetRequirement.Optional, entry.Requirement);
        Assert.Equal("الساعة واحدة إلا تلت", _catalog.FullPhrase(12, 40).Text);
        Assert.Equal("الساعة اتناشر", _catalog.FullPhrase(12, 0).Text);
    }

    [Theory]
    [InlineData("hour.13")]
    [InlineData("minute.00")]
    [InlineData("something")]
    [InlineData("phrase.00.10")]
    [InlineData(null)]
    public void Unknown_ids_are_not_found(string? id)
    {
        Assert.Null(_catalog.Find(id));
        Assert.False(_catalog.IsKnownId(id));
    }
}
