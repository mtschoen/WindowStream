using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
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
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        InAppDashboardSink inAppSink = new(capacity: 500);

        string logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowStream", "logs");
        Directory.CreateDirectory(logsDirectory);

        Logger serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logsDirectory, "server-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: false)
            .WriteTo.Sink(inAppSink)
            .CreateLogger();

        SerilogLoggerFactory loggerFactory = new(serilogLogger, dispose: true);
        ILogger<CoordinatorLauncher> launcherLogger = loggerFactory.CreateLogger<CoordinatorLauncher>();

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
