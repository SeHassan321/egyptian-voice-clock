using VoiceClock.Core.Catalog;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Resources;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Tests.TestSupport;
using VoiceClock.Core.ViewModels;

namespace VoiceClock.Core.Tests.ViewModels;

public class MainViewModelTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly FakeVolume _volume = new();
    private readonly FakeNavigation _navigation = new();
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _vm = new MainViewModel(
            _rig.Announcer, _volume, _navigation, _rig.Feedback, _rig.Initializer, TestCatalog.Catalog,
            _rig.Repository, TestCatalog.Engine, _rig.Settings, new ImmediateDispatcher());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public async Task The_big_button_speaks_the_time_and_confirms_the_press()
    {
        await _rig.RecordAllRequiredAsync();
        _rig.Clock.Time = new TimeOnly(9, 15);
        await _vm.OnAppearingAsync();

        await _vm.AnnounceCommand.ExecuteAsync();

        Assert.Single(_rig.Output.Played);
        Assert.Equal(1, _rig.Feedback.Taps);
        Assert.False(_vm.IsSpeaking);
        Assert.False(_vm.HasStatusMessage);
    }

    [Fact]
    public async Task Muted_media_volume_opens_the_volume_panel_instead_of_speaking_silently()
    {
        await _rig.RecordAllRequiredAsync();
        _volume.IsMuted = true;

        await _vm.AnnounceCommand.ExecuteAsync();

        Assert.Empty(_rig.Output.Played);
        Assert.Equal(1, _volume.PanelShown);
        Assert.Equal(1, _rig.Feedback.Errors);
        Assert.Equal(Strings.Main_Status_Muted, _vm.StatusMessage);
    }

    [Fact]
    public async Task Incomplete_setup_is_reported_to_the_caretaker()
    {
        await _vm.OnAppearingAsync();

        Assert.True(_vm.IsReady);
        Assert.Contains("71", _vm.StatusMessage);
    }

    [Fact]
    public async Task Missing_recordings_are_explained_after_a_failed_announcement()
    {
        await _vm.OnAppearingAsync();

        await _vm.AnnounceCommand.ExecuteAsync();

        Assert.Equal(Strings.Main_Status_Missing, _vm.StatusMessage);
        Assert.Equal(1, _rig.Feedback.Errors);
    }

    [Fact]
    public async Task Status_clears_once_setup_becomes_complete()
    {
        await _vm.OnAppearingAsync();
        Assert.True(_vm.HasStatusMessage);

        await _rig.RecordAllRequiredAsync();

        Assert.False(_vm.HasStatusMessage);
    }

    [Fact]
    public async Task Rapid_double_taps_speak_only_once()
    {
        await _rig.RecordAllRequiredAsync();
        _rig.Output.Blocker = new TaskCompletionSource();

        var first = _vm.AnnounceCommand.ExecuteAsync();
        await _rig.Output.Started.Task;
        Assert.False(_vm.AnnounceCommand.CanExecute(null));
        Assert.True(_vm.IsSpeaking);
        await _vm.AnnounceCommand.ExecuteAsync();
        await _vm.RepeatCommand.ExecuteAsync();

        _rig.Output.Blocker.SetResult();
        await first;
        Assert.Single(_rig.Output.Played);
    }

    [Fact]
    public async Task Repeat_replays_the_last_announcement()
    {
        await _rig.RecordAllRequiredAsync();
        await _vm.AnnounceCommand.ExecuteAsync();
        await _vm.RepeatCommand.ExecuteAsync();

        Assert.Equal(2, _rig.Output.Played.Count);
        Assert.Equal(_rig.Output.Played[0].Samples, _rig.Output.Played[1].Samples);
    }

    [Fact]
    public async Task Volume_buttons_adjust_media_volume()
    {
        _vm.VolumeUpCommand.Execute(null);
        _vm.VolumeUpCommand.Execute(null);
        _vm.VolumeDownCommand.Execute(null);

        Assert.Equal(2, _volume.Raised);
        Assert.Equal(1, _volume.Lowered);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Opening_setup_stops_speech_and_navigates()
    {
        await _vm.OpenSetupCommand.ExecuteAsync();
        Assert.Equal(new[] { "setup" }, _navigation.Visits.ToArray());
    }

    [Fact]
    public async Task Failures_become_a_friendly_message_not_a_crash()
    {
        _navigation.ThrowOnNext = new InvalidOperationException("boom");

        await _vm.OpenSetupCommand.ExecuteAsync();

        Assert.Equal(Strings.Common_Error_Message, _vm.StatusMessage);
    }
}

