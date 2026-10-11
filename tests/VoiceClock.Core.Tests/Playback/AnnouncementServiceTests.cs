using VoiceClock.Core.Audio;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Tests.TestSupport;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.Playback;

public class AnnouncementServiceTests : IDisposable
{
    private readonly TestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Speaks_the_current_local_time_from_its_segments_in_order()
    {
        _rig.Clock.Time = new TimeOnly(13, 40);
        await _rig.RecordAsync("hour.02", "minute.40");

        var result = await _rig.Announcer.AnnounceNowAsync();

        Assert.Equal(AnnouncementStatus.Played, result.Status);
        Assert.Equal("الساعة اتنين إلا تلت", result.SpokenText);
        Assert.Equal(new[] { "hour.02", "minute.40" }, result.Segments.Select(s => s.AssetId).ToArray());
        Assert.Single(_rig.Output.Played);
    }

    [Fact]
    public async Task Played_audio_contains_both_clips_with_lead_gap_and_tail()
    {
        await _rig.RecordAsync("hour.01", "minute.24");
        var hour = (await _rig.Repository.LoadAsync("hour.01"))!;
        var minute = (await _rig.Repository.LoadAsync("minute.24"))!;

        await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 24));

        var defaults = new CompositionOptions();
        var expected = AudioMath.MsToSamples(44100, defaults.LeadInMs) + hour.SampleCount +
            AudioMath.MsToSamples(44100, 60) + minute.SampleCount + AudioMath.MsToSamples(44100, defaults.TailMs);
        Assert.Equal(expected, _rig.Output.Played[0].SampleCount);
    }

    [Fact]
    public async Task Missing_recordings_are_reported_and_nothing_is_spoken()
    {
        await _rig.RecordAsync("hour.01");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 43));

        Assert.Equal(AnnouncementStatus.MissingRecordings, result.Status);
        Assert.Equal(new[] { "minute.43" }, result.MissingAssetIds.ToArray());
        Assert.False(result.Succeeded);
        Assert.Empty(_rig.Output.Played);
        Assert.Equal(1, _rig.Feedback.Errors);
    }

    [Fact]
    public async Task A_recorded_error_message_is_played_instead_of_the_beep()
    {
        await _rig.RecordAsync("feedback.error");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 43));

        Assert.Equal(AnnouncementStatus.MissingRecordings, result.Status);
        Assert.Single(_rig.Output.Played);
        Assert.Equal(0, _rig.Feedback.Errors);
    }

    [Fact]
    public async Task A_playback_failure_never_plays_the_missing_recordings_message()
    {
        // Regression: a false playback failure made the app say «فيه تسجيلات ناقصة» after every announcement.
        await _rig.RecordAsync("hour.02", "minute.40", "feedback.error");
        _rig.Output.NextOutcome = new PlaybackOutcome(PlaybackOutcomeKind.Failed, "test");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));

        Assert.Equal(AnnouncementStatus.PlaybackFailed, result.Status);
        Assert.Single(_rig.Output.Played);       // only the announcement itself
        Assert.Equal(1, _rig.Feedback.Errors);   // beep and vibration instead
    }

    [Fact]
    public async Task Error_feedback_can_be_turned_off()
    {
        await _rig.UpdateSettingsAsync(s => s with { PlayErrorFeedback = false });

        await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 43));

        Assert.Equal(0, _rig.Feedback.Errors);
        Assert.Empty(_rig.Output.Played);
    }

    [Fact]
    public async Task A_complete_recorded_sentence_takes_priority_over_segments()
    {
        await _rig.RecordAsync("hour.01", "minute.43", "phrase.01.43");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(13, 43));

        Assert.Equal(AnnouncementStatus.Played, result.Status);
        Assert.Equal(new[] { "phrase.01.43" }, result.Segments.Select(s => s.AssetId).ToArray());
        Assert.Equal("الساعة واحدة وتلاتة وأربعين دقيقة", result.SpokenText);
    }

    [Fact]
    public async Task Overrides_can_be_bypassed_to_compare_with_the_composed_version()
    {
        await _rig.RecordAsync("hour.01", "minute.43", "phrase.01.43");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 43), new AnnouncementOverrides(UseFullPhrase: false));

        Assert.Equal(new[] { "hour.01", "minute.43" }, result.Segments.Select(s => s.AssetId).ToArray());
    }

    [Fact]
    public async Task Full_sentence_is_followed_by_the_day_period_when_enabled()
    {
        await _rig.RecordAsync("phrase.01.43", "period.pm");
        await _rig.UpdateSettingsAsync(s => s with { IncludeDayPeriod = true });

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(13, 43));

        Assert.Equal(new[] { "phrase.01.43", "period.pm" }, result.Segments.Select(s => s.AssetId).ToArray());
        Assert.Equal("الساعة واحدة وتلاتة وأربعين دقيقة مساءً", result.SpokenText);
    }

    [Fact]
    public async Task A_damaged_full_sentence_falls_back_to_the_segments()
    {
        await _rig.RecordAsync("hour.01", "minute.43", "phrase.01.43");
        var path = Path.Combine(_rig.Locations.UserClipsDirectory, "phrase.01.43.wav");
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 3)]);
        _rig.Restart(); // the damage is only on disk; a fresh start must notice it on use

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 43));

        Assert.Equal(AnnouncementStatus.Played, result.Status);
        Assert.Equal(new[] { "hour.01", "minute.43" }, result.Segments.Select(s => s.AssetId).ToArray());
    }

    [Fact]
    public async Task Day_period_is_spoken_only_when_enabled()
    {
        await _rig.RecordAsync("hour.08", "minute.30", "period.am", "period.pm");

        var off = await _rig.Announcer.AnnounceAsync(new TimeOnly(20, 30));
        await _rig.UpdateSettingsAsync(s => s with { IncludeDayPeriod = true });
        var on = await _rig.Announcer.AnnounceAsync(new TimeOnly(20, 30));

        Assert.Equal("الساعة تمانية ونص", off.SpokenText);
        Assert.Equal("الساعة تمانية ونص مساءً", on.SpokenText);
    }

    [Fact]
    public async Task A_second_request_while_speaking_is_ignored_not_queued()
    {
        await _rig.RecordAsync("hour.02", "minute.40");
        _rig.Output.Blocker = new TaskCompletionSource();

        var first = _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));
        await _rig.Output.Started.Task;
        Assert.True(_rig.Announcer.IsSpeaking);

        var second = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));
        var repeat = await _rig.Announcer.RepeatLastAsync();

        _rig.Output.Blocker.SetResult();
        Assert.Equal(AnnouncementStatus.Played, (await first).Status);
        Assert.Equal(AnnouncementStatus.Busy, second.Status);
        Assert.Equal(AnnouncementStatus.Busy, repeat.Status);
        Assert.Single(_rig.Output.Played);
        Assert.False(_rig.Announcer.IsSpeaking);
    }

    [Fact]
    public async Task Stop_cancels_the_current_announcement()
    {
        await _rig.RecordAsync("hour.02", "minute.40");
        _rig.Output.Blocker = new TaskCompletionSource();

        var running = _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));
        await _rig.Output.Started.Task;
        _rig.Announcer.Stop();

        var result = await running;
        Assert.Equal(AnnouncementStatus.Cancelled, result.Status);
        Assert.False(_rig.Announcer.IsSpeaking);
        Assert.Equal(AudioActivity.None, _rig.Gate.Current);
    }

    [Fact]
    public async Task Cancellation_token_cancels_the_announcement()
    {
        await _rig.RecordAsync("hour.02", "minute.40");
        _rig.Output.Blocker = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var running = _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40), cancellationToken: cts.Token);
        await _rig.Output.Started.Task;
        await cts.CancelAsync();

        Assert.Equal(AnnouncementStatus.Cancelled, (await running).Status);
    }

    [Theory]
    [InlineData(PlaybackOutcomeKind.Failed, AnnouncementStatus.PlaybackFailed, 1)]
    [InlineData(PlaybackOutcomeKind.FocusDenied, AnnouncementStatus.PlaybackFailed, 1)]
    [InlineData(PlaybackOutcomeKind.BlockedByCall, AnnouncementStatus.BlockedByCall, 0)]
    [InlineData(PlaybackOutcomeKind.Interrupted, AnnouncementStatus.Interrupted, 0)]
    public async Task Player_failures_are_never_reported_as_success(PlaybackOutcomeKind outcome, AnnouncementStatus expected, int expectedErrorSignals)
    {
        await _rig.RecordAsync("hour.02", "minute.40");
        _rig.Output.NextOutcome = new PlaybackOutcome(outcome, "test");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));

        Assert.Equal(expected, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedErrorSignals, _rig.Feedback.Errors);
    }

    [Fact]
    public async Task Repeat_replays_exactly_the_last_announcement()
    {
        await _rig.RecordAsync("hour.02", "minute.40", "hour.03", "minute.05");
        _rig.Clock.Time = new TimeOnly(1, 40);
        await _rig.Announcer.AnnounceNowAsync();

        _rig.Clock.Time = new TimeOnly(3, 5);
        var repeat = await _rig.Announcer.RepeatLastAsync();

        Assert.Equal(AnnouncementStatus.Played, repeat.Status);
        Assert.Equal("الساعة اتنين إلا تلت", repeat.SpokenText);
        Assert.Equal(_rig.Output.Played[0].Samples, _rig.Output.Played[1].Samples);
    }

    [Fact]
    public async Task Repeat_with_nothing_to_repeat_speaks_the_time()
    {
        await _rig.RecordAsync("hour.03", "minute.05");
        _rig.Clock.Time = new TimeOnly(15, 5);

        var result = await _rig.Announcer.RepeatLastAsync();

        Assert.Equal("الساعة تلاتة وخمسة", result.SpokenText);
    }

    [Fact]
    public async Task Failed_playback_is_not_kept_for_repeat()
    {
        await _rig.RecordAsync("hour.02", "minute.40", "hour.03", "minute.05");
        _rig.Output.NextOutcome = new PlaybackOutcome(PlaybackOutcomeKind.Failed);
        await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));

        _rig.Output.NextOutcome = PlaybackOutcome.Completed;
        _rig.Clock.Time = new TimeOnly(3, 5);
        var repeat = await _rig.Announcer.RepeatLastAsync();

        Assert.Equal("الساعة تلاتة وخمسة", repeat.SpokenText);
    }

    [Fact]
    public async Task Identical_requests_produce_identical_audio()
    {
        await _rig.RecordAsync("hour.11", "minute.55");

        await _rig.Announcer.AnnounceAsync(new TimeOnly(10, 55));
        await _rig.Announcer.AnnounceAsync(new TimeOnly(22, 55));

        Assert.Equal(_rig.Output.Played[0].Samples, _rig.Output.Played[1].Samples);
    }

    [Fact]
    public async Task The_pause_between_words_follows_the_setting()
    {
        await _rig.RecordAsync("hour.02", "minute.40");
        await _rig.UpdateSettingsAsync(s => s with { SegmentGapMs = 0 });
        await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));
        await _rig.UpdateSettingsAsync(s => s with { SegmentGapMs = 200 });
        await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));

        Assert.Equal(AudioMath.MsToSamples(44100, 200), _rig.Output.Played[1].SampleCount - _rig.Output.Played[0].SampleCount);
    }

    [Fact]
    public async Task Nothing_is_spoken_while_a_recording_is_in_progress()
    {
        await _rig.RecordAsync("hour.02", "minute.40");
        await _rig.Recorder.StartAsync("hour.05");

        var result = await _rig.Announcer.AnnounceAsync(new TimeOnly(1, 40));

        Assert.Equal(AnnouncementStatus.Busy, result.Status);
        Assert.Empty(_rig.Output.Played);
        await _rig.Recorder.CancelAsync();
    }

    [Fact]
    public async Task Bundled_default_voice_speaks_without_any_recording()
    {
        using var rig = new TestRig(new FakeBundledSource(PackBuilder.Build(["hour.12", "minute.50"])));

        var result = await rig.Announcer.AnnounceAsync(new TimeOnly(23, 50));

        Assert.Equal(AnnouncementStatus.Played, result.Status);
        Assert.Equal("الساعة اتناشر إلا عشرة", result.SpokenText);
    }
}

