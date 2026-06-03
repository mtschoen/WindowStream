namespace WindowStream.Core.Discovery;

public sealed record AdvertisementOptions(
    string Hostname,
    int ProtocolMajorVersion,
    int ProtocolRevision);
