using System.Globalization;
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

/// <summary>Setup hub for the person configuring the app: progress, test tools, options, checks and backups.</summary>
public sealed class SetupViewModel : ObservableObject, IDisposable
{
    public const string FullVoiceFileName = VoicePackFormat.BundledFileName;

    private readonly AppInitializer _initializer;
    private readonly ISettingsRepository _settings;
    private readonly IVoiceAssetRepository _repository;
    private readonly RecordingCatalog _catalog;
    private readonly ITimeSpeechEngine _engine;
    private readonly IAnnouncementService _announcer;
    private readonly IBackupFiles _backup;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IVolumeControl _volume;
    private readonly IAppVersion _appInfo;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiagnosticLog _diagnostics;
    private readonly IAppLog _log;
    private CancellationTokenSource? _pendingGapSave;
    private AppSettings _current = AppSettings.Default;
    private string _summaryText = string.Empty;
    private string _completionText = string.Empty;
    private string _bundledText = string.Empty;
    private string _coverageText = string.Empty;
    private string _message = string.Empty;
    private bool _isComplete;
    private double _progress;
    private int _testHourIndex;
    private int _testMinute;
    private bool _testIsPm;
    private double _segmentGapMs = AppSettings.DefaultSegmentGapMs;

    public SetupViewModel(
        AppInitializer initializer,
        ISettingsRepository settings,
        IVoiceAssetRepository repository,
        RecordingCatalog catalog,
        ITimeSpeechEngine engine,
        IAnnouncementService announcer,
        IBackupFiles backup,
        IDialogService dialogs,
        INavigationService navigation,
        IVolumeControl volume,
        IAppVersion appInfo,
        IUiDispatcher dispatcher,
        IDiagnosticLog diagnostics,
        IAppLog? log = null)
    {
        _initializer = initializer;
        _settings = settings;
        _repository = repository;
        _catalog = catalog;
        _engine = engine;
        _announcer = announcer;
        _backup = backup;
        _dialogs = dialogs;
        _navigation = navigation;
        _volume = volume;
        _appInfo = appInfo;
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        _log = log ?? NullAppLog.Instance;

        TestNowCommand = new AsyncCommand(() => ReportAsync(_announcer.AnnounceNowAsync()), HandleError);
        TestSelectedCommand = new AsyncCommand(() => ReportAsync(_announcer.AnnounceAsync(SelectedTestTime)), HandleError);
        TestSegmentsOnlyCommand = new AsyncCommand(
            () => ReportAsync(_announcer.AnnounceAsync(SelectedTestTime, new AnnouncementOverrides(UseFullPhrase: false))),
            HandleError);
        CheckEverythingCommand = new AsyncCommand(CheckEverythingAsync, HandleError);
        OpenRecordingsCommand = new AsyncCommand(() => _navigation.OpenRecordingsAsync(), HandleError);
        RecordNextMissingCommand = new AsyncCommand(RecordNextMissingAsync, HandleError);
        ExportBackupCommand = new AsyncCommand(() => ExportAsync(VoicePackScope.UserRecordings, share: false), HandleError);
        ShareBackupCommand = new AsyncCommand(() => ExportAsync(VoicePackScope.UserRecordings, share: true), HandleError);
        ExportFullVoiceCommand = new AsyncCommand(() => ExportAsync(VoicePackScope.Everything, share: true), HandleError);
        ImportBackupCommand = new AsyncCommand(ImportAsync, HandleError);
        OpenVolumeCommand = new RelayCommand(_volume.ShowSystemVolumePanel);
        ShareReportCommand = new AsyncCommand(ShareReportAsync, HandleError);

        _repository.Changed += OnAssetsChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public AsyncCommand TestNowCommand { get; }

    public AsyncCommand TestSelectedCommand { get; }

    public AsyncCommand TestSegmentsOnlyCommand { get; }

    public AsyncCommand CheckEverythingCommand { get; }

    public AsyncCommand OpenRecordingsCommand { get; }

    public AsyncCommand RecordNextMissingCommand { get; }

    public AsyncCommand ExportBackupCommand { get; }

    public AsyncCommand ShareBackupCommand { get; }

    public AsyncCommand ExportFullVoiceCommand { get; }

    public AsyncCommand ImportBackupCommand { get; }

    public RelayCommand OpenVolumeCommand { get; }

    public AsyncCommand ShareReportCommand { get; }

    /// <summary>Whether warnings or crashes were recorded that the owner could send to the developer.</summary>
    public bool HasProblemReport => _diagnostics.HasEntries;

    public int[] HourOptions { get; } = Enumerable.Range(1, 12).ToArray();

    public string[] MinuteOptions { get; } =
        Enumerable.Range(0, 60).Select(m => m.ToString("00", CultureInfo.InvariantCulture)).ToArray();

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string CompletionText
    {
        get => _completionText;
        private set => SetProperty(ref _completionText, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set => SetProperty(ref _isComplete, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string BundledVoiceText
    {
        get => _bundledText;
        private set => SetProperty(ref _bundledText, value);
    }

    public string CoverageText
    {
        get => _coverageText;
        private set => SetProperty(ref _coverageText, value);
    }

    /// <summary>Result of the last action (test, backup, check).</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string VersionText => Texts.Format(Strings.Setup_Version, _appInfo.VersionLabel);

    public bool IncludeDayPeriod
    {
        get => _current.IncludeDayPeriod;
        set => UpdateSettings(_current with { IncludeDayPeriod = value });
    }

    public bool NormalizeLoudness
    {
        get => _current.NormalizeLoudness;
        set => UpdateSettings(_current with { NormalizeLoudness = value });
    }

    public bool UseFullPhraseOverrides
    {
        get => _current.UseFullPhraseOverrides;
        set => UpdateSettings(_current with { UseFullPhraseOverrides = value });
    }

    public bool PlayErrorFeedback
    {
        get => _current.PlayErrorFeedback;
        set => UpdateSettings(_current with { PlayErrorFeedback = value });
    }

    public double MinSegmentGapMs => AppSettings.MinSegmentGapMs;

    public double MaxSegmentGapMs => AppSettings.MaxSegmentGapMs;

    /// <summary>Bound to a slider; saved after the finger stops moving.</summary>
    public double SegmentGapMs
    {
        get => _segmentGapMs;
        set
        {
            var rounded = Math.Round(Math.Clamp(value, AppSettings.MinSegmentGapMs, AppSettings.MaxSegmentGapMs) / 10) * 10;
            if (SetProperty(ref _segmentGapMs, rounded))
            {
                OnPropertyChanged(nameof(SegmentGapText));
                ScheduleGapSave((int)rounded);
            }
        }
    }

    public string SegmentGapText => Texts.Format(Strings.Setup_SegmentGap, (int)_segmentGapMs);

    public int TestHourIndex
    {
        get => _testHourIndex;
        set
        {
            if (SetProperty(ref _testHourIndex, Math.Clamp(value, 0, 11)))
            {
                OnPropertyChanged(nameof(TestPreviewText));
            }
        }
    }

    public int TestMinute
    {
        get => _testMinute;
        set
        {
            if (SetProperty(ref _testMinute, Math.Clamp(value, 0, 59)))
            {
                OnPropertyChanged(nameof(TestPreviewText));
            }
        }
    }

    public bool TestIsPm
    {
        get => _testIsPm;
        set
        {
            if (SetProperty(ref _testIsPm, value))
            {
                OnPropertyChanged(nameof(TestPreviewText));
            }
        }
    }

    public TimeOnly SelectedTestTime
    {
        get
        {
            var hour12 = _testHourIndex + 1;
            var hour24 = (hour12 % 12) + (_testIsPm ? 12 : 0);
            return new TimeOnly(hour24, _testMinute);
        }
    }

    /// <summary>Exactly what will be said for the selected test time.</summary>
    public string TestPreviewText =>
        Texts.Format(Strings.Setup_WillSay, _engine.BuildPlan(SelectedTestTime, new TimeSpeechSettings(_current.IncludeDayPeriod)).Text);

    public async Task OnAppearingAsync()
    {
        try
        {
            await _initializer.EnsureInitializedAsync();
            _current = _settings.Current;
            _segmentGapMs = _current.SegmentGapMs;
            RaiseSettingsProperties();
            Refresh();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    public void OnDisappearing()
    {
        _announcer.Stop();
        FlushPendingGapSave();
    }

    public void Dispose()
    {
        _repository.Changed -= OnAssetsChanged;
        _settings.Changed -= OnSettingsChanged;
        _pendingGapSave?.Cancel();
    }

    private void Refresh()
    {
        if (!_repository.IsInitialized)
        {
            return;
        }

        var summary = SetupStatusCalculator.Compute(_catalog, _repository, _engine, _current.IncludeDayPeriod);
        SummaryText = Texts.Format(Strings.Setup_Summary, summary.ReadyCount, summary.RequiredCount);
        IsComplete = summary.IsComplete;
        CompletionText = summary.IsComplete ? Strings.Setup_Complete : Strings.Setup_Incomplete;
        Progress = summary.Progress;

        var bundled = _repository.BundledVoice;
        BundledVoiceText = bundled.Problem is not null
            ? Texts.Format(Strings.Setup_Bundled_Problem, bundled.Problem)
            : bundled.Present
                ? Texts.Format(Strings.Setup_Bundled_Present, bundled.ClipCount)
                : Strings.Setup_Bundled_None;

        CoverageText = DescribeCoverage(summary.Coverage);
        OnPropertyChanged(nameof(TestPreviewText));
        OnPropertyChanged(nameof(HasProblemReport));
    }

    private string DescribeCoverage(CoverageReport coverage)
    {
        if (coverage.IsComplete)
        {
            return Texts.Format(Strings.Setup_Coverage_Complete, coverage.TimesChecked);
        }

        var example = coverage.Uncovered[0];
        var missingWords = string.Join("، ", example.MissingAssetIds.Select(id => "«" + (_catalog.Find(id)?.Text ?? id) + "»"));
        return Texts.Format(Strings.Setup_Coverage_Incomplete, coverage.Uncovered.Count, coverage.TimesChecked, Texts.Clock(example.Time), missingWords);
    }

    private async Task ReportAsync(Task<AnnouncementResult> announcement)
    {
        Message = string.Empty;
        var result = await announcement;
        Message = result.Status switch
        {
            AnnouncementStatus.Played => result.SpokenText ?? string.Empty,
            AnnouncementStatus.MissingRecordings => Strings.Main_Status_Missing + " " +
                string.Join("، ", result.MissingAssetIds.Select(id => "«" + (_catalog.Find(id)?.Text ?? id) + "»")),
            AnnouncementStatus.BlockedByCall => Strings.Main_Status_BlockedByCall,
            AnnouncementStatus.Interrupted => Strings.Main_Status_Interrupted,
            AnnouncementStatus.PlaybackFailed => Strings.Main_Status_PlaybackFailed,
            AnnouncementStatus.Busy => Strings.Recorder_Busy,
            _ => string.Empty,
        };
    }

    private async Task CheckEverythingAsync()
    {
        Message = Strings.Setup_Checking;
        var verification = await _repository.VerifyAllAsync();
        Refresh();
        Message = verification.AllValid ? string.Empty : Texts.Format(Strings.Setup_Damaged, verification.InvalidIds.Count);
    }

    private async Task RecordNextMissingAsync()
    {
        var next = _catalog.RequiredEntries(_current.IncludeDayPeriod)
            .FirstOrDefault(e => !_repository.IsAvailable(e.Id));
        if (next is null)
        {
            Message = Strings.Recordings_NothingMissing;
            return;
        }

        await _navigation.OpenRecorderAsync(next.Id);
    }

    private async Task ExportAsync(VoicePackScope scope, bool share)
    {
        if (scope == VoicePackScope.UserRecordings && !HasUserRecordings())
        {
            Message = Strings.Setup_Backup_Empty;
            return;
        }

        var exported = 0;
        async Task Write(Stream stream, CancellationToken token) =>
            exported = await _repository.ExportAsync(stream, scope, _appInfo.VersionLabel, token);

        var fileName = scope == VoicePackScope.Everything
            ? FullVoiceFileName
            : string.Create(CultureInfo.InvariantCulture, $"voice-clock-backup-{DateTime.Now:yyyy-MM-dd-HHmm}{VoicePackFormat.FileExtension}");

        if (share)
        {
            await _backup.ShareAsync(fileName, VoicePackFormat.MimeType, Write);
            Message = Texts.Format(Strings.Setup_Backup_Saved, exported);
            return;
        }

        var saved = await _backup.SaveAsync(fileName, Write);
        Message = saved ? Texts.Format(Strings.Setup_Backup_Saved, exported) : Strings.Setup_Backup_Cancelled;
    }

    private async Task ShareReportAsync()
    {
        if (!_diagnostics.HasEntries)
        {
            Message = Strings.Setup_NoProblems;
            return;
        }

        async Task Write(Stream stream, CancellationToken token)
        {
            var header = System.Text.Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"Egyptian Voice Clock {_appInfo.VersionLabel}\nReport created {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z\n\n"));
            await stream.WriteAsync(header, token);
            await _diagnostics.CopyToAsync(stream, token);
        }

        await _backup.ShareAsync("voice-clock-report.txt", "text/plain", Write);
    }

    private bool HasUserRecordings() =>
        _catalog.Entries.Any(e => _repository.GetStatus(e.Id).HasUserRecording) ||
        _repository.AvailableFullPhraseIds.Any(id => _repository.GetStatus(id).HasUserRecording);

    private async Task ImportAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            Strings.Setup_Import_ConfirmTitle,
            Strings.Setup_Import_ConfirmMessage,
            Strings.Common_Ok,
            Strings.Common_Cancel);
        if (!confirmed)
        {
            return;
        }

        var stream = await _backup.PickAsync();
        if (stream is null)
        {
            Message = Strings.Setup_Backup_Cancelled;
            return;
        }

        VoicePackImportResult result;
        await using (stream)
        {
            result = await _repository.ImportAsync(stream);
        }

        if (!result.Accepted)
        {
            Message = Strings.Setup_Import_Rejected;
            return;
        }

        var parts = new List<string> { Texts.Format(Strings.Setup_Import_Done, result.Imported) };
        if (result.MatchedDefault > 0)
        {
            parts.Add(Texts.Format(Strings.Setup_Import_MatchedDefault, result.MatchedDefault));
        }

        var skipped = result.Problems.Select(p => p.ClipId).Distinct(StringComparer.Ordinal).Count();
        if (skipped > 0)
        {
            parts.Add(Texts.Format(Strings.Setup_Import_Skipped, skipped));
        }

        Message = string.Join(" ", parts);
        Refresh();
    }

    private void UpdateSettings(AppSettings updated)
    {
        if (updated == _current)
        {
            return;
        }

        _current = updated;
        RaiseSettingsProperties();
        Refresh();
        _ = SaveSettingsAsync(updated);
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        try
        {
            await _settings.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    private void ScheduleGapSave(int gapMs)
    {
        _pendingGapSave?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingGapSave = cts;
        _ = SaveGapLaterAsync(gapMs, cts.Token);
    }

    private async Task SaveGapLaterAsync(int gapMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), token);
            _dispatcher.Post(() => UpdateSettings(_current with { SegmentGapMs = gapMs }));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FlushPendingGapSave()
    {
        if (_pendingGapSave is { IsCancellationRequested: false } pending)
        {
            pending.Cancel();
            UpdateSettings(_current with { SegmentGapMs = (int)_segmentGapMs });
        }
    }

    private void RaiseSettingsProperties()
    {
        OnPropertyChanged(nameof(IncludeDayPeriod));
        OnPropertyChanged(nameof(NormalizeLoudness));
        OnPropertyChanged(nameof(UseFullPhraseOverrides));
        OnPropertyChanged(nameof(PlayErrorFeedback));
        OnPropertyChanged(nameof(SegmentGapMs));
        OnPropertyChanged(nameof(SegmentGapText));
        OnPropertyChanged(nameof(TestPreviewText));
    }

    private void OnAssetsChanged(object? sender, AssetsChangedEventArgs e) => _dispatcher.Post(Refresh);

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(() =>
    {
        if (settings != _current && (_pendingGapSave is null || _pendingGapSave.IsCancellationRequested))
        {
            _current = settings;
            _segmentGapMs = settings.SegmentGapMs;
            RaiseSettingsProperties();
            Refresh();
        }
    });

    private void HandleError(Exception ex)
    {
        _log.Warn("Setup action failed.", ex);
        Message = Strings.Common_Error_Message;
    }
}