public class AudioPlanResolverTests
{
    private readonly TimeSpeechEngine _engine = new(SpeechRules.Default);

    [Fact]
    public void Without_overrides_the_plan_segments_are_used()
    {
        var plan = _engine.BuildPlan(new TimeOnly(1, 45), TimeSpeechSettings.Default);
        var resolved = AudioPlanResolver.Resolve(plan, id => !id.StartsWith("phrase.", StringComparison.Ordinal), useFullPhraseOverrides: true);

        Assert.False(resolved.UsesFullPhrase);
        Assert.Equal(plan.Segments, resolved.Segments);
        Assert.True(resolved.IsPlayable);
    }

    [Fact]
    public void Missing_ids_are_listed_once_in_plan_order()
    {
        var plan = _engine.BuildPlan(new TimeOnly(1, 45), new TimeSpeechSettings(IncludeDayPeriod: true));
        var resolved = AudioPlanResolver.Resolve(plan, _ => false, useFullPhraseOverrides: true);

        Assert.Equal(new[] { "hour.02", "minute.45", "period.am" }, resolved.MissingAssetIds.ToArray());
    }

    [Fact]
    public void Override_is_used_only_when_enabled_and_available()
    {
        var plan = _engine.BuildPlan(new TimeOnly(1, 45), TimeSpeechSettings.Default);

        Assert.True(AudioPlanResolver.Resolve(plan, id => id == "phrase.01.45", true).UsesFullPhrase);
        Assert.False(AudioPlanResolver.Resolve(plan, id => id == "phrase.01.45", false).UsesFullPhrase);
        Assert.False(AudioPlanResolver.Resolve(plan, id => id != "phrase.01.45", true).UsesFullPhrase);
    }
}

