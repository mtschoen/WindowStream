using System;
using System.Collections.Generic;

namespace WindowStream.Core.Discovery;

public static class ServiceTextRecords
{
    public static IReadOnlyList<string> Build(AdvertisementOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.protocolMajorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.protocolMajorVersion,
                "Protocol major version must be non-negative.");
        }

        if (options.protocolRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.protocolRevision,
                "Protocol revision must be non-negative.");
        }

        if (string.IsNullOrWhiteSpace(options.hostname))
        {
            throw new ArgumentException("Hostname must not be empty.", nameof(options));
        }

        if (options.hostname.Contains('='))
        {
            throw new ArgumentException("Hostname must not contain '='.", nameof(options));
        }

        return new string[]
        {
            "version=" + options.protocolMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hostname=" + options.hostname,
            "protocolRev=" + options.protocolRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
