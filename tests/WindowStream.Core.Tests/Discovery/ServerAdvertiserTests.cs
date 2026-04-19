using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Discovery;
using Xunit;

namespace WindowStream.Core.Tests.Discovery;

public sealed class ServerAdvertiserTests
{
    private sealed class FakeMulticastServiceHost : IMulticastServiceHost
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public string? ServiceInstance { get; private set; }
        public string? ServiceType { get; private set; }
        public int? Port { get; private set; }
        public IReadOnlyList<string>? TextRecords { get; private set; }

        public Task StartAdvertisingAsync(
            string serviceInstance,
            string serviceType,
            int port,
            IReadOnlyList<string> textRecords,
            CancellationToken cancellationToken)
        {
            StartCount++;
            ServiceInstance = serviceInstance;
            ServiceType = serviceType;
            Port = port;
            TextRecords = textRecords;
            return Task.CompletedTask;
        }

        public Task StopAdvertisingAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_PublishesExpectedServiceTypeAndText()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        AdvertisementOptions options = new AdvertisementOptions("desk", 1, 1);

        await using ServerAdvertiser advertiser = new ServerAdvertiser(host);

        await advertiser.StartAsync(options, controlPort: 47813, CancellationToken.None);

        Assert.Equal(1, host.StartCount);
        Assert.Equal("_windowstream._tcp.local.", host.ServiceType);
        Assert.Equal("desk", host.ServiceInstance);
        Assert.Equal(47813, host.Port);
        Assert.NotNull(host.TextRecords);
        Assert.Contains("version=1", host.TextRecords!);
        Assert.Contains("hostname=desk", host.TextRecords!);
        Assert.Contains("protocolRev=1", host.TextRecords!);
    }

    [Fact]
    public async Task StartAsync_Twice_ThrowsInvalidOperation()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using ServerAdvertiser advertiser = new ServerAdvertiser(host);

        await advertiser.StartAsync(
            new AdvertisementOptions("desk", 1, 1),
            controlPort: 1,
            CancellationToken.None);

        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            advertiser.StartAsync(
                new AdvertisementOptions("desk", 1, 1),
                controlPort: 1,
                CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_BeforeStart_IsNoOp()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using ServerAdvertiser advertiser = new ServerAdvertiser(host);

        await advertiser.StopAsync(CancellationToken.None);

        Assert.Equal(0, host.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_StopsIfStarted()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        ServerAdvertiser advertiser = new ServerAdvertiser(host);
        await advertiser.StartAsync(
            new AdvertisementOptions("desk", 1, 1),
            controlPort: 1,
            CancellationToken.None);

        await advertiser.DisposeAsync();

        Assert.Equal(1, host.StopCount);
    }

    [Fact]
    public async Task StartAsync_PortBelowOne_Throws()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using ServerAdvertiser advertiser = new ServerAdvertiser(host);

        await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() =>
            advertiser.StartAsync(
                new AdvertisementOptions("desk", 1, 1),
                controlPort: 0,
                CancellationToken.None));
    }

    [Fact]
    public void Constructor_RejectsNullHost()
    {
        Assert.Throws<System.ArgumentNullException>(() => new ServerAdvertiser(null!));
    }

    [Fact]
    public async Task StartAsync_RejectsNullOptions()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using ServerAdvertiser advertiser = new ServerAdvertiser(host);

        await Assert.ThrowsAsync<System.ArgumentNullException>(() =>
            advertiser.StartAsync(null!, controlPort: 1, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_WhenNotStarted_DoesNotCallStop()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        ServerAdvertiser advertiser = new ServerAdvertiser(host);

        await advertiser.DisposeAsync();

        Assert.Equal(0, host.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsIdempotent()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        ServerAdvertiser advertiser = new ServerAdvertiser(host);
        await advertiser.StartAsync(new AdvertisementOptions("desk", 1, 1), controlPort: 1, CancellationToken.None);

        await advertiser.DisposeAsync();
        await advertiser.DisposeAsync();

        Assert.Equal(1, host.StopCount);
    }
}
