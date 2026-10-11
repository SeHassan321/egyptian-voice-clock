using VoiceClock.Core.Tests.TestSupport;
using VoiceClock.Core.Validation;

namespace VoiceClock.Core.Tests.Validation;

public class SetupValidationTests : IDisposable
{
    private readonly TestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task<CoverageReport> CoverageAsync(bool includeDayPeriod = false, bool useOverrides = false)
    {
        await _rig.Repository.InitializeAsync();
        return CoverageValidator.Validate(TestCatalog.Engine, _rig.Repository.IsAvailable, includeDayPeriod, useOverrides);
    }

    [Fact]
    public async Task With_nothing_recorded_no_time_can_be_spoken()
    {
        var report = await CoverageAsync();

        Assert.Equal(720, report.TimesChecked);
        Assert.Equal(0, report.TimesCovered);
        Assert.Equal(71, report.MissingAssetIds.Count);
    }

    [Fact]
    public async Task All_720_times_are_covered_once_the_required_clips_exist()
    {
        await _rig.RecordAllRequiredAsync();

        var report = await CoverageAsync();

        Assert.True(report.IsComplete);
        Assert.Equal(720, report.TimesCovered);
    }

    [Fact]
    public async Task With_the_day_period_all_1440_minutes_are_checked()
    {
        await _rig.RecordAllRequiredAsync();
        var withoutPeriods = await CoverageAsync(includeDayPeriod: true);

        Assert.Equal(1440, withoutPeriods.TimesChecked);
        Assert.Equal(0, withoutPeriods.TimesCovered);
        Assert.Equal(new[] { "period.am", "period.pm" }, withoutPeriods.MissingAssetIds.ToArray());

        await _rig.RecordAsync("period.am", "period.pm");
        Assert.True((await CoverageAsync(includeDayPeriod: true)).IsComplete);
    }

    [Fact]
    public async Task One_missing_minute_leaves_exactly_twelve_times_uncovered()
    {
        await _rig.RecordAllRequiredAsync(except: "minute.43");

        var report = await CoverageAsync();

        Assert.Equal(12, report.Uncovered.Count);
        Assert.All(report.Uncovered, u => Assert.Equal(43, u.Time.Minute));
        Assert.Equal(new[] { "minute.43" }, report.MissingAssetIds.ToArray());
    }

    [Fact]
    public async Task One_missing_hour_affects_its_own_times_and_the_previous_hours_ila_times()
    {
        await _rig.RecordAllRequiredAsync(except: "hour.02");

        var report = await CoverageAsync();

        // 2:00–2:59 except 2:40/45/50/55 (56 times) plus 1:40, 1:45, 1:50, 1:55 ("الساعة اتنين إلا …").
        Assert.Equal(60, report.Uncovered.Count);
        Assert.Contains(report.Uncovered, u => u.Time == new TimeOnly(1, 40));
        Assert.DoesNotContain(report.Uncovered, u => u.Time == new TimeOnly(2, 40));
    }

    [Fact]
    public async Task Optional_full_sentences_do_not_hide_a_missing_word()
    {
        await _rig.RecordAllRequiredAsync(except: "minute.43");
        await _rig.RecordAsync("phrase.01.43");

        Assert.Equal(12, (await CoverageAsync()).Uncovered.Count);
        Assert.Equal(11, (await CoverageAsync(useOverrides: true)).Uncovered.Count);
    }

    [Fact]
    public async Task Setup_is_incomplete_on_a_fresh_install()
    {
        await _rig.Repository.InitializeAsync();
        var summary = SetupStatusCalculator.Compute(TestCatalog.Catalog, _rig.Repository, TestCatalog.Engine, includeDayPeriod: false);

        Assert.False(summary.IsComplete);
        Assert.Equal(71, summary.RequiredCount);
        Assert.Equal(0, summary.ReadyCount);
        Assert.Equal(71, summary.MissingCount);
        Assert.Equal(0, summary.Progress);
    }

    [Fact]
    public async Task Setup_is_complete_only_when_every_required_clip_is_usable()
    {
        await _rig.RecordAllRequiredAsync();
        var complete = SetupStatusCalculator.Compute(TestCatalog.Catalog, _rig.Repository, TestCatalog.Engine, includeDayPeriod: false);
        Assert.True(complete.IsComplete);
        Assert.Equal(1.0, complete.Progress);

        var withPeriods = SetupStatusCalculator.Compute(TestCatalog.Catalog, _rig.Repository, TestCatalog.Engine, includeDayPeriod: true);
        Assert.False(withPeriods.IsComplete);
        Assert.Equal(new[] { "period.am", "period.pm" }, withPeriods.MissingIds.ToArray());
    }

    [Fact]
    public async Task A_damaged_clip_makes_setup_incomplete()
    {
        await _rig.RecordAllRequiredAsync();
        var path = Path.Combine(_rig.Locations.UserClipsDirectory, "minute.15.wav");
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..50]);
        await _rig.Repository.VerifyAllAsync();

        var summary = SetupStatusCalculator.Compute(TestCatalog.Catalog, _rig.Repository, TestCatalog.Engine, includeDayPeriod: false);

        Assert.False(summary.IsComplete);
        Assert.Equal(new[] { "minute.15" }, summary.InvalidIds.ToArray());
    }
}
