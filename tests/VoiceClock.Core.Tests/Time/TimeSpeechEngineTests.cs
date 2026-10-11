using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.Time;

public class TimeSpeechEngineTests
{
    private static readonly int[] ToNextHourMinutes = [40, 45, 50, 55];

    private readonly TimeSpeechEngine _engine = new(SpeechRules.Default);

    private string Say(int hour24, int minute, bool includeDayPeriod = false) =>
        _engine.BuildPlan(new TimeOnly(hour24, minute), new TimeSpeechSettings(includeDayPeriod)).Text;

    // The acceptance examples from the project brief (12-hour times, checked in both halves of the day).
    [Theory]
    [InlineData(1, 0, "الساعة واحدة")]
    [InlineData(1, 5, "الساعة واحدة وخمسة")]
    [InlineData(1, 15, "الساعة واحدة وربع")]
    [InlineData(1, 20, "الساعة واحدة وتلت")]
    [InlineData(1, 30, "الساعة واحدة ونص")]
    [InlineData(1, 35, "الساعة واحدة ونص وخمسة")]
    [InlineData(1, 40, "الساعة اتنين إلا تلت")]
    [InlineData(1, 45, "الساعة اتنين إلا ربع")]
    [InlineData(1, 50, "الساعة اتنين إلا عشرة")]
    [InlineData(1, 55, "الساعة اتنين إلا خمسة")]
    [InlineData(11, 40, "الساعة اتناشر إلا تلت")]
    [InlineData(11, 50, "الساعة اتناشر إلا عشرة")]
    [InlineData(11, 55, "الساعة اتناشر إلا خمسة")]
    [InlineData(12, 0, "الساعة اتناشر")]
    [InlineData(12, 40, "الساعة واحدة إلا تلت")]
    [InlineData(12, 50, "الساعة واحدة إلا عشرة")]
    public void Acceptance_examples_from_the_brief(int hour12, int minute, string expected)
    {
        var morning = hour12 == 12 ? 0 : hour12;
        var evening = hour12 == 12 ? 12 : hour12 + 12;

        Assert.Equal(expected, Say(morning, minute));
        Assert.Equal(expected, Say(evening, minute));
    }

    // Decisions made by the owner (Hassan) for minutes that are not multiples of five.
    [Theory]
    [InlineData(1, 1, "الساعة واحدة ودقيقة")]
    [InlineData(1, 2, "الساعة واحدة ودقيقتين")]
    [InlineData(1, 3, "الساعة واحدة وتلات دقايق")]
    [InlineData(1, 10, "الساعة واحدة وعشرة")]
    [InlineData(1, 24, "الساعة واحدة وأربعة وعشرين دقيقة")]
    [InlineData(1, 25, "الساعة واحدة ونص إلا خمسة")]
    [InlineData(1, 43, "الساعة واحدة وتلاتة وأربعين دقيقة")]
    [InlineData(1, 58, "الساعة واحدة وتمانية وخمسين دقيقة")]
    [InlineData(12, 43, "الساعة اتناشر وتلاتة وأربعين دقيقة")]
    [InlineData(11, 59, "الساعة حداشر وتسعة وخمسين دقيقة")]
    public void Owner_decisions_for_exact_minutes(int hour12, int minute, string expected)
    {
        Assert.Equal(expected, Say(hour12 % 12, minute));
        Assert.Equal(expected, Say((hour12 % 12) + 12, minute));
    }

    [Fact]
    public void Every_minute_of_the_day_has_a_plan_with_the_expected_structure()
    {
        var checkedMinutes = 0;
        for (var hour24 = 0; hour24 < 24; hour24++)
        {
            for (var minute = 0; minute < 60; minute++)
            {
                var plan = _engine.BuildPlan(new TimeOnly(hour24, minute), TimeSpeechSettings.Default);

                // Expectations derived independently of the engine.
                var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
                var usesNextHour = ToNextHourMinutes.Contains(minute);
                var expectedHour = usesNextHour ? (hour12 == 12 ? 1 : hour12 + 1) : hour12;

                Assert.Equal(hour12, plan.Hour12);
                Assert.Equal(expectedHour, plan.SpokenHour);
                Assert.Equal(minute == 0 ? 1 : 2, plan.Segments.Count);
                Assert.Equal($"hour.{expectedHour:00}", plan.Segments[0].AssetId);
                Assert.Equal(SegmentRole.Hour, plan.Segments[0].Role);
                Assert.StartsWith("الساعة ", plan.Segments[0].Text);

                if (minute == 0)
                {
                    Assert.Equal(MinuteRelation.OnTheHour, plan.Relation);
                }
                else
                {
                    Assert.Equal($"minute.{minute:00}", plan.Segments[1].AssetId);
                    Assert.Equal(SegmentRole.Minute, plan.Segments[1].Role);
                    Assert.Equal(usesNextHour ? MinuteRelation.ToNextHour : MinuteRelation.PastCurrentHour, plan.Relation);
                    Assert.StartsWith(usesNextHour ? "إلا " : "و", plan.Segments[1].Text);
                }

                Assert.Null(plan.DayPeriodSegment);
                checkedMinutes++;
            }
        }

        Assert.Equal(1440, checkedMinutes);
    }

    [Fact]
    public void The_720_times_of_a_12_hour_cycle_produce_720_distinct_sentences()
    {
        var sentences = new HashSet<string>(StringComparer.Ordinal);
        for (var hour24 = 0; hour24 < 12; hour24++)
        {
            for (var minute = 0; minute < 60; minute++)
            {
                var morning = Say(hour24, minute);
                var evening = Say(hour24 + 12, minute);
                Assert.Equal(morning, evening);
                Assert.True(sentences.Add(morning), $"Duplicate sentence: {morning}");
            }
        }

        Assert.Equal(720, sentences.Count);
    }

