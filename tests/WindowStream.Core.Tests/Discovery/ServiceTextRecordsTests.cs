using System.Collections.Generic;
using WindowStream.Core.Discovery;
using Xunit;

namespace WindowStream.Core.Tests.Discovery;

public sealed class ServiceTextRecordsTests
{
    [Fact]
    public void Build_EmitsRequiredKeys()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "mtsch-desktop",
            protocolMajorVersion: 1,
            protocolRevision: 1);

        IReadOnlyList<string> records = ServiceTextRecords.Build(options);

        Assert.Contains("version=1", records);
        Assert.Contains("hostname=mtsch-desktop", records);
        Assert.Contains("protocolRev=1", records);
        Assert.Equal(3, records.Count);
    }

    [Fact]
    public void Build_RejectsEmptyHostname()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "",
            protocolMajorVersion: 1,
            protocolRevision: 1);

        Assert.Throws<System.ArgumentException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsHostnameWithEqualsSign()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "bad=name",
            protocolMajorVersion: 1,
            protocolRevision: 1);

        Assert.Throws<System.ArgumentException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNegativeVersion()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "h",
            protocolMajorVersion: -1,
            protocolRevision: 0);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNegativeRevision()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "h",
            protocolMajorVersion: 1,
            protocolRevision: -1);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNullOptions()
    {
        Assert.Throws<System.ArgumentNullException>(() => ServiceTextRecords.Build(null!));
    }
}
