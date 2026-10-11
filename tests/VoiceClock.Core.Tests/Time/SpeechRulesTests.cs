using System.Text.Json.Nodes;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.Time;

public class SpeechRulesTests
{
    private static readonly int[] ToNextHourMinutes = [40, 45, 50, 55];

    internal static string DefaultJson()
    {
        using var stream = typeof(SpeechRules).Assembly.GetManifestResourceStream(SpeechRules.DefaultResourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Modify(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(DefaultJson())!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    [Fact]
    public void Default_rules_map_all_twelve_hours()
    {
        var rules = SpeechRules.Default;
        var expected = new[]
        {
            "", "الساعة واحدة", "الساعة اتنين", "الساعة تلاتة", "الساعة أربعة", "الساعة خمسة", "الساعة ستة",
            "الساعة سبعة", "الساعة تمانية", "الساعة تسعة", "الساعة عشرة", "الساعة حداشر", "الساعة اتناشر",
        };

        for (var h = 1; h <= 12; h++)
        {
            Assert.Equal(expected[h], rules.HourText(h));
        }
    }

    [Fact]
    public void Default_rules_map_all_fifty_nine_minutes_with_unique_text()
    {
        var rules = SpeechRules.Default;
        Assert.Equal(59, rules.Minutes.Count);
        Assert.Equal(Enumerable.Range(1, 59), rules.Minutes.Select(m => m.Minute));
        Assert.Equal(59, rules.Minutes.Select(m => m.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Only_40_45_50_and_55_refer_to_the_next_hour()
    {
        var toNext = SpeechRules.Default.Minutes
            .Where(m => m.Relation == MinuteRelation.ToNextHour)
            .Select(m => m.Minute)
            .ToArray();

        Assert.Equal(ToNextHourMinutes, toNext);
    }

    [Theory]
    [InlineData(5, "وخمسة")]
    [InlineData(10, "وعشرة")]
    [InlineData(15, "وربع")]
    [InlineData(20, "وتلت")]
    [InlineData(25, "ونص إلا خمسة")]
    [InlineData(30, "ونص")]
    [InlineData(35, "ونص وخمسة")]
    [InlineData(40, "إلا تلت")]
    [InlineData(45, "إلا ربع")]
    [InlineData(50, "إلا عشرة")]
    [InlineData(55, "إلا خمسة")]
    public void Round_minutes_use_the_familiar_fractions(int minute, string expected)
    {
        var rule = SpeechRules.Default.Minute(minute);
        Assert.Equal(expected, rule.Text);
        Assert.True(rule.IsRound);
    }

    [Fact]
    public void Connectors_match_the_relation()
    {
        foreach (var rule in SpeechRules.Default.Minutes)
        {
            if (rule.Relation == MinuteRelation.ToNextHour)
            {
                Assert.StartsWith("إلا ", rule.Text);
            }
            else
            {
                Assert.StartsWith("و", rule.Text);
            }
        }
    }

    [Fact]
    public void Exact_minutes_use_the_right_word_for_minutes()
    {
        foreach (var rule in SpeechRules.Default.Minutes.Where(m => !m.IsRound))
        {
            var expectedWord = rule.Minute switch
            {
                1 => "ودقيقة",
                2 => "ودقيقتين",
                <= 10 => "دقايق",   // 3-9: "تلات دقايق"
                _ => "دقيقة",       // 11+: "حداشر دقيقة"
            };

            Assert.Contains(expectedWord, rule.Text);
        }
    }

    [Fact]
    public void Day_period_and_feedback_texts_are_present()
    {
        Assert.Equal("صباحًا", SpeechRules.Default.AmText);
        Assert.Equal("مساءً", SpeechRules.Default.PmText);
        Assert.False(string.IsNullOrWhiteSpace(SpeechRules.Default.FeedbackErrorText));
    }

    [Fact]
    public void A_word_can_be_corrected_in_the_data_without_changing_the_algorithm()
    {
        // Switch 1:43 to the "next hour" style purely through the rules document.
        var json = Modify(root => root["minutes"]!["43"] = new JsonObject
        {
            ["relation"] = "to",
            ["text"] = "إلا سبعتاشر دقيقة",
        });

        var engine = new TimeSpeechEngine(SpeechRules.Parse(json));
        var plan = engine.BuildPlan(new TimeOnly(1, 43), TimeSpeechSettings.Default);

        Assert.Equal("الساعة اتنين إلا سبعتاشر دقيقة", plan.Text);
        Assert.Equal(MinuteRelation.ToNextHour, plan.Relation);
    }

    [Fact]
    public void Invalid_json_is_rejected()
    {
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse("{ not json"));
        Assert.Single(ex.Problems);
    }

    [Fact]
    public void Missing_hour_is_reported()
    {
        var json = Modify(root => root["hours"]!.AsObject().Remove("7"));
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains("hour 7", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_minute_is_reported()
    {
        var json = Modify(root => root["minutes"]!.AsObject().Remove("33"));
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains("minute 33", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_relation_is_reported()
    {
        var json = Modify(root => root["minutes"]!["12"]!["relation"] = "before");
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains("'before'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hours", "13")]
    [InlineData("hours", "0")]
    [InlineData("minutes", "60")]
    [InlineData("minutes", "0")]
    [InlineData("minutes", "07")]
    public void Out_of_range_keys_are_reported(string section, string key)
    {
        var json = Modify(root =>
        {
            JsonNode value = section == "hours"
                ? JsonValue.Create("الساعة")
                : new JsonObject { ["relation"] = "past", ["text"] = "و" };
            root[section]![key] = value;
        });

        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains($"'{key}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_text_is_reported()
    {
        var json = Modify(root => root["minutes"]!["15"]!["text"] = "  ");
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains("minute 15", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_schema_version_is_reported()
    {
        var json = Modify(root => root["schemaVersion"] = 2);
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_day_periods_are_reported()
    {
        var json = Modify(root => root.Remove("dayPeriods"));
        var ex = Assert.Throws<SpeechRulesException>(() => SpeechRules.Parse(json));
        Assert.Contains(ex.Problems, p => p.Contains("dayPeriods", StringComparison.Ordinal));
    }
}
