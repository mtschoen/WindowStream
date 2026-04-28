using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly string[] VirtualInterfaceDescriptionFragments = new[]
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

    private MulticastService? multicastService;
    private ServiceDiscovery? serviceDiscovery;
    private ServiceProfile? serviceProfile;

    public Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken)
    {
        if (multicastService is not null)
        {
            throw new InvalidOperationException("Already advertising.");
        }

        // ServiceProfile expects a service type in the form "_windowstream._tcp"
        // (without the trailing ".local."). Strip if present.
        string normalizedType = serviceType;
        if (normalizedType.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
        {
            normalizedType = normalizedType[..^".local.".Length];
        }

        ServiceProfile profile = new ServiceProfile(
            instanceName: serviceInstance,
            serviceName: normalizedType,
            port: (ushort)port);

        foreach (string record in textRecords)
        {
            string[] parts = record.Split('=', 2);
            string key = parts[0];
            string value = parts.Length == 2 ? parts[1] : string.Empty;
            profile.AddProperty(key, value);
        }

        MulticastService multicast = new MulticastService(FilterPhysicalLanInterfaces);
        ServiceDiscovery discovery = new ServiceDiscovery(multicast);
        discovery.Advertise(profile);
        multicast.Start();

        multicastService = multicast;
        serviceDiscovery = discovery;
        serviceProfile = profile;
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
    private static IEnumerable<NetworkInterface> FilterPhysicalLanInterfaces(
        IEnumerable<NetworkInterface> candidates)
    {
        IReadOnlyList<NetworkInterface> snapshot = candidates.ToList();

        string? overrideName = Environment.GetEnvironmentVariable("WINDOWSTREAM_MDNS_INTERFACE");
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            IReadOnlyList<NetworkInterface> matched = snapshot.Where(intf =>
                intf.Name.Contains(overrideName!, StringComparison.OrdinalIgnoreCase) ||
                intf.Description.Contains(overrideName!, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count > 0)
            {
                return matched;
            }
            // Fall through to heuristic if the override didn't match anything.
        }

        IReadOnlyList<NetworkInterface> physical = snapshot.Where(IsPhysicalLanInterface).ToList();
        // If the heuristic excluded everything (unusual, e.g. all interfaces
        // happened to match a virtual-fragment), fall back to the unfiltered
        // candidates rather than break discovery entirely.
        return physical.Count > 0 ? physical : snapshot;
    }

    private static bool IsPhysicalLanInterface(NetworkInterface intf)
    {
        if (intf.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
        if (intf.OperationalStatus != OperationalStatus.Up) return false;

        string description = intf.Description ?? string.Empty;
        foreach (string fragment in VirtualInterfaceDescriptionFragments)
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
        if (serviceDiscovery is not null && serviceProfile is not null)
        {
            serviceDiscovery.Unadvertise(serviceProfile);
        }
        multicastService?.Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        serviceDiscovery?.Dispose();
        multicastService?.Dispose();
        serviceDiscovery = null;
        multicastService = null;
        serviceProfile = null;
        return ValueTask.CompletedTask;
    }
}
