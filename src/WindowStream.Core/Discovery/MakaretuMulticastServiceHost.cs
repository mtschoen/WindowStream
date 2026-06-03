using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

namespace WindowStream.Core.Discovery;

[ExcludeFromCodeCoverage(Justification = "Thin adapter over Makaretu.Dns; covered by integration tests.")]
public sealed class MakaretuMulticastServiceHost : IMulticastServiceHost
{
    // Substrings that, if found anywhere in NetworkInterface.Description, mean
    // the interface is a virtual / paravirtual adapter we should not advertise
    // mDNS records on. The phone/HMD on the LAN can't route to a Hyper-V
    // bridge or a WSL pseudo-interface even though the interface is "Up" with
    // a valid IPv4 address, so without this filter Makaretu's default picks
    // up Docker/WSL/etc. and the viewer resolves the wrong IP.
    static readonly string[] VirtualInterfaceDescriptionFragments = new[]
    {
        "Hyper-V",
        "Virtual",
        "VMware",
        "VirtualBox",
        "Pseudo-Interface",
        "WSL",
        "TAP-",
        "Docker",
    };

    MulticastService? _multicastService;
    ServiceDiscovery? _serviceDiscovery;
    ServiceProfile? _serviceProfile;

    public Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken)
    {
        if (_multicastService is not null)
        {
            throw new InvalidOperationException("Already advertising.");
        }

        // ServiceProfile expects a service type in the form "_windowstream._tcp"
        // (without the trailing ".local."). Strip if present.
        var normalizedType = serviceType;
        if (normalizedType.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
        {
            normalizedType = normalizedType[..^".local.".Length];
        }

        // Pass the LAN IPv4 addresses explicitly so the A records advertised
        // for the service hostname only point at interfaces remote viewers can
        // route to. Without this Makaretu auto-generates A records from every
        // local IPv4 (including Hyper-V / WSL bridges) and the picker resolves
        // to an unreachable IP.
#pragma warning disable CA1859 // CA1859: interface type kept for readability on this cold discovery path
        IReadOnlyList<IPAddress> physicalAddresses = ResolvePhysicalLanAddresses();
#pragma warning restore CA1859

        var profile = new ServiceProfile(
            instanceName: serviceInstance,
            serviceName: normalizedType,
            port: (ushort)port,
            addresses: physicalAddresses.Count > 0 ? physicalAddresses : null);

        foreach (var record in textRecords)
        {
            var parts = record.Split('=', 2);
            var key = parts[0];
            var value = parts.Length == 2 ? parts[1] : string.Empty;
            profile.AddProperty(key, value);
        }

        var multicast = new MulticastService(FilterPhysicalLanInterfaces);
        var discovery = new ServiceDiscovery(multicast);
        discovery.Advertise(profile);
        multicast.Start();

        _multicastService = multicast;
        _serviceDiscovery = discovery;
        _serviceProfile = profile;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Filters Makaretu's candidate network interfaces down to physical LAN
    /// adapters. Excludes virtual / paravirtual interfaces by description so
    /// mDNS doesn't advertise the server on a Hyper-V or WSL bridge that
    /// remote viewers can't route to.
    ///
    /// Override the heuristic with the WINDOWSTREAM_MDNS_INTERFACE env var,
    /// which (case-insensitively) matches against
    /// <see cref="NetworkInterface.Name"/> or
    /// <see cref="NetworkInterface.Description"/>.
    /// </summary>
    static IEnumerable<NetworkInterface> FilterPhysicalLanInterfaces(
        IEnumerable<NetworkInterface> candidates)
    {
        IReadOnlyList<NetworkInterface> snapshot = candidates.ToList();

        var overrideName = Environment.GetEnvironmentVariable("WINDOWSTREAM_MDNS_INTERFACE");
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            var matched = snapshot.Where(intf =>
                intf.Name.Contains(overrideName, StringComparison.OrdinalIgnoreCase) ||
                intf.Description.Contains(overrideName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count > 0)
            {
                return matched;
            }
            // Fall through to heuristic if the override didn't match anything.
        }

        var physical = snapshot.Where(IsPhysicalLanInterface).ToList();
        // If the heuristic excluded everything (unusual, e.g. all interfaces
        // happened to match a virtual-fragment), fall back to the unfiltered
        // candidates rather than break discovery entirely.
        return physical.Count > 0 ? physical : snapshot;
    }

    static List<IPAddress> ResolvePhysicalLanAddresses()
    {
        var filtered = FilterPhysicalLanInterfaces(
            NetworkInterface.GetAllNetworkInterfaces());
        return filtered
            .SelectMany(intf => intf.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .ToList();
    }

    static bool IsPhysicalLanInterface(NetworkInterface intf)
    {
        if (intf.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
        if (intf.OperationalStatus != OperationalStatus.Up) return false;

        var description = intf.Description;
        foreach (var fragment in VirtualInterfaceDescriptionFragments)
        {
            if (description.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken)
    {
        if (_serviceDiscovery is not null && _serviceProfile is not null)
        {
            _serviceDiscovery.Unadvertise(_serviceProfile);
        }
        _multicastService?.Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _serviceDiscovery?.Dispose();
        _multicastService?.Dispose();
        _serviceDiscovery = null;
        _multicastService = null;
        _serviceProfile = null;
        return ValueTask.CompletedTask;
    }
}
