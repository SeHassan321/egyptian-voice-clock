using VoiceClock.Core.ViewModels;

namespace VoiceClock.App.Pages;

public partial class RecordingsPage : ContentPage
{
    private readonly RecordingsViewModel _viewModel;

    public RecordingsPage(RecordingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnDisappearing();
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
