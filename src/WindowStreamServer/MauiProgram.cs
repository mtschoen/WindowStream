using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using WindowStream.Core.Hosting;
using WindowStream.Core.Observability;
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

#if DEBUG
        builder.Logging.AddDebug();
#endif

        MauiApp app = builder.Build();

        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<CoordinatorLauncher>();
        Diagnostics diagnostics = new Diagnostics(logger);

        CoordinatorLauncher launcher = new CoordinatorLauncher(tcpPort: 0, diagnostics);
        ServerDashboardViewModel dashboard = new ServerDashboardViewModel(launcher);

        // Wire pipeline events → dashboard VM via Diagnostics.Subscribe.
        diagnostics.Subscribe(pipelineEvent =>
        {
            switch (pipelineEvent)
            {
                case PipelineEvent.Listening listening:
                    dashboard.ReportPorts(listening.TcpPort, listening.UdpPort);
                    break;
                case PipelineEvent.ViewerAccepted viewerAccepted:
                    dashboard.ReportConnectedViewer(viewerAccepted.Endpoint);
                    break;
                case PipelineEvent.ViewerDisconnected:
                    dashboard.ReportConnectedViewer(null);
                    break;
                case PipelineEvent.WorkerSpawned:
                    // Active stream count is tracked separately; no direct mapping needed.
                    break;
                case PipelineEvent.StreamStopped:
                    // Active stream count is tracked separately; no direct mapping needed.
                    break;
            }
        });

        builder.Services.AddSingleton<ISessionHostLauncher>(launcher);
        builder.Services.AddSingleton(dashboard);
        builder.Services.AddTransient<MainPage>();

        return app;
    }
}
