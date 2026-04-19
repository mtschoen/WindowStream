using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Discovery;

public sealed class ServerAdvertiser : IAsyncDisposable
{
    public const string ServiceType = "_windowstream._tcp.local.";

    private readonly IMulticastServiceHost multicastServiceHost;
    private bool started;
    private bool disposed;

    public ServerAdvertiser(IMulticastServiceHost multicastServiceHost)
    {
        this.multicastServiceHost = multicastServiceHost
            ?? throw new ArgumentNullException(nameof(multicastServiceHost));
    }

    public async Task StartAsync(
        AdvertisementOptions options,
        int controlPort,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (controlPort < 1 || controlPort > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(controlPort),
                controlPort,
                "controlPort must be in [1, 65535].");
        }
        if (started)
        {
            throw new InvalidOperationException("ServerAdvertiser has already been started.");
        }

        IReadOnlyList<string> textRecords = ServiceTextRecords.Build(options);
        await multicastServiceHost.StartAdvertisingAsync(
            serviceInstance: options.hostname,
            serviceType: ServiceType,
            port: controlPort,
            textRecords: textRecords,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!started)
        {
            return;
        }
        await multicastServiceHost.StopAdvertisingAsync(cancellationToken).ConfigureAwait(false);
        started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await multicastServiceHost.DisposeAsync().ConfigureAwait(false);
        }
    }
}
