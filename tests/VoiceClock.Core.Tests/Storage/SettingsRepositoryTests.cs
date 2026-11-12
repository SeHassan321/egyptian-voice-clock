using System.Text.Json.Nodes;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Tests.TestSupport;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.Storage;

public class SettingsRepositoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StorageLocations _locations;

    public SettingsRepositoryTests()
    {
        _locations = new StorageLocations(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private JsonSettingsRepository NewRepository() => new(_locations);

    [Fact]
    public async Task First_launch_creates_defaults_with_day_period_off()
    {
        var result = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.CreatedDefaults, result.Outcome);
        Assert.False(result.Settings.IncludeDayPeriod);
        Assert.True(File.Exists(_locations.SettingsPath));
        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(_locations.SettingsPath))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Saved_settings_survive_a_restart()
    {
        var repo = NewRepository();
        await repo.LoadAsync();
        await repo.SaveAsync(repo.Current with { IncludeDayPeriod = true, SegmentGapMs = 120, NormalizeLoudness = false });

        var reloaded = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, reloaded.Outcome);
        Assert.True(reloaded.Settings.IncludeDayPeriod);
        Assert.Equal(120, reloaded.Settings.SegmentGapMs);
        Assert.False(reloaded.Settings.NormalizeLoudness);
    }

    [Fact]
    public async Task Rounding_is_off_at_first_and_once_turned_on_survives_a_restart()
    {
        var repo = NewRepository();
        Assert.False((await repo.LoadAsync()).Settings.RoundToFiveMinutes);

        await repo.SaveAsync(repo.Current with { RoundToFiveMinutes = true });

        var reloaded = await NewRepository().LoadAsync();
        Assert.Equal(SettingsLoadOutcome.Loaded, reloaded.Outcome);
        Assert.True(reloaded.Settings.RoundToFiveMinutes);
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(_locations.SettingsPath))!["roundToFiveMinutes"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Settings_saved_before_rounding_existed_keep_their_values_and_exact_minutes()
    {
        // What version 1.1 wrote: no "roundToFiveMinutes" at all.
        await File.WriteAllTextAsync(_locations.SettingsPath, "{\"schemaVersion\": 1, \"includeDayPeriod\": true, \"segmentGapMs\": 90}");

        var result = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, result.Outcome);
        Assert.False(result.Settings.RoundToFiveMinutes);
        Assert.True(result.Settings.IncludeDayPeriod);
        Assert.Equal(90, result.Settings.SegmentGapMs);
    }

    [Fact]
    public void Speech_settings_carry_the_day_period_and_rounding_choices()
    {
        var settings = new AppSettings { IncludeDayPeriod = true, RoundToFiveMinutes = true }.ToSpeechSettings();

        Assert.True(settings.IncludeDayPeriod);
        Assert.True(settings.RoundToFiveMinutes);
        Assert.Equal(TimeSpeechSettings.Default, AppSettings.Default.ToSpeechSettings());
    }

    [Fact]
    public async Task Saving_raises_changed_with_sanitized_values()
    {
        var repo = NewRepository();
        AppSettings? raised = null;
        repo.Changed += (_, s) => raised = s;

        await repo.SaveAsync(new AppSettings { SegmentGapMs = 5000 });

        Assert.Equal(AppSettings.MaxSegmentGapMs, raised!.SegmentGapMs);
        Assert.Equal(AppSettings.MaxSegmentGapMs, repo.Current.SegmentGapMs);
    }

    [Theory]
    [InlineData("{ broken json")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\": 1, \"includeDayPeriod\": \"yes\"}")]
    [InlineData("{\"schemaVersion\": 1, \"roundToFiveMinutes\": 1}")]
    [InlineData("{\"schemaVersion\": \"one\"}")]
    [InlineData("")]
    public async Task Damaged_settings_are_kept_aside_and_defaults_restored(string content)
    {
        await File.WriteAllTextAsync(_locations.SettingsPath, content);

        var result = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.RecoveredFromCorruption, result.Outcome);
        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.NotNull(result.CorruptFileBackupPath);
        Assert.Equal(content, await File.ReadAllTextAsync(result.CorruptFileBackupPath!));
        Assert.Equal(SettingsLoadOutcome.Loaded, (await NewRepository().LoadAsync()).Outcome);
    }

    [Fact]
    public async Task Unversioned_settings_are_migrated_and_values_preserved()
    {
        await File.WriteAllTextAsync(_locations.SettingsPath, "{\"includeDayPeriod\": true, \"segmentGapMs\": 150}");

        var result = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Migrated, result.Outcome);
        Assert.True(result.Settings.IncludeDayPeriod);
        Assert.Equal(150, result.Settings.SegmentGapMs);
        var rewritten = JsonNode.Parse(await File.ReadAllTextAsync(_locations.SettingsPath))!;
        Assert.Equal(AppSettings.CurrentSchemaVersion, rewritten["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Settings_from_a_newer_app_are_read_but_not_overwritten()
    {
        const string newer = "{\"schemaVersion\": 99, \"includeDayPeriod\": true, \"somethingNew\": 5}";
        await File.WriteAllTextAsync(_locations.SettingsPath, newer);

        var result = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.NewerVersion, result.Outcome);
        Assert.True(result.Settings.IncludeDayPeriod);
        Assert.Equal(newer, await File.ReadAllTextAsync(_locations.SettingsPath));
    }

    [Fact]
    public async Task Missing_values_fall_back_to_defaults_and_out_of_range_values_are_clamped()
    {
        await File.WriteAllTextAsync(_locations.SettingsPath, "{\"schemaVersion\": 1, \"segmentGapMs\": -50}");

        var result = await NewRepository().LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(0, result.Settings.SegmentGapMs);
        Assert.True(result.Settings.NormalizeLoudness);
        Assert.True(result.Settings.UseFullPhraseOverrides);
    }
}