public class AudioSessionGateTests
{
    [Fact]
    public void Only_one_activity_at_a_time()
    {
        var gate = new AudioSessionGate();
        var first = gate.TryAcquire(AudioActivity.Announcement);

        Assert.NotNull(first);
        Assert.Null(gate.TryAcquire(AudioActivity.Recording));
        Assert.Equal(AudioActivity.Announcement, gate.Current);

        first!.Dispose();
        first.Dispose(); // releasing twice is harmless
        Assert.Equal(AudioActivity.None, gate.Current);

        var second = gate.TryAcquire(AudioActivity.Recording);
        Assert.NotNull(second);
        first.Dispose(); // a stale lease cannot release someone else's session
        Assert.Equal(AudioActivity.Recording, gate.Current);
        second!.Dispose();
    }

    [Fact]
    public void Activity_changes_are_announced()
    {
        var gate = new AudioSessionGate();
        var seen = new List<AudioActivity>();
        gate.ActivityChanged += (_, a) => seen.Add(a);

        using (gate.TryAcquire(AudioActivity.Preview))
        {
        }

        Assert.Equal(new[] { AudioActivity.Preview, AudioActivity.None }, seen.ToArray());
    }

    [Fact]
    public void None_is_not_an_activity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSessionGate().TryAcquire(AudioActivity.None));
    }
}
