using WindowStream.Core.Discovery;
using Xunit;

namespace WindowStream.Core.Tests.Discovery;

public sealed class ServiceTextRecordsTests
{
    [Fact]
    public void Build_EmitsRequiredKeys()
    {
        var options = new AdvertisementOptions(
            Hostname: "mtsch-desktop",
            ProtocolMajorVersion: 1,
            ProtocolRevision: 1);

        var records = ServiceTextRecords.Build(options);

        Assert.Contains("version=1", records);
        Assert.Contains("hostname=mtsch-desktop", records);
        Assert.Contains("protocolRev=1", records);
        Assert.Equal(3, records.Count);
    }

    [Fact]
    public void Build_RejectsEmptyHostname()
    {
        var options = new AdvertisementOptions(
            Hostname: "",
            ProtocolMajorVersion: 1,
            ProtocolRevision: 1);

        Assert.Throws<ArgumentException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsHostnameWithEqualsSign()
    {
        var options = new AdvertisementOptions(
            Hostname: "bad=name",
            ProtocolMajorVersion: 1,
            ProtocolRevision: 1);

        Assert.Throws<ArgumentException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNegativeVersion()
    {
        var options = new AdvertisementOptions(
            Hostname: "h",
            ProtocolMajorVersion: -1,
            ProtocolRevision: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNegativeRevision()
    {
        var options = new AdvertisementOptions(
            Hostname: "h",
            ProtocolMajorVersion: 1,
            ProtocolRevision: -1);

        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceTextRecords.Build(null!));
    }
}
