using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace WindowStream.Core.Discovery;

[ExcludeFromCodeCoverage(Justification = "Thin adapter over Makaretu.Dns; covered by integration tests.")]
public sealed class MakaretuMulticastServiceHost : IMulticastServiceHost
{
    private MulticastService? multicastService;
    private ServiceDiscovery? serviceDiscovery;
    private ServiceProfile? serviceProfile;

    public Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken)
    {
        if (multicastService is not null)
        {
            throw new InvalidOperationException("Already advertising.");
        }

        // ServiceProfile expects a service type in the form "_windowstream._tcp"
        // (without the trailing ".local."). Strip if present.
        string normalizedType = serviceType;
        if (normalizedType.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
        {
            normalizedType = normalizedType[..^".local.".Length];
        }

        ServiceProfile profile = new ServiceProfile(
            instanceName: serviceInstance,
            serviceName: normalizedType,
            port: (ushort)port);

        foreach (string record in textRecords)
        {
            string[] parts = record.Split('=', 2);
            string key = parts[0];
            string value = parts.Length == 2 ? parts[1] : string.Empty;
            profile.AddProperty(key, value);
        }

        MulticastService multicast = new MulticastService();
        ServiceDiscovery discovery = new ServiceDiscovery(multicast);
        discovery.Advertise(profile);
        multicast.Start();

        multicastService = multicast;
        serviceDiscovery = discovery;
        serviceProfile = profile;
        return Task.CompletedTask;
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken)
    {
        if (serviceDiscovery is not null && serviceProfile is not null)
        {
            serviceDiscovery.Unadvertise(serviceProfile);
        }
        multicastService?.Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        serviceDiscovery?.Dispose();
        multicastService?.Dispose();
        serviceDiscovery = null;
        multicastService = null;
        serviceProfile = null;
        return ValueTask.CompletedTask;
    }
}
