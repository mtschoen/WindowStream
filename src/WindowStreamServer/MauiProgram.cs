using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Hosting;
using WindowStream.Core.Session;
using WindowStream.Server.Pages;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<IWindowCaptureSource>(_ => new WgcCaptureSource());
        builder.Services.AddSingleton<ISessionHostLauncher>(_ => new CoordinatorLauncher(tcpPort: 0, output: Console.Out));
        builder.Services.AddSingleton<WindowPickerViewModel>();
        builder.Services.AddSingleton<SessionViewModel>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
