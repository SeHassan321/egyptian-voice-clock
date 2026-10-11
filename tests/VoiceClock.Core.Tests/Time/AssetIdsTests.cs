using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.Time;

public class AssetIdsTests
{
    [Theory]
    [InlineData(1, "hour.01")]
    [InlineData(12, "hour.12")]
    public void Hour_ids(int hour, string expected) => Assert.Equal(expected, AssetIds.Hour(hour));

    [Theory]
    [InlineData(1, "minute.01")]
    [InlineData(43, "minute.43")]
    [InlineData(59, "minute.59")]
    public void Minute_ids(int minute, string expected) => Assert.Equal(expected, AssetIds.Minute(minute));

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Hour_out_of_range_throws(int hour) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetIds.Hour(hour));

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void Minute_out_of_range_throws(int minute) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetIds.Minute(minute));

    [Fact]
    public void Full_phrase_ids_round_trip_for_all_720_times()
    {
        for (var h = 1; h <= 12; h++)
        {
            for (var m = 0; m < 60; m++)
            {
                var id = AssetIds.FullPhrase(h, m);
                Assert.True(AssetIds.TryParseFullPhrase(id, out var h2, out var m2));
                Assert.Equal(h, h2);
                Assert.Equal(m, m2);
                Assert.True(AssetIds.IsWellFormed(id));
            }
        }
    }

    [Theory]
    [InlineData("hour.01", true)]
    [InlineData("hour.12", true)]
    [InlineData("minute.01", true)]
    [InlineData("minute.59", true)]
    [InlineData("period.am", true)]
    [InlineData("period.pm", true)]
    [InlineData("feedback.error", true)]
    [InlineData("phrase.12.00", true)]
    [InlineData("hour.00", false)]
    [InlineData("hour.13", false)]
    [InlineData("hour.1", false)]
    [InlineData("minute.00", false)]
    [InlineData("minute.60", false)]
    [InlineData("phrase.13.00", false)]
    [InlineData("phrase.01.60", false)]
    [InlineData("phrase.1.05", false)]
    [InlineData("../hour.01", false)]
    [InlineData("hour.01/../../x", false)]
    [InlineData("HOUR.01", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Well_formed_ids(string? id, bool expected) => Assert.Equal(expected, AssetIds.IsWellFormed(id));
}
