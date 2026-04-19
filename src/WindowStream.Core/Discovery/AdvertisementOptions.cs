namespace WindowStream.Core.Discovery;

public sealed record AdvertisementOptions(
    string hostname,
    int protocolMajorVersion,
    int protocolRevision);