public class SetupViewModelTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly FakeBackupFiles _backup = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeNavigation _navigation = new();
    private readonly SetupViewModel _vm;

    public SetupViewModelTests()
    {
        _vm = new SetupViewModel(
            _rig.Initializer, _rig.Settings, _rig.Repository, TestCatalog.Catalog, TestCatalog.Engine, _rig.Announcer,
            _backup, _dialogs, _navigation, new FakeVolume(), new FakeAppVersion(), new ImmediateDispatcher());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public async Task Shows_progress_and_that_setup_is_not_complete()
    {
        await _vm.OnAppearingAsync();

        Assert.Equal("0 من 71 تسجيل جاهزين", _vm.SummaryText);
        Assert.False(_vm.IsComplete);
        Assert.Equal(Strings.Setup_Incomplete, _vm.CompletionText);
        Assert.Equal(Strings.Setup_Bundled_None, _vm.BundledVoiceText);
    }

    [Fact]
    public async Task Never_claims_completion_until_every_time_can_be_spoken()
    {
        await _rig.RecordAllRequiredAsync(except: "minute.43");
        await _vm.OnAppearingAsync();
        Assert.False(_vm.IsComplete);
        Assert.Contains("12:43", _vm.CoverageText); // first uncovered time in the 12-hour cycle
        Assert.Contains("وتلاتة وأربعين دقيقة", _vm.CoverageText);

        await _rig.RecordAsync("minute.43");
        Assert.True(_vm.IsComplete);
        Assert.Equal(Strings.Setup_Complete, _vm.CompletionText);
    }

    [Fact]
    public async Task Turning_on_the_day_period_is_saved_and_adds_two_required_clips()
    {
        await _vm.OnAppearingAsync();

        _vm.IncludeDayPeriod = true;
        await WaitUntil(() => _rig.Settings.Current.IncludeDayPeriod);

        Assert.Equal("0 من 73 تسجيل جاهزين", _vm.SummaryText);
        Assert.True((await new Settings.JsonSettingsRepository(_rig.Locations).LoadAsync()).Settings.IncludeDayPeriod);
    }

    [Fact]
    public async Task Preview_text_shows_exactly_what_will_be_said()
    {
        await _vm.OnAppearingAsync();
        _vm.TestHourIndex = 0;
        _vm.TestMinute = 43;

        Assert.Equal("هيقول: الساعة واحدة وتلاتة وأربعين دقيقة", _vm.TestPreviewText);

        _vm.TestHourIndex = 11;
        _vm.TestMinute = 40;
        Assert.Equal("هيقول: الساعة واحدة إلا تلت", _vm.TestPreviewText);
    }

    [Fact]
    public async Task Testing_a_time_with_missing_words_names_the_missing_words()
    {
        await _rig.RecordAsync("hour.01");
        await _vm.OnAppearingAsync();
        _vm.TestHourIndex = 0;
        _vm.TestMinute = 43;

        await _vm.TestSelectedCommand.ExecuteAsync();

        Assert.Contains("«وتلاتة وأربعين دقيقة»", _vm.Message);
    }

    [Fact]
    public async Task Testing_a_recorded_time_reports_the_spoken_sentence()
    {
        await _rig.RecordAsync("hour.02", "minute.45");
        await _vm.OnAppearingAsync();
        _vm.TestHourIndex = 0;
        _vm.TestMinute = 45;

        await _vm.TestSelectedCommand.ExecuteAsync();

        Assert.Equal("الساعة اتنين إلا ربع", _vm.Message);
    }

    [Fact]
    public async Task Check_everything_reports_damaged_recordings()
    {
        await _rig.RecordAllRequiredAsync();
        var path = Path.Combine(_rig.Locations.UserClipsDirectory, "hour.03.wav");
        await File.WriteAllBytesAsync(path, (await File.ReadAllBytesAsync(path))[..80]);
        await _vm.OnAppearingAsync();

        await _vm.CheckEverythingCommand.ExecuteAsync();

        Assert.Contains("1", _vm.Message);
        Assert.False(_vm.IsComplete);
    }

    [Fact]
    public async Task Backup_needs_own_recordings()
    {
        await _vm.OnAppearingAsync();
        await _vm.ExportBackupCommand.ExecuteAsync();

        Assert.Equal(Strings.Setup_Backup_Empty, _vm.Message);
        Assert.Null(_backup.LastWritten);
    }

    [Fact]
    public async Task Backup_writes_a_valid_voice_pack_to_the_chosen_place()
    {
        await _rig.RecordAsync("hour.01", "minute.43");
        await _vm.OnAppearingAsync();

        await _vm.ExportBackupCommand.ExecuteAsync();

        Assert.False(_backup.LastWasShare);
        Assert.EndsWith(".zip", _backup.LastFileName);
        Assert.Equal("اتحفظت نسخة فيها 2 تسجيل.", _vm.Message);
        using var other = new TempDirectory();
        var fresh = new VoiceAssetRepository(new StorageLocations(other.Path), TestCatalog.Catalog, new NoBundledVoicePack());
        Assert.Equal(2, (await fresh.ImportAsync(new MemoryStream(_backup.LastWritten!))).Imported);
    }

    [Fact]
    public async Task Cancelling_the_save_picker_is_reported()
    {
        await _rig.RecordAsync("hour.01");
        _backup.UserCancelsSave = true;
        await _vm.OnAppearingAsync();

        await _vm.ExportBackupCommand.ExecuteAsync();

        Assert.Equal(Strings.Setup_Backup_Cancelled, _vm.Message);
    }

    [Fact]
    public async Task Full_voice_export_is_named_like_the_bundled_pack()
    {
        await _rig.RecordAllRequiredAsync();
        await _vm.OnAppearingAsync();

        await _vm.ExportFullVoiceCommand.ExecuteAsync();

        Assert.True(_backup.LastWasShare);
        Assert.Equal("voicepack.zip", _backup.LastFileName);
    }

    [Fact]
    public async Task Restoring_a_backup_asks_first_then_imports()
    {
        using var source = new TestRig();
        await source.RecordAsync("hour.05", "hour.06");
        using var pack = new MemoryStream();
        await source.Repository.ExportAsync(pack, VoicePackScope.UserRecordings);
        _backup.PickedFile = pack.ToArray();
        await _vm.OnAppearingAsync();

        _dialogs.Answers.Enqueue(false);
        await _vm.ImportBackupCommand.ExecuteAsync();
        Assert.False(_rig.Repository.IsAvailable("hour.05"));

        _dialogs.Answers.Enqueue(true);
        await _vm.ImportBackupCommand.ExecuteAsync();
        Assert.True(_rig.Repository.IsAvailable("hour.05"));
        Assert.Equal("اترجّع 2 تسجيل.", _vm.Message);
    }

    [Fact]
    public async Task Restoring_a_file_that_is_not_a_backup_is_refused()
    {
        _backup.PickedFile = [1, 2, 3];
        _dialogs.Answers.Enqueue(true);
        await _vm.OnAppearingAsync();

        await _vm.ImportBackupCommand.ExecuteAsync();

        Assert.Equal(Strings.Setup_Import_Rejected, _vm.Message);
    }

    [Fact]
    public async Task Gap_slider_saves_after_the_finger_stops_or_when_leaving()
    {
        await _vm.OnAppearingAsync();

        _vm.SegmentGapMs = 133;
        Assert.Equal(130, _vm.SegmentGapMs);
        await WaitUntil(() => _rig.Settings.Current.SegmentGapMs == 130);

        _vm.SegmentGapMs = 250;
        _vm.OnDisappearing();
        await WaitUntil(() => _rig.Settings.Current.SegmentGapMs == 250);
    }

    [Fact]
    public async Task Record_next_missing_opens_the_first_missing_clip()
    {
        await _rig.RecordAsync("hour.01");
        await _vm.OnAppearingAsync();

        await _vm.RecordNextMissingCommand.ExecuteAsync();

        Assert.Equal(new[] { "recorder:hour.02" }, _navigation.Visits.ToArray());
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met in time.");
    }
}

