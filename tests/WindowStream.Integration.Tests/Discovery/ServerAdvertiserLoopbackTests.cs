using Makaretu.Dns;
using WindowStream.Core.Discovery;
using Xunit;

namespace WindowStream.Integration.Tests.Discovery;

public sealed class ServerAdvertiserLoopbackTests
{
    [Fact(Timeout = 10000, Skip = "Windows does not reflect mDNS multicast to the sending socket; runs locally on Linux/macOS. Re-enable once the host is wired to an actual mDNS responder.")]
    public async Task Advertised_Service_Is_Visible_To_Local_ServiceDiscovery()
    {
        await using var host = new MakaretuMulticastServiceHost();
        await using var advertiser = new ServerAdvertiser(host);

        var uniqueHostname = "wstest-" + Guid.NewGuid().ToString("N")[..8];
        var options = new AdvertisementOptions(uniqueHostname, 1, 1);

        await advertiser.StartAsync(options, controlPort: 48000, CancellationToken.None);

        var discovered =
            new TaskCompletionSource<ServiceInstanceDiscoveryEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MulticastService();
        using var discovery = new ServiceDiscovery(listener);
        discovery.ServiceInstanceDiscovered += (_, eventArguments) =>
        {
            if (eventArguments.ServiceInstanceName.ToString()
                .StartsWith(uniqueHostname, StringComparison.OrdinalIgnoreCase))
            {
                discovered.TrySetResult(eventArguments);
            }
        };
        listener.Start();
        discovery.QueryServiceInstances(ServerAdvertiser.ServiceType);

        var hit = await discovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains(uniqueHostname, hit.ServiceInstanceName.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
