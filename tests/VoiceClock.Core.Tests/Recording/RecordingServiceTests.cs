using VoiceClock.Core.Playback;
using VoiceClock.Core.Recording;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Recording;

public class RecordingServiceTests : IDisposable
{
    private readonly TestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Start_and_stop_produce_a_trimmed_valid_take()
    {
        var raw = Signals.Word(leadMs: 700, speechMs: 600, tailMs: 900);
        _rig.Input.NextRecording = raw;

        Assert.Equal(RecordingStartStatus.Started, await _rig.Recorder.StartAsync("hour.01"));
        Assert.True(_rig.Recorder.IsRecording);
        Assert.Equal(AudioActivity.Recording, _rig.Gate.Current);

        var take = await _rig.Recorder.StopAsync();

        Assert.NotNull(take);
        Assert.Equal("hour.01", take!.AssetId);
        Assert.True(take.IsValid);
        Assert.True(take.Audio.SampleCount < raw.SampleCount);
        Assert.False(_rig.Recorder.IsRecording);
        Assert.Equal(AudioActivity.None, _rig.Gate.Current);
        Assert.True(_rig.Input.LastCapture!.Disposed);
    }

    [Fact]
    public async Task Only_one_recording_session_at_a_time()
    {
        await _rig.Recorder.StartAsync("hour.01");

        Assert.Equal(RecordingStartStatus.Busy, await _rig.Recorder.StartAsync("hour.02"));
        Assert.Equal(1, _rig.Input.Starts);
        await _rig.Recorder.CancelAsync();
    }

    [Fact]
    public async Task Unknown_ids_cannot_be_recorded()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _rig.Recorder.StartAsync("minute.99"));
    }

    [Fact]
    public async Task Unavailable_microphone_is_reported_and_releases_the_audio_session()
    {
        _rig.Input.Unavailable = true;

        Assert.Equal(RecordingStartStatus.DeviceUnavailable, await _rig.Recorder.StartAsync("hour.01"));
        Assert.Equal(AudioActivity.None, _rig.Gate.Current);

        _rig.Input.Unavailable = false;
        Assert.Equal(RecordingStartStatus.Started, await _rig.Recorder.StartAsync("hour.01"));
        await _rig.Recorder.CancelAsync();
    }

    [Fact]
    public async Task Silent_take_is_invalid_and_cannot_be_saved()
    {
        _rig.Input.NextRecording = Signals.Noise(2000, peakDbfs: -60);
        await _rig.Recorder.StartAsync("hour.01");
        var take = await _rig.Recorder.StopAsync();

        Assert.False(take!.IsValid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Recorder.SaveAsync(take));
        Assert.Equal(AssetState.Missing, _rig.Repository.GetStatus("hour.01").State);
    }

    [Fact]
    public async Task Saving_a_take_stores_it_as_the_owners_recording()
    {
        await _rig.Recorder.StartAsync("minute.24");
        var take = await _rig.Recorder.StopAsync();
        await _rig.Recorder.SaveAsync(take!);

        var status = _rig.Repository.GetStatus("minute.24");
        Assert.Equal(AssetState.Ready, status.State);
        Assert.Equal(AssetSource.User, status.Source);
    }

    [Fact]
    public async Task Cancel_discards_the_take_and_releases_the_microphone()
    {
        await _rig.Recorder.StartAsync("hour.01");
        await _rig.Recorder.CancelAsync();

        Assert.True(_rig.Input.LastCapture!.Disposed);
        Assert.False(_rig.Recorder.IsRecording);
        Assert.Null(await _rig.Recorder.StopAsync());
        Assert.Equal(AssetState.Missing, _rig.Repository.GetStatus("hour.01").State);
    }

    [Fact]
    public async Task Preview_is_refused_while_recording()
    {
        await _rig.Recorder.StartAsync("hour.01");

        var outcome = await _rig.Recorder.PreviewAsync(Signals.Word());

        Assert.Equal(PlaybackOutcomeKind.Busy, outcome.Kind);
        Assert.Empty(_rig.Output.Played);
        await _rig.Recorder.CancelAsync();
    }

    [Fact]
    public async Task Recording_cannot_start_while_audio_is_playing()
    {
        _rig.Output.Blocker = new TaskCompletionSource();
        var preview = _rig.Recorder.PreviewAsync(Signals.Word());
        await _rig.Output.Started.Task;

        Assert.Equal(RecordingStartStatus.Busy, await _rig.Recorder.StartAsync("hour.01"));

        _rig.Recorder.StopPreview();
        Assert.Equal(PlaybackOutcomeKind.Cancelled, (await preview).Kind);
        Assert.Equal(RecordingStartStatus.Started, await _rig.Recorder.StartAsync("hour.01"));
        await _rig.Recorder.CancelAsync();
    }

    [Fact]
    public async Task Playing_a_missing_recording_reports_failure()
    {
        var outcome = await _rig.Recorder.PlaySavedAsync("hour.07");
        Assert.Equal(PlaybackOutcomeKind.Failed, outcome.Kind);
    }

    [Fact]
    public async Task Another_clip_can_be_replaced_while_a_recording_is_active()
    {
        await _rig.RecordAsync("hour.02");
        await _rig.Recorder.StartAsync("hour.01");

        var replacement = PackBuilder.ClipFor("new hour 2");
        await _rig.Repository.SaveUserClipAsync("hour.02", replacement);
        var take = await _rig.Recorder.StopAsync();
        await _rig.Recorder.SaveAsync(take!);

        Assert.True(_rig.Repository.IsAvailable("hour.01"));
        Assert.Equal(replacement.Samples, (await _rig.Repository.LoadAsync("hour.02"))!.Samples);
    }

    [Fact]
    public async Task Level_and_limit_events_are_forwarded()
    {
        var levels = new List<float>();
        var limitReached = false;
        _rig.Recorder.LevelChanged += (_, l) => levels.Add(l);
        _rig.Recorder.MaxDurationReached += (_, _) => limitReached = true;

        await _rig.Recorder.StartAsync("hour.01");
        _rig.Input.LastCapture!.RaiseLevel(0.5f);
        _rig.Input.LastCapture.RaiseLimit();
        await _rig.Recorder.StopAsync();
        _rig.Input.LastCapture.RaiseLevel(0.9f); // after stop: no longer forwarded

        Assert.Equal(new[] { 0.5f }, levels.ToArray());
        Assert.True(limitReached);
    }
}
