using System.Diagnostics;
using VoiceClock.Core.Catalog;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Recording;
using VoiceClock.Core.Resources;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Startup;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.ViewModels;

/// <summary>Record, check and save one clip, then move on to the next missing one.</summary>
public sealed class RecordClipViewModel : ObservableObject, IDisposable
{
    private readonly IRecordingService _recorder;
    private readonly IVoiceAssetRepository _repository;
    private readonly RecordingCatalog _catalog;
    private readonly ISettingsRepository _settings;
    private readonly IMicrophonePermission _microphone;
    private readonly IDialogService _dialogs;
    private readonly IAnnouncementService _announcer;
    private readonly IFeedbackService _feedback;
    private readonly AppInitializer _initializer;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLog _log;
    private CatalogEntry? _entry;
    private RecordingTake? _take;
    private CancellationTokenSource? _elapsedLoop;
    private bool _isRecording;
    private double _level;
    private string _elapsedText = string.Empty;
    private string _message = string.Empty;
    private string _savedStatusText = string.Empty;
    private string _positionText = string.Empty;

    public RecordClipViewModel(
        IRecordingService recorder,
        IVoiceAssetRepository repository,
        RecordingCatalog catalog,
        ISettingsRepository settings,
        IMicrophonePermission microphone,
        IDialogService dialogs,
        IAnnouncementService announcer,
        IFeedbackService feedback,
        AppInitializer initializer,
        IUiDispatcher dispatcher,
        IAppLog? log = null)
    {
        _recorder = recorder;
        _repository = repository;
        _catalog = catalog;
        _settings = settings;
        _microphone = microphone;
        _dialogs = dialogs;
        _announcer = announcer;
        _feedback = feedback;
        _initializer = initializer;
        _dispatcher = dispatcher;
        _log = log ?? NullAppLog.Instance;

        ToggleRecordingCommand = new AsyncCommand(ToggleRecordingAsync, HandleError, () => _entry is not null);
        PlayTakeCommand = new AsyncCommand(PlayTakeAsync, HandleError, () => _take is not null && !_isRecording);
        SaveCommand = new AsyncCommand(SaveAsync, HandleError, () => _take is { IsValid: true } && !_isRecording);
        DiscardCommand = new AsyncCommand(DiscardAsync, HandleError, () => _take is not null && !_isRecording);
        PlaySavedCommand = new AsyncCommand(PlaySavedAsync, HandleError, () => _entry is not null && _repository.IsAvailable(_entry.Id) && !_isRecording);
        CompareComposedCommand = new AsyncCommand(CompareComposedAsync, HandleError, () => IsFullPhrase && !_isRecording);
        NextCommand = new AsyncCommand(() => MoveAsync(+1), HandleError, () => Neighbour(+1) is not null && !_isRecording);
        PreviousCommand = new AsyncCommand(() => MoveAsync(-1), HandleError, () => Neighbour(-1) is not null && !_isRecording);

        _recorder.LevelChanged += OnLevelChanged;
        _recorder.MaxDurationReached += OnMaxDurationReached;
        _repository.Changed += OnAssetsChanged;
    }

    public AsyncCommand ToggleRecordingCommand { get; }

    public AsyncCommand PlayTakeCommand { get; }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand DiscardCommand { get; }

    public AsyncCommand PlaySavedCommand { get; }

    public AsyncCommand CompareComposedCommand { get; }

    public AsyncCommand NextCommand { get; }

    public AsyncCommand PreviousCommand { get; }

    public string AssetId => _entry?.Id ?? string.Empty;

    /// <summary>Exactly what to say.</summary>
    public string PhraseText => _entry?.Text ?? string.Empty;

    public string CategoryText => _entry is null ? string.Empty : Texts.Category(_entry.Category);

    public bool IsFullPhrase => _entry?.Category == AssetCategory.FullPhrase;

