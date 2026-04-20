using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using WindowStream.Server.Pages;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<WindowPickerViewModel>();
        builder.Services.AddSingleton<SessionViewModel>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