public class RecordingsViewModelTests : IDisposable
{
    private readonly TestRig _rig;
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeNavigation _navigation = new();
    private readonly RecordingsViewModel _vm;

    public RecordingsViewModelTests()
    {
        _rig = new TestRig(new FakeBundledSource(PackBuilder.Build(["hour.12"])));
        _vm = new RecordingsViewModel(
            _rig.Initializer, _rig.Repository, TestCatalog.Catalog, _rig.Settings, _rig.Recorder,
            _dialogs, _navigation, new ImmediateDispatcher());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    private ClipItemViewModel Item(string id) => _vm.Groups.SelectMany(g => g).Single(i => i.Id == id);

    [Fact]
    public async Task Checklist_groups_every_clip_in_recording_order()
    {
        await _vm.OnAppearingAsync();

        Assert.Equal(
            new[] { AssetCategory.Hour, AssetCategory.RoundMinute, AssetCategory.ExactMinute, AssetCategory.DayPeriod, AssetCategory.Feedback },
            _vm.Groups.Select(g => g.Category).ToArray());
        Assert.Equal(new[] { 12, 11, 48, 2, 1 }, _vm.Groups.Select(g => g.Count).ToArray());
        Assert.Equal("الساعات", _vm.Groups[0].Title);
        Assert.Equal("الساعة واحدة", _vm.Groups[0][0].Text);
    }

    [Fact]
    public async Task Statuses_show_missing_default_and_own_recordings()
    {
        await _vm.OnAppearingAsync();
        Assert.True(Item("hour.01").NeedsAttention);
        Assert.Equal(Strings.Status_Missing, Item("hour.01").StatusText);
        Assert.Equal(Strings.Status_Bundled, Item("hour.12").StatusText);
        Assert.Equal(Strings.Status_Optional, Item("period.am").StatusText);

        await _rig.RecordAsync("hour.01");

        Assert.True(Item("hour.01").IsUserRecording);
        Assert.Equal(Strings.Status_User, Item("hour.01").StatusText);
    }

    [Fact]
    public async Task Recording_a_full_sentence_adds_it_to_the_list()
    {
        await _vm.OnAppearingAsync();
        await _rig.RecordAsync("phrase.01.43");

        var group = Assert.Single(_vm.Groups, g => g.Category == AssetCategory.FullPhrase);
        Assert.Equal("الساعة واحدة وتلاتة وأربعين دقيقة", Assert.Single(group).Text);
    }

    [Fact]
    public async Task Deleting_asks_for_confirmation()
    {
        await _rig.RecordAsync("hour.01");
        await _vm.OnAppearingAsync();
        Assert.True(Item("hour.01").CanDelete);

        _dialogs.Answers.Enqueue(false);
        await _vm.DeleteCommand.ExecuteAsync(Item("hour.01"));
        Assert.True(_rig.Repository.IsAvailable("hour.01"));

        _dialogs.Answers.Enqueue(true);
        await _vm.DeleteCommand.ExecuteAsync(Item("hour.01"));
        Assert.False(_rig.Repository.IsAvailable("hour.01"));
        Assert.True(Item("hour.01").IsMissing);
    }

    [Fact]
    public async Task Own_recording_over_a_default_can_go_back_to_the_default()
    {
        await _rig.RecordAsync("hour.12");
        await _vm.OnAppearingAsync();
        Assert.True(Item("hour.12").CanRestoreDefault);
        Assert.False(Item("hour.12").CanDelete);

        _dialogs.Answers.Enqueue(true);
        await _vm.RestoreDefaultCommand.ExecuteAsync(Item("hour.12"));

        Assert.Equal(AssetSource.Bundled, _rig.Repository.GetStatus("hour.12").Source);
        Assert.Equal(Strings.Status_Bundled, Item("hour.12").StatusText);
    }

    [Fact]
    public async Task Playing_a_row_uses_the_effective_recording()
    {
        await _vm.OnAppearingAsync();
        await _vm.PlayCommand.ExecuteAsync(Item("hour.12"));
        Assert.Single(_rig.Output.Played);
    }

    [Fact]
    public async Task Adding_a_full_sentence_opens_the_recorder_for_that_time()
    {
        await _vm.OnAppearingAsync();
        _vm.PhraseHourIndex = 11;
        _vm.PhraseMinute = 40;

        Assert.Equal("الساعة واحدة إلا تلت", _vm.PhrasePreviewText);
        await _vm.AddFullPhraseCommand.ExecuteAsync();
        Assert.Equal(new[] { "recorder:phrase.12.40" }, _navigation.Visits.ToArray());
    }
}

public class RecordClipViewModelTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly FakeMicrophone _microphone = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly RecordClipViewModel _vm;

