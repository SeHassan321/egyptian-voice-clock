using VoiceClock.Core.Audio;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Startup;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Playback;

public enum AnnouncementStatus
{
    Played,

    /// <summary>Another announcement, preview or recording is already active; the request was ignored.</summary>
    Busy,

    /// <summary>Required recordings are missing or damaged; nothing was spoken.</summary>
    MissingRecordings,

    BlockedByCall,
    Interrupted,
    Cancelled,
    PlaybackFailed,
}

public sealed record AnnouncementResult(
    AnnouncementStatus Status,
    TimeSpeechPlan? Plan,
    IReadOnlyList<AudioSegment> Segments,
    IReadOnlyList<string> MissingAssetIds,
    string? Detail = null)
{
    public bool Succeeded => Status == AnnouncementStatus.Played;

    public string? SpokenText => Segments.Count == 0 ? null : string.Join(' ', Segments.Select(s => s.Text));

    internal static AnnouncementResult Simple(AnnouncementStatus status, TimeSpeechPlan? plan = null, string? detail = null) =>
        new(status, plan, [], [], detail);
}

/// <summary>Per-request changes, used by the setup screen's test tools.</summary>
public sealed record AnnouncementOverrides(bool? UseFullPhrase = null, bool? IncludeDayPeriod = null);

public interface IAnnouncementService
{
    bool IsSpeaking { get; }

    event EventHandler? SpeakingChanged;

    /// <summary>Speaks the current local time.</summary>
    Task<AnnouncementResult> AnnounceNowAsync(CancellationToken cancellationToken = default);

    Task<AnnouncementResult> AnnounceAsync(TimeOnly time, AnnouncementOverrides? overrides = null, CancellationToken cancellationToken = default);

    /// <summary>Replays exactly the last spoken announcement (or speaks the time if there is none yet).</summary>
    Task<AnnouncementResult> RepeatLastAsync(CancellationToken cancellationToken = default);

    void Stop();
}

/// <summary>Builds, resolves, composes and plays time announcements.</summary>
public sealed class AnnouncementService : IAnnouncementService
{
    private readonly ITimeSpeechEngine _engine;
    private readonly IVoiceAssetRepository _repository;
    private readonly ISettingsRepository _settings;
    private readonly IAudioOutput _output;
    private readonly IFeedbackService _feedback;
    private readonly AudioSessionGate _gate;
    private readonly ILocalClock _clock;
    private readonly AppInitializer _initializer;
    private readonly IAppLog _log;
    private readonly object _sync = new();
    private CancellationTokenSource? _playback;
    private LastAnnouncement? _last;
    private bool _isSpeaking;

    public AnnouncementService(
        ITimeSpeechEngine engine,
        IVoiceAssetRepository repository,
        ISettingsRepository settings,
        IAudioOutput output,
        IFeedbackService feedback,
        AudioSessionGate gate,
        ILocalClock clock,
        AppInitializer initializer,
        IAppLog? log = null)
    {
        _engine = engine;
        _repository = repository;
        _settings = settings;
        _output = output;
        _feedback = feedback;
        _gate = gate;
        _clock = clock;
        _initializer = initializer;
        _log = log ?? NullAppLog.Instance;
    }

    public event EventHandler? SpeakingChanged;

    public bool IsSpeaking
    {
        get
        {
            lock (_sync)
            {
                return _isSpeaking;
            }
        }
    }

    public Task<AnnouncementResult> AnnounceNowAsync(CancellationToken cancellationToken = default) =>
        AnnounceAsync(_clock.Now(), null, cancellationToken);