    [Theory]
    [InlineData(12, 40, 1)]
    [InlineData(0, 40, 1)]
    [InlineData(0, 55, 1)]
    [InlineData(11, 45, 12)]
    [InlineData(23, 55, 12)]
    [InlineData(23, 59, 11)]
    [InlineData(12, 39, 12)]
    public void Next_hour_wraps_from_12_to_1(int hour24, int minute, int expectedSpokenHour)
    {
        var plan = _engine.BuildPlan(new TimeOnly(hour24, minute), TimeSpeechSettings.Default);
        Assert.Equal(expectedSpokenHour, plan.SpokenHour);
    }

    [Fact]
    public void Midnight_and_noon_are_both_twelve_with_the_right_day_period()
    {
        var midnight = _engine.BuildPlan(new TimeOnly(0, 0), new TimeSpeechSettings(IncludeDayPeriod: true));
        var noon = _engine.BuildPlan(new TimeOnly(12, 0), new TimeSpeechSettings(IncludeDayPeriod: true));

        Assert.Equal(12, midnight.Hour12);
        Assert.Equal(12, noon.Hour12);
        Assert.Equal(DayPeriod.Am, midnight.DayPeriod);
        Assert.Equal(DayPeriod.Pm, noon.DayPeriod);
        Assert.Equal("الساعة اتناشر صباحًا", midnight.Text);
        Assert.Equal("الساعة اتناشر مساءً", noon.Text);
    }

    [Theory]
    [InlineData(9, 15, "الساعة تسعة وربع صباحًا")]
    [InlineData(11, 40, "الساعة اتناشر إلا تلت صباحًا")]
    [InlineData(11, 59, "الساعة حداشر وتسعة وخمسين دقيقة صباحًا")]
    [InlineData(13, 43, "الساعة واحدة وتلاتة وأربعين دقيقة مساءً")]
    [InlineData(23, 40, "الساعة اتناشر إلا تلت مساءً")]
    [InlineData(20, 0, "الساعة تمانية مساءً")]
    public void Day_period_follows_the_real_time_and_comes_last(int hour24, int minute, string expected)
    {
        var plan = _engine.BuildPlan(new TimeOnly(hour24, minute), new TimeSpeechSettings(IncludeDayPeriod: true));

        Assert.Equal(expected, plan.Text);
        Assert.Equal(SegmentRole.DayPeriod, plan.Segments[^1].Role);
        Assert.Equal(hour24 < 12 ? "period.am" : "period.pm", plan.Segments[^1].AssetId);
        Assert.Equal(plan.Segments.Count - 1, plan.TimeSegments.Count);
    }

    [Fact]
    public void Day_period_is_off_by_default()
    {
        var plan = _engine.BuildPlan(new TimeOnly(20, 30), TimeSpeechSettings.Default);
        Assert.Equal("الساعة تمانية ونص", plan.Text);
        Assert.False(plan.IncludesDayPeriod);
    }

    [Fact]
    public void Segments_are_ordered_hour_then_minute_then_day_period()
    {
        var plan = _engine.BuildPlan(new TimeOnly(14, 45), new TimeSpeechSettings(IncludeDayPeriod: true));

        Assert.Equal(
            new[] { "hour.03", "minute.45", "period.pm" },
            plan.Segments.Select(s => s.AssetId).ToArray());
        Assert.Equal(
            new[] { SegmentRole.Hour, SegmentRole.Minute, SegmentRole.DayPeriod },
            plan.Segments.Select(s => s.Role).ToArray());
    }

    [Fact]
    public void Identical_inputs_give_identical_plans_even_across_engine_instances()
    {
        var other = new TimeSpeechEngine(SpeechRules.Default);
        for (var hour24 = 0; hour24 < 24; hour24++)
        {
            for (var minute = 0; minute < 60; minute++)
            {
                var time = new TimeOnly(hour24, minute);
                var settings = new TimeSpeechSettings(IncludeDayPeriod: minute % 2 == 0);
                var a = _engine.BuildPlan(time, settings);
                var b = _engine.BuildPlan(time, settings);
                var c = other.BuildPlan(time, settings);

                Assert.Equal(a.Segments, b.Segments);
                Assert.Equal(a.Segments, c.Segments);
                Assert.Equal(a.Text, c.Text);
            }
        }
    }

    [Fact]
    public void Seconds_are_ignored()
    {
        var plan = _engine.BuildPlan(new TimeOnly(1, 40, 59, 999), TimeSpeechSettings.Default);
        Assert.Equal("الساعة اتنين إلا تلت", plan.Text);
        Assert.Equal(new TimeOnly(1, 40), plan.Time);
    }

    [Theory]
    [InlineData(13, 43, "phrase.01.43")]
    [InlineData(0, 5, "phrase.12.05")]
    [InlineData(12, 0, "phrase.12.00")]
    [InlineData(11, 40, "phrase.11.40")]
    public void Full_phrase_override_id_uses_the_displayed_12_hour_time(int hour24, int minute, string expectedId)
    {
        var plan = _engine.BuildPlan(new TimeOnly(hour24, minute), TimeSpeechSettings.Default);
        Assert.Equal(expectedId, plan.FullPhraseAssetId);
    }

    [Fact]
    public void Null_settings_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.BuildPlan(new TimeOnly(1, 0), null!));
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(1, 1)]
    [InlineData(11, 11)]
    [InlineData(12, 12)]
    [InlineData(13, 1)]
    [InlineData(23, 11)]
    public void Hour_conversion_to_12_hour_clock(int hour24, int expected)
    {
        Assert.Equal(expected, TimeSpeechEngine.ToHour12(hour24));
    }
}
