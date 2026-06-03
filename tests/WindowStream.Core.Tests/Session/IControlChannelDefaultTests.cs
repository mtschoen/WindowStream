using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class IControlChannelDefaultTests
{
    /// <summary>
    /// In-memory channel that does NOT override <see cref="IControlChannel.RemoteIpAddress"/>,
    /// so accessing the property exercises the default interface implementation.
    /// </summary>
    sealed class MinimalControlChannel : IControlChannel
    {
        public DateTimeOffset LastHeartbeatReceived => default;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void NotifyHeartbeatReceived() { }
        public Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
            => Task.FromResult<ControlMessage>(new HelloMessage(1, new DisplayCapabilities(0, 0, Array.Empty<string>())));
    }

    [Fact]
    public async Task RemoteIpAddress_DefaultImplementation_IsNull()
    {
        await using IControlChannel channel = new MinimalControlChannel();
        var address = channel.RemoteIpAddress;
        Assert.Null(address);
    }
}