    public async Task<AnnouncementResult> AnnounceAsync(TimeOnly time, AnnouncementOverrides? overrides = null, CancellationToken cancellationToken = default)
    {
        using var lease = _gate.TryAcquire(AudioActivity.Announcement);
        if (lease is null)
        {
            return AnnouncementResult.Simple(AnnouncementStatus.Busy);
        }

        using var playback = BeginPlayback(cancellationToken);
        var token = playback.Token;
        TimeSpeechPlan? plan = null;
        try
        {
            await _initializer.EnsureInitializedAsync(token).ConfigureAwait(false);
            var settings = _settings.Current;
            plan = _engine.BuildPlan(time, new TimeSpeechSettings(overrides?.IncludeDayPeriod ?? settings.IncludeDayPeriod));
            var useFullPhrase = overrides?.UseFullPhrase ?? settings.UseFullPhraseOverrides;

            var (resolved, clips) = await ResolveAndLoadAsync(plan, useFullPhrase, token).ConfigureAwait(false);
            if (!resolved.IsPlayable && resolved.UsesFullPhrase)
            {
                // The complete sentence turned out to be unreadable: fall back to the segments.
                (resolved, clips) = await ResolveAndLoadAsync(plan, false, token).ConfigureAwait(false);
            }

            if (!resolved.IsPlayable)
            {
                _log.Warn($"Cannot speak {plan}: missing {string.Join(", ", resolved.MissingAssetIds)}");
                await SignalErrorAsync(settings, missingRecordings: true, token).ConfigureAwait(false);
                return new AnnouncementResult(AnnouncementStatus.MissingRecordings, plan, [], resolved.MissingAssetIds);
            }

            var audio = AnnouncementComposer.Compose(clips, CompositionFor(settings));
            var outcome = await _output.PlayAsync(audio, token).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                lock (_sync)
                {
                    _last = new LastAnnouncement(plan, resolved.Segments, audio);
                }

                return new AnnouncementResult(AnnouncementStatus.Played, plan, resolved.Segments, []);
            }

            return await MapFailureAsync(outcome, plan, settings).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return AnnouncementResult.Simple(AnnouncementStatus.Cancelled, plan);
        }
        finally
        {
            EndPlayback(playback);
        }
    }

    public async Task<AnnouncementResult> RepeatLastAsync(CancellationToken cancellationToken = default)
    {
        LastAnnouncement? last;
        lock (_sync)
        {
            last = _last;
        }

        if (last is null)
        {
            return await AnnounceNowAsync(cancellationToken).ConfigureAwait(false);
        }

        using var lease = _gate.TryAcquire(AudioActivity.Announcement);
        if (lease is null)
        {
            return AnnouncementResult.Simple(AnnouncementStatus.Busy, last.Plan);
        }

        using var playback = BeginPlayback(cancellationToken);
        try
        {
            var outcome = await _output.PlayAsync(last.Audio, playback.Token).ConfigureAwait(false);
            return outcome.Succeeded
                ? new AnnouncementResult(AnnouncementStatus.Played, last.Plan, last.Segments, [])
                : await MapFailureAsync(outcome, last.Plan, _settings.Current).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (playback.Token.IsCancellationRequested)
        {
            return AnnouncementResult.Simple(AnnouncementStatus.Cancelled, last.Plan);
        }
        finally
        {
            EndPlayback(playback);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _playback?.Cancel();
        }
    }

    private async Task<(ResolvedAnnouncement Resolved, List<PcmAudio> Clips)> ResolveAndLoadAsync(TimeSpeechPlan plan, bool useFullPhrase, CancellationToken token)
    {
        var resolved = AudioPlanResolver.Resolve(plan, _repository.IsAvailable, useFullPhrase);
        var clips = new List<PcmAudio>(resolved.Segments.Count);
        if (!resolved.IsPlayable)
        {
            return (resolved, clips);
        }

        var missing = new List<string>();
        foreach (var segment in resolved.Segments)
        {
            var audio = await _repository.LoadAsync(segment.AssetId, token).ConfigureAwait(false);
            if (audio is null)
            {
                missing.Add(segment.AssetId);
            }
            else
            {
                clips.Add(audio);
            }
        }

        return missing.Count == 0 ? (resolved, clips) : (resolved with { MissingAssetIds = missing }, clips);
    }

    private async Task<AnnouncementResult> MapFailureAsync(PlaybackOutcome outcome, TimeSpeechPlan plan, AppSettings settings)
    {
        var status = outcome.Kind switch
        {
            PlaybackOutcomeKind.Cancelled => AnnouncementStatus.Cancelled,
            PlaybackOutcomeKind.Interrupted => AnnouncementStatus.Interrupted,
            PlaybackOutcomeKind.BlockedByCall => AnnouncementStatus.BlockedByCall,
            PlaybackOutcomeKind.Busy => AnnouncementStatus.Busy,
            _ => AnnouncementStatus.PlaybackFailed,
        };

        if (status == AnnouncementStatus.PlaybackFailed)
        {
            _log.Warn($"Playback failed: {outcome.Kind} {outcome.Detail}");
            await SignalErrorAsync(settings, missingRecordings: false, CancellationToken.None).ConfigureAwait(false);
        }

        return AnnouncementResult.Simple(status, plan, outcome.Detail);
    }

    /// <summary>
    /// Signals that the time could not be spoken. The owner's recorded message («فيه تسجيلات ناقصة»)
    /// is used only when recordings are really missing; any other problem gets a beep and vibration.
    /// </summary>
    private async Task SignalErrorAsync(AppSettings settings, bool missingRecordings, CancellationToken token)
    {
        if (!settings.PlayErrorFeedback)
        {
            return;
        }

        try
        {
            if (missingRecordings &&
                _repository.IsAvailable(AssetIds.FeedbackError) &&
                await _repository.LoadAsync(AssetIds.FeedbackError, token).ConfigureAwait(false) is { } message)
            {
                var outcome = await _output.PlayAsync(AnnouncementComposer.Compose([message], CompositionFor(settings)), token).ConfigureAwait(false);
                if (outcome.Succeeded)
                {
                    return;
                }
            }

            await _feedback.SignalErrorAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn("Error feedback failed.", ex);
        }
    }

    private static CompositionOptions CompositionFor(AppSettings settings) => new()
    {
        GapMs = settings.SegmentGapMs,
        NormalizeLoudness = settings.NormalizeLoudness,
    };

    private CancellationTokenSource BeginPlayback(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _playback = cts;
            _isSpeaking = true;
        }

        SpeakingChanged?.Invoke(this, EventArgs.Empty);
        return cts;
    }

    private void EndPlayback(CancellationTokenSource cts)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_playback, cts))
            {
                _playback = null;
            }

            _isSpeaking = false;
        }

        SpeakingChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record LastAnnouncement(TimeSpeechPlan Plan, IReadOnlyList<AudioSegment> Segments, PcmAudio Audio);
}
