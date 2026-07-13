using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using WindowStream.Core.Hosting;
using WindowStream.Core.Observability;
using WindowStream.Core.Session;
using WindowStream.Server.Observability;
using WindowStream.Server.Pages;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        InAppDashboardSink inAppSink = new(capacity: 500);

        var serilogLogger = WindowStreamFileLogging.CreateConfiguration()
            .WriteTo.Sink(inAppSink)
            .CreateLogger();

#pragma warning disable CA2000 // CA2000: SerilogLoggerFactory(dispose:true) owns the logger lifetime; the MAUI DI container controls disposal
        SerilogLoggerFactory loggerFactory = new(serilogLogger, dispose: true);
#pragma warning restore CA2000
        var launcherLogger = loggerFactory.CreateLogger<CoordinatorLauncher>();

        Diagnostics diagnostics = new(launcherLogger);
        CoordinatorLauncher launcher = new(tcpPort: 0, diagnostics: diagnostics);
        ServerDashboardViewModel dashboard = new(launcher, inAppSink);

        diagnostics.Subscribe(dashboard.ApplyEvent);

        builder.Services.AddSingleton<ISessionHostLauncher>(launcher);
        builder.Services.AddSingleton(dashboard);
        builder.Services.AddSingleton(inAppSink);
        builder.Services.AddSingleton(diagnostics);
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
