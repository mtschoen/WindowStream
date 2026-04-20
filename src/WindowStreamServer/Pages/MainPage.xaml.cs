using Microsoft.Maui.Controls;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server.Pages;

public partial class MainPage : ContentPage
{
    public SessionViewModel SessionViewModel { get; }

    public MainPage(WindowPickerViewModel pickerViewModel, SessionViewModel sessionViewModel)
    {
        InitializeComponent();
        BindingContext = pickerViewModel;
        SessionViewModel = sessionViewModel;
    }

    private void OnStopClicked(object? sender, System.EventArgs eventArguments)
    {
        SessionViewModel.ReportStatus(SessionStatus.Idle);
    }
}