    public RecordClipViewModelTests()
    {
        _vm = new RecordClipViewModel(
            _rig.Recorder, _rig.Repository, TestCatalog.Catalog, _rig.Settings, _microphone, _dialogs,
            _rig.Announcer, _rig.Feedback, _rig.Initializer, new ImmediateDispatcher());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public async Task Shows_what_to_say_and_where_we_are()
    {
        await _vm.LoadAsync("hour.01");

        Assert.Equal("الساعة واحدة", _vm.PhraseText);
        Assert.Equal("1 من 74", _vm.PositionText);
        Assert.Equal(Strings.Recorder_NotSaved, _vm.SavedStatusText);
        Assert.False(_vm.PreviousCommand.CanExecute(null));
        Assert.True(_vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task Record_listen_save_then_continue_with_the_next_missing_clip()
    {
        await _vm.LoadAsync("hour.01");

        await _vm.ToggleRecordingCommand.ExecuteAsync();
        Assert.True(_vm.IsRecording);
        Assert.Equal(Strings.Recorder_Stop, _vm.RecordButtonText);

        await _vm.ToggleRecordingCommand.ExecuteAsync();
        Assert.False(_vm.IsRecording);
        Assert.True(_vm.TakeIsValid);
        Assert.Equal(Strings.Recorder_Take_Ok, _vm.Message);

        await _vm.PlayTakeCommand.ExecuteAsync();
        Assert.Single(_rig.Output.Played);

        await _vm.SaveCommand.ExecuteAsync();
        Assert.True(_rig.Repository.IsAvailable("hour.01"));
        Assert.Equal("hour.02", _vm.AssetId);
        Assert.Equal(Strings.Recorder_Saved, _vm.Message);
        Assert.False(_vm.HasTake);
    }

    [Fact]
    public async Task A_silent_take_cannot_be_saved()
    {
        _rig.Input.NextRecording = Signals.Noise(1500, peakDbfs: -60);
        await _vm.LoadAsync("minute.43");

        await _vm.ToggleRecordingCommand.ExecuteAsync();
        await _vm.ToggleRecordingCommand.ExecuteAsync();

        Assert.False(_vm.TakeIsValid);
        Assert.False(_vm.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.Recorder_Take_NoSpeech, _vm.Message);
    }

    [Fact]
    public async Task Microphone_permission_is_requested_only_when_recording_starts()
    {
        await _vm.LoadAsync("hour.01");
        Assert.Equal(0, _microphone.Requests);

        _microphone.Access = MicrophoneAccess.Denied;
        await _vm.ToggleRecordingCommand.ExecuteAsync();

        Assert.Equal(1, _microphone.Requests);
        Assert.False(_vm.IsRecording);
        Assert.Equal(0, _rig.Input.Starts);
        Assert.Equal(Strings.Recorder_PermissionDenied, _vm.Message);
    }

    [Fact]
    public async Task Permanently_denied_permission_offers_the_app_settings()
    {
        _microphone.Access = MicrophoneAccess.PermanentlyDenied;
        await _vm.LoadAsync("hour.01");

        _dialogs.Answers.Enqueue(true);
        await _vm.ToggleRecordingCommand.ExecuteAsync();

        Assert.Equal(1, _microphone.SettingsOpened);
        Assert.Equal(0, _rig.Input.Starts);
    }

    [Fact]
    public async Task Unavailable_microphone_is_explained()
    {
        _rig.Input.Unavailable = true;
        await _vm.LoadAsync("hour.01");

        await _vm.ToggleRecordingCommand.ExecuteAsync();

        Assert.False(_vm.IsRecording);
        Assert.Equal(Strings.Recorder_MicUnavailable, _vm.Message);
    }

    [Fact]
    public async Task Leaving_the_screen_mid_recording_releases_the_microphone()
    {
        await _vm.LoadAsync("hour.01");
        await _vm.ToggleRecordingCommand.ExecuteAsync();

        await _vm.OnDisappearingAsync();

        Assert.True(_rig.Input.LastCapture!.Disposed);
        Assert.False(_rig.Recorder.IsRecording);
        Assert.False(_rig.Repository.IsAvailable("hour.01"));
    }

    [Fact]
    public async Task Full_sentence_can_be_compared_with_the_composed_words()
    {
        await _rig.RecordAsync("hour.01", "minute.43");
        await _vm.LoadAsync("phrase.01.43");

        Assert.True(_vm.IsFullPhrase);
        Assert.Equal("الساعة واحدة وتلاتة وأربعين دقيقة", _vm.PhraseText);
        Assert.False(_vm.NextCommand.CanExecute(null));

        await _vm.CompareComposedCommand.ExecuteAsync();
        Assert.Single(_rig.Output.Played);
    }

    [Fact]
    public async Task Next_and_previous_walk_the_catalog()
    {
        await _vm.LoadAsync("hour.12");
        await _vm.NextCommand.ExecuteAsync();
        Assert.Equal("minute.05", _vm.AssetId);

        await _vm.PreviousCommand.ExecuteAsync();
        Assert.Equal("hour.12", _vm.AssetId);
    }
}
