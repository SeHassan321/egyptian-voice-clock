using VoiceClock.Core.Audio;
using VoiceClock.Core.Catalog;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Storage;

namespace VoiceClock.Core.Recording;

/// <summary>Raised by the platform when the microphone cannot be used.</summary>
public sealed class AudioInputException : Exception
{
    public AudioInputException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A running microphone capture.</summary>
public interface IAudioCapture : IAsyncDisposable
{
    /// <summary>Input level 0–1, raised from the capture thread several times per second.</summary>
    event EventHandler<float>? LevelChanged;

    /// <summary>The capture stopped itself because it reached its maximum length.</summary>
    event EventHandler? LimitReached;

    TimeSpan Elapsed { get; }

    /// <summary>Stops capturing and returns everything recorded (mono, 16-bit).</summary>
    Task<PcmAudio> StopAsync();
}

/// <summary>Platform microphone.</summary>
public interface IAudioInput
{
    /// <summary>Starts capturing. Throws <see cref="AudioInputException"/> if the microphone is unavailable.</summary>
    Task<IAudioCapture> StartAsync(int sampleRate, TimeSpan maxDuration, CancellationToken cancellationToken);
}

public enum RecordingStartStatus
{
    Started,

    /// <summary>Something else is recording or playing.</summary>
    Busy,

    DeviceUnavailable,
}

/// <summary>A finished recording, already validated and trimmed, not yet saved.</summary>
public sealed record RecordingTake(string AssetId, ProcessedClip Clip)
{
    public bool IsValid => Clip.IsValid;

    public PcmAudio Audio => Clip.Audio;
}

public interface IRecordingService
{
    TimeSpan MaxDuration { get; }

    bool IsRecording { get; }

    event EventHandler<float>? LevelChanged;

    event EventHandler? MaxDurationReached;

    Task<RecordingStartStatus> StartAsync(string assetId, CancellationToken cancellationToken = default);

    /// <summary>Stops the current recording and returns the processed take (null if nothing was recording).</summary>
    Task<RecordingTake?> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops and discards the current recording, releasing the microphone.</summary>
    Task CancelAsync();

    /// <summary>Plays a take or any clip for checking.</summary>
    Task<PlaybackOutcome> PreviewAsync(PcmAudio audio, CancellationToken cancellationToken = default);

    /// <summary>Plays the currently effective recording for an asset.</summary>
    Task<PlaybackOutcome> PlaySavedAsync(string assetId, CancellationToken cancellationToken = default);

    void StopPreview();

    /// <summary>Saves a valid take, atomically replacing any previous recording of the same asset only.</summary>
    Task SaveAsync(RecordingTake take, CancellationToken cancellationToken = default);
}

/// <summary>Coordinates the microphone, previews and saving, one activity at a time.</summary>
public sealed class RecordingService : IRecordingService
{
    private readonly IAudioInput _input;
    private readonly IAudioOutput _output;
    private readonly IVoiceAssetRepository _repository;
    private readonly RecordingCatalog _catalog;
    private readonly AudioSessionGate _gate;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _previewSync = new();
    private IAudioCapture? _capture;
    private AudioLease? _lease;
    private string? _assetId;
    private CancellationTokenSource? _preview;

    public RecordingService(
        IAudioInput input,
        IAudioOutput output,
        IVoiceAssetRepository repository,
        RecordingCatalog catalog,
        AudioSessionGate gate,
        IAppLog? log = null)
    {
        _input = input;
        _output = output;
        _repository = repository;
        _catalog = catalog;
        _gate = gate;
        _log = log ?? NullAppLog.Instance;
    }

    public event EventHandler<float>? LevelChanged;

    public event EventHandler? MaxDurationReached;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(12);

    public bool IsRecording => Volatile.Read(ref _capture) is not null;

    public async Task<RecordingStartStatus> StartAsync(string assetId, CancellationToken cancellationToken = default)
    {
        if (!_catalog.IsKnownId(assetId))
        {
            throw new ArgumentException($"Unknown recording id '{assetId}'.", nameof(assetId));
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null)
            {
                return RecordingStartStatus.Busy;
            }

            var lease = _gate.TryAcquire(AudioActivity.Recording);
            if (lease is null)
            {
                return RecordingStartStatus.Busy;
            }

            IAudioCapture capture;
            try
            {
                capture = await _input.StartAsync(PcmAudio.StandardSampleRate, MaxDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (AudioInputException ex)
            {
                lease.Dispose();
                _log.Warn("Microphone unavailable.", ex);
                return RecordingStartStatus.DeviceUnavailable;
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            capture.LevelChanged += OnLevelChanged;
            capture.LimitReached += OnLimitReached;
            _lease = lease;
            _assetId = assetId;
            Volatile.Write(ref _capture, capture);
            return RecordingStartStatus.Started;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RecordingTake?> StopAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (capture, lease, assetId) = Detach();
            if (capture is null)
            {
                return null;
            }

            try
            {
                var raw = await capture.StopAsync().ConfigureAwait(false);
                return new RecordingTake(assetId!, ClipProcessor.Process(raw));
            }
            finally
            {
                await ReleaseAsync(capture, lease).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CancelAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var (capture, lease, _) = Detach();
            if (capture is not null)
            {
                await ReleaseAsync(capture, lease).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlaybackOutcome> PreviewAsync(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using var lease = _gate.TryAcquire(AudioActivity.Preview);
        if (lease is null)
        {
            return PlaybackOutcome.Busy;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_previewSync)
        {
            _preview = cts;
        }

        try
        {
            var composed = AnnouncementComposer.Compose([audio], new CompositionOptions { LeadInMs = 80, TailMs = 80 });
            return await _output.PlayAsync(composed, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return PlaybackOutcome.Cancelled;
        }
        finally
        {
            lock (_previewSync)
            {
                if (ReferenceEquals(_preview, cts))
                {
                    _preview = null;
                }
            }
        }
    }

    public async Task<PlaybackOutcome> PlaySavedAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var audio = await _repository.LoadAsync(assetId, cancellationToken).ConfigureAwait(false);
        return audio is null
            ? new PlaybackOutcome(PlaybackOutcomeKind.Failed, "missing")
            : await PreviewAsync(audio, cancellationToken).ConfigureAwait(false);
    }

    public void StopPreview()
    {
        lock (_previewSync)
        {
            _preview?.Cancel();
        }
    }

    public Task SaveAsync(RecordingTake take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(take);
        if (!take.IsValid)
        {
            throw new InvalidOperationException("An invalid recording cannot be saved: " + string.Join(", ", take.Clip.Issues));
        }

        return _repository.SaveUserClipAsync(take.AssetId, take.Audio, cancellationToken);
    }

    private (IAudioCapture? Capture, AudioLease? Lease, string? AssetId) Detach()
    {
        var capture = Volatile.Read(ref _capture);
        var result = (capture, _lease, _assetId);
        Volatile.Write(ref _capture, null);
        _lease = null;
        _assetId = null;
        if (capture is not null)
        {
            capture.LevelChanged -= OnLevelChanged;
            capture.LimitReached -= OnLimitReached;
        }

        return result;
    }

    private async Task ReleaseAsync(IAudioCapture capture, AudioLease? lease)
    {
        try
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Releasing the microphone failed.", ex);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private void OnLevelChanged(object? sender, float level) => LevelChanged?.Invoke(this, level);

    private void OnLimitReached(object? sender, EventArgs e) => MaxDurationReached?.Invoke(this, EventArgs.Empty);
}