    public string FullPhraseInfo => IsFullPhrase ? Strings.Recorder_FullPhraseInfo : string.Empty;

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string SavedStatusText
    {
        get => _savedStatusText;
        private set => SetProperty(ref _savedStatusText, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(RecordButtonText));
                RaiseCommandStates();
            }
        }
    }

    public string RecordButtonText => _isRecording ? Strings.Recorder_Stop : Strings.Recorder_Start;

    /// <summary>Microphone level 0–1 while recording.</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public bool HasTake => _take is not null;

    public bool TakeIsValid => _take?.IsValid == true;

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public async Task LoadAsync(string assetId)
    {
        await _initializer.EnsureInitializedAsync();
        await StopEverythingAsync();
        _entry = _catalog.Find(assetId) ?? throw new ArgumentException($"Unknown recording id '{assetId}'.", nameof(assetId));
        SetTake(null);
        Message = string.Empty;
        ElapsedText = string.Empty;
        Level = 0;

        var index = IndexOf(_entry);
        PositionText = index >= 0 ? Texts.Format(Strings.Recorder_Position, index + 1, _catalog.Entries.Count) : string.Empty;
        RefreshSavedStatus();

        foreach (var name in new[] { nameof(AssetId), nameof(PhraseText), nameof(CategoryText), nameof(IsFullPhrase), nameof(FullPhraseInfo) })
        {
            OnPropertyChanged(name);
        }

        RaiseCommandStates();
    }

    public Task OnDisappearingAsync() => StopEverythingAsync();

    public void Dispose()
    {
        _recorder.LevelChanged -= OnLevelChanged;
        _recorder.MaxDurationReached -= OnMaxDurationReached;
        _repository.Changed -= OnAssetsChanged;
        _elapsedLoop?.Cancel();
    }

    private async Task ToggleRecordingAsync()
    {
        if (_isRecording)
        {
            await StopRecordingAsync();
            return;
        }

        var access = await _microphone.RequestAsync();
        if (access == MicrophoneAccess.Denied)
        {
            Message = Strings.Recorder_PermissionDenied;
            return;
        }

        if (access == MicrophoneAccess.PermanentlyDenied)
        {
            if (await _dialogs.ConfirmAsync(
                    Strings.Recorder_PermissionBlockedTitle,
                    Strings.Recorder_PermissionBlockedMessage,
                    Strings.Recorder_OpenSettings,
                    Strings.Common_Cancel))
            {
                _microphone.OpenAppSettings();
            }

            return;
        }

        _recorder.StopPreview();
        _announcer.Stop();
        SetTake(null);

        var status = await _recorder.StartAsync(_entry!.Id);
        switch (status)
        {
            case RecordingStartStatus.Started:
                _feedback.Tap();
                Message = Strings.Recorder_Recording;
                IsRecording = true;
                StartElapsedLoop();
                break;
            case RecordingStartStatus.Busy:
                Message = Strings.Recorder_Busy;
                break;
            default:
                Message = Strings.Recorder_MicUnavailable;
                break;
        }
    }

    private async Task StopRecordingAsync()
    {
        _elapsedLoop?.Cancel();
        var take = await _recorder.StopAsync();
        IsRecording = false;
        Level = 0;
        SetTake(take);
        Message = take is null ? Strings.Recorder_Take_Empty : Texts.Take(take.Clip);
    }

    private async Task PlayTakeAsync()
    {
        var outcome = await _recorder.PreviewAsync(_take!.Audio);
        if (outcome.Kind == PlaybackOutcomeKind.Busy)
        {
            Message = Strings.Recorder_Busy;
        }
    }

    private async Task SaveAsync()
    {
        var take = _take!;
        await _recorder.SaveAsync(take);
        SetTake(null);

        // Guided flow: continue with the next missing clip after this one, if any.
        var next = NextMissingAfter(_entry!);
        if (next is not null)
        {
            await LoadAsync(next.Id);
        }
        else
        {
            RefreshSavedStatus();
        }

        Message = Strings.Recorder_Saved;
    }

    private Task DiscardAsync()
    {
        SetTake(null);
        Message = string.Empty;
        return Task.CompletedTask;
    }

    private async Task PlaySavedAsync()
    {
        var outcome = await _recorder.PlaySavedAsync(_entry!.Id);
        if (outcome.Kind == PlaybackOutcomeKind.Busy)
        {
            Message = Strings.Recorder_Busy;
        }
    }

    /// <summary>For a full sentence: hear the same time built from the individual words, to compare.</summary>
    private async Task CompareComposedAsync()
    {
        if (!AssetIds.TryParseFullPhrase(_entry!.Id, out var hour12, out var minute))
        {
            return;
        }

        var result = await _announcer.AnnounceAsync(
            new TimeOnly(hour12 % 12, minute),
            new AnnouncementOverrides(UseFullPhrase: false, IncludeDayPeriod: false));
        if (result.Status == AnnouncementStatus.MissingRecordings)
        {
            Message = Strings.Main_Status_Missing;
        }
    }

    private async Task MoveAsync(int direction)
    {
        if (Neighbour(direction) is { } target)
        {
            await LoadAsync(target.Id);
        }
    }

    private CatalogEntry? Neighbour(int direction)
    {
        if (_entry is null)
        {
            return null;
        }

        var index = IndexOf(_entry);
        if (index < 0)
        {
            return null;
        }

        var target = index + direction;
        return target >= 0 && target < _catalog.Entries.Count ? _catalog.Entries[target] : null;
    }

    private CatalogEntry? NextMissingAfter(CatalogEntry current)
    {
        var includeDayPeriod = _settings.Current.IncludeDayPeriod;
        var index = IndexOf(current);
        return index < 0
            ? null
            : _catalog.Entries.Skip(index + 1).FirstOrDefault(e => e.IsRequired(includeDayPeriod) && !_repository.IsAvailable(e.Id));
    }

    private int IndexOf(CatalogEntry entry)
    {
        for (var i = 0; i < _catalog.Entries.Count; i++)
        {
            if (_catalog.Entries[i].Id == entry.Id)
            {
                return i;
            }
        }

        return -1;
    }

    private void RefreshSavedStatus()
    {
        if (_entry is null)
        {
            return;
        }

        var status = _repository.GetStatus(_entry.Id);
        SavedStatusText = status.IsReady || status.State == AssetState.Invalid
            ? Texts.Status(status, _entry.IsRequired(_settings.Current.IncludeDayPeriod))
            : Strings.Recorder_NotSaved;
        PlaySavedCommand.RaiseCanExecuteChanged();
    }

    private void SetTake(RecordingTake? take)
    {
        _take = take;
        OnPropertyChanged(nameof(HasTake));
        OnPropertyChanged(nameof(TakeIsValid));
        RaiseCommandStates();
    }

    private async Task StopEverythingAsync()
    {
        _elapsedLoop?.Cancel();
        _recorder.StopPreview();
        if (_isRecording)
        {
            await _recorder.CancelAsync();
            IsRecording = false;
        }
    }

    private void StartElapsedLoop()
    {
        _elapsedLoop?.Cancel();
        var cts = new CancellationTokenSource();
        _elapsedLoop = cts;
        var stopwatch = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var seconds = stopwatch.Elapsed.TotalSeconds;
                    _dispatcher.Post(() => ElapsedText = Texts.Format(Strings.Recorder_Elapsed, seconds));
                    await Task.Delay(100, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RaiseCommandStates()
    {
        ToggleRecordingCommand.RaiseCanExecuteChanged();
        PlayTakeCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        PlaySavedCommand.RaiseCanExecuteChanged();
        CompareComposedCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
    }

    private void OnLevelChanged(object? sender, float level) => _dispatcher.Post(() => Level = level);

    private void OnMaxDurationReached(object? sender, EventArgs e) => _dispatcher.Post(async () =>
    {
        try
        {
            if (_isRecording)
            {
                await StopRecordingAsync();
                Message = Strings.Recorder_MaxReached + " " + Message;
            }
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    });

    private void OnAssetsChanged(object? sender, AssetsChangedEventArgs e)
    {
        if (_entry is not null && (e.AllChanged || e.AssetIds.Contains(_entry.Id)))
        {
            _dispatcher.Post(RefreshSavedStatus);
        }
    }

    private void HandleError(Exception ex)
    {
        _log.Warn("Recording action failed.", ex);
        IsRecording = _recorder.IsRecording;
        Message = Strings.Common_Error_Message;
    }
}
