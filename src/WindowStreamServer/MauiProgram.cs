using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
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

        CoordinatorLauncher launcher = new CoordinatorLauncher(tcpPort: 0, output: Console.Out);
        ServerDashboardViewModel dashboard = new ServerDashboardViewModel(launcher);

        // Wire coordinator status callbacks → dashboard VM (thread-safe; VM uses INotifyPropertyChanged).
        launcher.OnAvailableWindowCountChanged = count => dashboard.ReportAvailableWindows(count);
        launcher.OnPortsAssigned = (tcp, udp) => dashboard.ReportPorts(tcp, udp);
        launcher.OnActiveStreamCountChanged = count => dashboard.ReportActiveStreams(count);

        builder.Services.AddSingleton<ISessionHostLauncher>(launcher);
        builder.Services.AddSingleton(dashboard);
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
