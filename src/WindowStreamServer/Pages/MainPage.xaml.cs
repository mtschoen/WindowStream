using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Maui.Controls;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server.Pages;

#pragma warning disable CA1001 // CA1001: MAUI ContentPage; servingCancellation is disposed in OnDisappearing (page lifecycle), not via IDisposable
public partial class MainPage : ContentPage
{
    private readonly ServerDashboardViewModel dashboardViewModel;
    private CancellationTokenSource? servingCancellation;

    public MainPage(ServerDashboardViewModel dashboardViewModel)
    {
        InitializeComponent();
        this.dashboardViewModel = dashboardViewModel;
        BindingContext = dashboardViewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (servingCancellation is not null)
        {
            return;
        }

        servingCancellation = new CancellationTokenSource();
        await dashboardViewModel.StartServingAsync(servingCancellation.Token);
    }

    protected override void OnDisappearing()
    {
        servingCancellation?.Cancel();
        servingCancellation?.Dispose();
        servingCancellation = null;
        base.OnDisappearing();
    }

    private void OnOpenLogFolderClicked(object? sender, EventArgs e)
    {
        string logsPath = Path.Combine(
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
