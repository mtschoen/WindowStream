using System.Threading;
using Microsoft.Maui.Controls;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server.Pages;

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
}
