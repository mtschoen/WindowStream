using System.Globalization;

namespace WindowStream.Core.Discovery;

public static class ServiceTextRecords
{
    public static IReadOnlyList<string> Build(AdvertisementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProtocolMajorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ProtocolMajorVersion,
                "Protocol major version must be non-negative.");
        }

        if (options.ProtocolRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ProtocolRevision,
                "Protocol revision must be non-negative.");
        }

        if (string.IsNullOrWhiteSpace(options.Hostname))
        {
            throw new ArgumentException("Hostname must not be empty.", nameof(options));
        }

        if (options.Hostname.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentException("Hostname must not contain '='.", nameof(options));
        }

        return new[]
        {
            "version=" + options.ProtocolMajorVersion.ToString(CultureInfo.InvariantCulture),
            "hostname=" + options.Hostname,
            "protocolRev=" + options.ProtocolRevision.ToString(CultureInfo.InvariantCulture),
        };
    }
}
