using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>One row of the recording checklist.</summary>
public sealed class ClipItemViewModel : ObservableObject
{
    private AssetStatus _status;
    private bool _isRequired;

    public ClipItemViewModel(CatalogEntry entry, AssetStatus status, bool isRequired)
    {
        Entry = entry;
        _status = status;
        _isRequired = isRequired;
    }

    public CatalogEntry Entry { get; }

    public string Id => Entry.Id;

    public string Text => Entry.Text;

    public AssetStatus Status => _status;

    public bool IsReady => _status.IsReady;

    public bool IsMissing => _status.State == AssetState.Missing;

    public bool IsInvalid => _status.State == AssetState.Invalid;

    public bool IsUserRecording => _status.IsReady && _status.Source == AssetSource.User;

    public bool NeedsAttention => !_status.IsReady && _isRequired;

    public string StatusText => Texts.Status(_status, _isRequired);

    /// <summary>Own recording on top of a shipped default: offer "back to default".</summary>
    public bool CanRestoreDefault => _status.HasUserRecording && _status.HasBundledDefault;

    /// <summary>Own recording without a default: offer "delete".</summary>
    public bool CanDelete => _status.HasUserRecording && !_status.HasBundledDefault;

    public void Update(AssetStatus status, bool isRequired)
    {
        _status = status;
        _isRequired = isRequired;
        foreach (var name in new[]
        {
            nameof(Status), nameof(IsReady), nameof(IsMissing), nameof(IsInvalid), nameof(IsUserRecording),
            nameof(NeedsAttention), nameof(StatusText), nameof(CanRestoreDefault), nameof(CanDelete),
        })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>A titled section of the checklist (a collection, as grouped lists require).</summary>
public sealed class ClipGroupViewModel : ObservableCollection<ClipItemViewModel>
{
    public ClipGroupViewModel(AssetCategory category, IEnumerable<ClipItemViewModel> items)
        : base(items)
    {
        Category = category;
        Title = Texts.Category(category);
    }

    public AssetCategory Category { get; }

    public string Title { get; }
}

/// <summary>The guided recording checklist.</summary>
public sealed class RecordingsViewModel : ObservableObject, IDisposable
{
    private static readonly AssetCategory[] GroupOrder =
    [
        AssetCategory.Hour, AssetCategory.RoundMinute, AssetCategory.ExactMinute,
        AssetCategory.DayPeriod, AssetCategory.Feedback, AssetCategory.FullPhrase,
    ];

    private readonly AppInitializer _initializer;
    private readonly IVoiceAssetRepository _repository;
    private readonly RecordingCatalog _catalog;
    private readonly ISettingsRepository _settings;
    private readonly IRecordingService _recorder;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLog _log;
    private readonly Dictionary<string, ClipItemViewModel> _items = new(StringComparer.Ordinal);
    private bool _subscribed;
    private string _message = string.Empty;
    private int _phraseHourIndex;
    private int _phraseMinute;

    public RecordingsViewModel(
        AppInitializer initializer,
        IVoiceAssetRepository repository,
        RecordingCatalog catalog,
        ISettingsRepository settings,
        IRecordingService recorder,
        IDialogService dialogs,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        IAppLog? log = null)
    {
        _initializer = initializer;
        _repository = repository;
        _catalog = catalog;
        _settings = settings;
        _recorder = recorder;
        _dialogs = dialogs;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _log = log ?? NullAppLog.Instance;

        OpenCommand = new AsyncCommand<ClipItemViewModel>(item => _navigation.OpenRecorderAsync(item.Id), HandleError);
        PlayCommand = new AsyncCommand<ClipItemViewModel>(PlayAsync, HandleError, item => item.IsReady);
        RestoreDefaultCommand = new AsyncCommand<ClipItemViewModel>(RestoreDefaultAsync, HandleError, item => item.CanRestoreDefault);
        DeleteCommand = new AsyncCommand<ClipItemViewModel>(DeleteAsync, HandleError, item => item.CanDelete);
        RecordNextMissingCommand = new AsyncCommand(RecordNextMissingAsync, HandleError);
        AddFullPhraseCommand = new AsyncCommand(
            () => _navigation.OpenRecorderAsync(AssetIds.FullPhrase(_phraseHourIndex + 1, _phraseMinute)),
            HandleError);
    }

    public ObservableCollection<ClipGroupViewModel> Groups { get; } = [];

    public AsyncCommand<ClipItemViewModel> OpenCommand { get; }

    public AsyncCommand<ClipItemViewModel> PlayCommand { get; }

    public AsyncCommand<ClipItemViewModel> RestoreDefaultCommand { get; }

    public AsyncCommand<ClipItemViewModel> DeleteCommand { get; }

    public AsyncCommand RecordNextMissingCommand { get; }

    public AsyncCommand AddFullPhraseCommand { get; }

    public int[] HourOptions { get; } = Enumerable.Range(1, 12).ToArray();

    public string[] MinuteOptions { get; } =
        Enumerable.Range(0, 60).Select(m => m.ToString("00", CultureInfo.InvariantCulture)).ToArray();

    public int PhraseHourIndex
    {
        get => _phraseHourIndex;
        set
        {
            if (SetProperty(ref _phraseHourIndex, Math.Clamp(value, 0, 11)))
            {
                OnPropertyChanged(nameof(PhrasePreviewText));
            }
        }
    }

    public int PhraseMinute
    {
        get => _phraseMinute;
        set
        {
            if (SetProperty(ref _phraseMinute, Math.Clamp(value, 0, 59)))
            {
                OnPropertyChanged(nameof(PhrasePreviewText));
            }
        }
    }

    /// <summary>The sentence a full-phrase recording for the selected time should contain.</summary>
    public string PhrasePreviewText => _catalog.FullPhrase(_phraseHourIndex + 1, _phraseMinute).Text;

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public async Task OnAppearingAsync()
    {
        try
        {
            await _initializer.EnsureInitializedAsync();
            Rebuild();
            if (!_subscribed)
            {
                _repository.Changed += OnAssetsChanged;
                _settings.Changed += OnSettingsChanged;
                _subscribed = true;
            }
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    public void OnDisappearing() => _recorder.StopPreview();

    public void Dispose()
    {
        if (_subscribed)
        {
            _repository.Changed -= OnAssetsChanged;
            _settings.Changed -= OnSettingsChanged;
            _subscribed = false;
        }
    }

    private bool IncludeDayPeriod => _settings.Current.IncludeDayPeriod;

    private void Rebuild()
    {
        _items.Clear();
        Groups.Clear();

        var entries = _catalog.Entries
            .Concat(_repository.AvailableFullPhraseIds.Select(id => _catalog.Find(id)).OfType<CatalogEntry>());

        foreach (var category in GroupOrder)
        {
            var items = entries.Where(e => e.Category == category)
                .Select(e => new ClipItemViewModel(e, _repository.GetStatus(e.Id), e.IsRequired(IncludeDayPeriod)))
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            foreach (var item in items)
            {
                _items[item.Id] = item;
            }

            Groups.Add(new ClipGroupViewModel(category, items));
        }

        RaiseCommandStates();
    }

    private void Refresh(IReadOnlyCollection<string> ids)
    {
        var fullPhrasesChanged = ids.Count == 0 || ids.Any(AssetIds.IsFullPhrase);
        if (fullPhrasesChanged && !_repository.AvailableFullPhraseIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(_items.Keys.Where(AssetIds.IsFullPhrase)))
        {
            Rebuild();
            return;
        }

        foreach (var item in ids.Count == 0 ? _items.Values : ids.Select(id => _items.GetValueOrDefault(id)).OfType<ClipItemViewModel>())
        {
            item.Update(_repository.GetStatus(item.Id), item.Entry.IsRequired(IncludeDayPeriod));
        }

        RaiseCommandStates();
    }

    private async Task PlayAsync(ClipItemViewModel item)
    {
        var outcome = await _recorder.PlaySavedAsync(item.Id);
        Message = outcome.Kind == PlaybackOutcomeKind.Busy ? Strings.Recorder_Busy : string.Empty;
    }

    private async Task RestoreDefaultAsync(ClipItemViewModel item)
    {
        if (await _dialogs.ConfirmAsync(
                Strings.Recordings_RestoreTitle,
                Texts.Format(Strings.Recordings_RestoreMessage, item.Text),
                Strings.Recordings_RestoreDefault,
                Strings.Common_Cancel))
        {
            await _repository.DeleteUserClipAsync(item.Id);
        }
    }

    private async Task DeleteAsync(ClipItemViewModel item)
    {
        if (await _dialogs.ConfirmAsync(
                Strings.Recordings_DeleteTitle,
                Texts.Format(Strings.Recordings_DeleteMessage, item.Text),
                Strings.Recordings_Delete,
                Strings.Common_Cancel))
        {
            await _repository.DeleteUserClipAsync(item.Id);
        }
    }

    private async Task RecordNextMissingAsync()
    {
        var next = _catalog.RequiredEntries(IncludeDayPeriod).FirstOrDefault(e => !_repository.IsAvailable(e.Id));
        if (next is null)
        {
            Message = Strings.Recordings_NothingMissing;
            return;
        }

        await _navigation.OpenRecorderAsync(next.Id);
    }

    private void RaiseCommandStates()
    {
        PlayCommand.RaiseCanExecuteChanged();
        RestoreDefaultCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private void OnAssetsChanged(object? sender, AssetsChangedEventArgs e) => _dispatcher.Post(() => Refresh(e.AssetIds));

    private void OnSettingsChanged(object? sender, AppSettings e) => _dispatcher.Post(() => Refresh([]));

    private void HandleError(Exception ex)
    {
        _log.Warn("Recordings action failed.", ex);
        Message = Strings.Common_Error_Message;
    }
}
