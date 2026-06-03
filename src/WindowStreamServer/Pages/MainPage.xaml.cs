using System.Diagnostics;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server.Pages;

#pragma warning disable CA1001 // CA1001: MAUI ContentPage; servingCancellation is disposed in OnDisappearing (page lifecycle), not via IDisposable
public partial class MainPage
{
    readonly ServerDashboardViewModel _dashboardViewModel;
    CancellationTokenSource? _servingCancellation;

    public MainPage(ServerDashboardViewModel dashboardViewModel)
    {
        InitializeComponent();
        _dashboardViewModel = dashboardViewModel;
        BindingContext = dashboardViewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_servingCancellation is not null)
        {
            return;
        }

        _servingCancellation = new CancellationTokenSource();
        await _dashboardViewModel.StartServingAsync(_servingCancellation.Token);
    }

    protected override void OnDisappearing()
    {
        _servingCancellation?.Cancel();
        _servingCancellation?.Dispose();
        _servingCancellation = null;
        base.OnDisappearing();
    }

    void OnOpenLogFolderClicked(object? sender, EventArgs e)
    {
        var logsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowStream", "logs");
        Directory.CreateDirectory(logsPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = logsPath,
            UseShellExecute = true,
        });
    }
}
#pragma warning restore CA1001
