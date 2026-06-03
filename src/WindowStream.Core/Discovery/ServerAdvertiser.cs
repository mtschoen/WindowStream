namespace WindowStream.Core.Discovery;

public sealed class ServerAdvertiser : IAsyncDisposable
{
    public const string ServiceType = "_windowstream._tcp.local.";

    readonly IMulticastServiceHost _multicastServiceHost;
    bool _started;
    bool _disposed;

    public ServerAdvertiser(IMulticastServiceHost multicastServiceHost)
    {
        _multicastServiceHost = multicastServiceHost
            ?? throw new ArgumentNullException(nameof(multicastServiceHost));
    }

    public async Task StartAsync(
        AdvertisementOptions options,
        int controlPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (controlPort < 1 || controlPort > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(controlPort),
                controlPort,
                "controlPort must be in [1, 65535].");
        }
        if (_started)
        {
            throw new InvalidOperationException("ServerAdvertiser has already been started.");
        }

        var textRecords = ServiceTextRecords.Build(options);
        await _multicastServiceHost.StartAdvertisingAsync(
            serviceInstance: options.Hostname,
            serviceType: ServiceType,
            port: controlPort,
            textRecords: textRecords,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }
        await _multicastServiceHost.StopAdvertisingAsync(cancellationToken).ConfigureAwait(false);
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _multicastServiceHost.DisposeAsync().ConfigureAwait(false);
        }
    }
}
