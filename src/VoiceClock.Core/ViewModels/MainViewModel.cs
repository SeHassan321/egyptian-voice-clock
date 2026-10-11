using VoiceClock.Core.Catalog;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Resources;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Startup;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Time;
using VoiceClock.Core.Validation;

namespace VoiceClock.Core.ViewModels;

/// <summary>
/// The voice-first home screen: one big "say the time" button, repeat, volume and setup.
/// Nothing here shows the time as text; status messages are only for the person who set the app up.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAnnouncementService _announcer;
    private readonly IVolumeControl _volume;
    private readonly INavigationService _navigation;
    private readonly IFeedbackService _feedback;
    private readonly AppInitializer _initializer;
    private readonly RecordingCatalog _catalog;
    private readonly IVoiceAssetRepository _repository;
    private readonly ITimeSpeechEngine _engine;
    private readonly ISettingsRepository _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLog _log;
    private bool _isSpeaking;
    private bool _isReady;
    private string _statusMessage = string.Empty;
    private string _setupMessage = string.Empty;

    public MainViewModel(
        IAnnouncementService announcer,
        IVolumeControl volume,
        INavigationService navigation,
        IFeedbackService feedback,
        AppInitializer initializer,
        RecordingCatalog catalog,
        IVoiceAssetRepository repository,
        ITimeSpeechEngine engine,
        ISettingsRepository settings,
        IUiDispatcher dispatcher,
        IAppLog? log = null)
    {
        _announcer = announcer;
        _volume = volume;
        _navigation = navigation;
        _feedback = feedback;
        _initializer = initializer;
        _catalog = catalog;
        _repository = repository;
        _engine = engine;
        _settings = settings;
        _dispatcher = dispatcher;
        _log = log ?? NullAppLog.Instance;

        AnnounceCommand = new AsyncCommand(() => SpeakAsync(_announcer.AnnounceNowAsync), HandleError);
        RepeatCommand = new AsyncCommand(() => SpeakAsync(_announcer.RepeatLastAsync), HandleError);
        VolumeUpCommand = new RelayCommand(() =>
        {
            _feedback.Tap();
            _volume.Raise();
        });
        VolumeDownCommand = new RelayCommand(() =>
        {
            _feedback.Tap();
            _volume.Lower();
        });
        OpenSetupCommand = new AsyncCommand(OpenSetupAsync, HandleError);

        _announcer.SpeakingChanged += OnSpeakingChanged;
        _repository.Changed += OnAssetsChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public AsyncCommand AnnounceCommand { get; }

    public AsyncCommand RepeatCommand { get; }

    public RelayCommand VolumeUpCommand { get; }

    public RelayCommand VolumeDownCommand { get; }

    public AsyncCommand OpenSetupCommand { get; }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set => SetProperty(ref _isSpeaking, value);
    }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    /// <summary>A short note for the caretaker (empty when everything is fine).</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => StatusMessage.Length > 0;

    public async Task OnAppearingAsync()
    {
        try
        {
            await _initializer.EnsureInitializedAsync();
            IsReady = true;
            RefreshSetupMessage();
            StatusMessage = _setupMessage;
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    public void OnDisappearing() => _announcer.Stop();

    public void Dispose()
    {
        _announcer.SpeakingChanged -= OnSpeakingChanged;
        _repository.Changed -= OnAssetsChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    private async Task SpeakAsync(Func<CancellationToken, Task<AnnouncementResult>> speak)
    {
        _feedback.Tap();
        if (_volume.IsMuted)
        {
            // Speaking now would be silent and look like a broken button.
            _volume.ShowSystemVolumePanel();
            StatusMessage = Strings.Main_Status_Muted;
            await _feedback.SignalErrorAsync(CancellationToken.None);
            return;
        }

        var result = await speak(CancellationToken.None);
        StatusMessage = result.Status switch
        {
            AnnouncementStatus.Played => _setupMessage,
            AnnouncementStatus.MissingRecordings => Strings.Main_Status_Missing,
            AnnouncementStatus.BlockedByCall => Strings.Main_Status_BlockedByCall,
            AnnouncementStatus.Interrupted => Strings.Main_Status_Interrupted,
            AnnouncementStatus.PlaybackFailed => Strings.Main_Status_PlaybackFailed,
            _ => StatusMessage,
        };
    }

    private async Task OpenSetupAsync()
    {
        _feedback.Tap();
        _announcer.Stop();
        await _navigation.OpenSetupAsync();
    }

    private void RefreshSetupMessage()
    {
        if (!_repository.IsInitialized)
        {
            return;
        }

        var summary = SetupStatusCalculator.Compute(_catalog, _repository, _engine, _settings.Current.IncludeDayPeriod);
        _setupMessage = summary.IsComplete
            ? string.Empty
            : Texts.Format(Strings.Main_Status_SetupIncomplete, Math.Max(1, summary.MissingCount + summary.InvalidCount));
    }

    private void OnSpeakingChanged(object? sender, EventArgs e) =>
        _dispatcher.Post(() => IsSpeaking = _announcer.IsSpeaking);

    private void OnAssetsChanged(object? sender, AssetsChangedEventArgs e) => _dispatcher.Post(RefreshAfterChange);

    private void OnSettingsChanged(object? sender, AppSettings e) => _dispatcher.Post(RefreshAfterChange);

    private void RefreshAfterChange()
    {
        var wasShowingSetup = StatusMessage == _setupMessage;
        RefreshSetupMessage();
        if (wasShowingSetup)
        {
            StatusMessage = _setupMessage;
        }
    }

    private void HandleError(Exception ex)
    {
        _log.Warn("Main screen action failed.", ex);
        StatusMessage = Strings.Common_Error_Message;
    }
}
