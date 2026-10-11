using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.ViewModels;

namespace VoiceClock.App.Pages;

/// <summary>Receives the clip id through Shell navigation: <c>recorder?id=hour.01</c>.</summary>
public partial class RecordClipPage : ContentPage, IQueryAttributable
{
    private readonly RecordClipViewModel _viewModel;
    private readonly IAppLog _log;

    public RecordClipPage(RecordClipViewModel viewModel, IAppLog log)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _log = log;
    }

    public async void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("id", out var value) || value is not string assetId)
        {
            return;
        }

        try
        {
            await _viewModel.LoadAsync(assetId);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open recorder for '{assetId}'.", ex);
            await DisplayAlertAsync(
                VoiceClock.Core.Resources.Strings.Common_Error_Title,
                VoiceClock.Core.Resources.Strings.Recorder_OpenFailed + $"\n\n({ex.GetType().Name}: {ex.Message})",
                VoiceClock.Core.Resources.Strings.Common_Ok);
            await Shell.Current.GoToAsync("..");
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        try
        {
            await _viewModel.OnDisappearingAsync();
        }
        catch (Exception ex)
        {
            _log.Warn("Stopping the recorder failed.", ex);
        }
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        if (args.NavigationType is NavigationType.Pop or NavigationType.PopToRoot)
        {
            _viewModel.Dispose();
        }
    }
}
