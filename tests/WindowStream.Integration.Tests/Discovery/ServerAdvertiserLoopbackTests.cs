using System;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using WindowStream.Core.Discovery;
using Xunit;

namespace WindowStream.Integration.Tests.Discovery;

public sealed class ServerAdvertiserLoopbackTests
{
    [Fact(Timeout = 10000, Skip = "Windows does not reflect mDNS multicast to the sending socket; runs locally on Linux/macOS. Re-enable once the host is wired to an actual mDNS responder.")]
    public async Task Advertised_Service_Is_Visible_To_Local_ServiceDiscovery()
    {
        MakaretuMulticastServiceHost host = new MakaretuMulticastServiceHost();
        await using ServerAdvertiser advertiser = new ServerAdvertiser(host);

        string uniqueHostname = "wstest-" + Guid.NewGuid().ToString("N")[..8];
        AdvertisementOptions options = new AdvertisementOptions(uniqueHostname, 1, 1);

        await advertiser.StartAsync(options, controlPort: 48000, CancellationToken.None);

        TaskCompletionSource<ServiceInstanceDiscoveryEventArgs> discovered =
            new TaskCompletionSource<ServiceInstanceDiscoveryEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        using MulticastService listener = new MulticastService();
        using ServiceDiscovery discovery = new ServiceDiscovery(listener);
        discovery.ServiceInstanceDiscovered += (sender, eventArguments) =>
        {
            if (eventArguments.ServiceInstanceName.ToString()
                .StartsWith(uniqueHostname, StringComparison.OrdinalIgnoreCase))
            {
                discovered.TrySetResult(eventArguments);
            }
        };
        listener.Start();
        discovery.QueryServiceInstances(ServerAdvertiser.ServiceType);

        ServiceInstanceDiscoveryEventArgs hit = await discovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains(uniqueHostname, hit.ServiceInstanceName.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
