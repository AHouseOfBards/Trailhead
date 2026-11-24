using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jellyfin.Plugin.Trailhead.Network;

internal enum NetworkProfile
{
    /// <summary>Not Windows, or Windows wouldn't say. A valid answer: the page then says nothing about it.</summary>
    Unknown,

    Public,
    Private,
    Domain,
}

/// <summary>
/// Which profile Windows applies to the network an interface is on (T35). The 8443 rule is for Private and Domain
/// only, so on a Public network even the LAN can't reach Edge, and the page says so rather than opening Public.
/// Read through the Network List Manager COM API, which needs no elevation and starts no program
/// (Get-NetConnectionProfile would mean running PowerShell). Any failure is Unknown.
/// </summary>
internal static class WindowsNetworkProfile
{
    // NLM_NETWORK_CATEGORY: 0 public, 1 private, 2 domain authenticated.
    internal static NetworkProfile FromCategory(int category) => category switch
    {
        0 => NetworkProfile.Public,
        1 => NetworkProfile.Private,
        2 => NetworkProfile.Domain,
        _ => NetworkProfile.Unknown,
    };

    /// <summary>The profile of the network on the adapter whose <see cref="System.Net.NetworkInformation.NetworkInterface.Id"/> is <paramref name="adapterId"/>.</summary>
    internal static NetworkProfile ForAdapter(string? adapterId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(adapterId, out var adapter))
        {
            return NetworkProfile.Unknown;
        }

        try
        {
            return Read(adapter);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidComObjectException or NotSupportedException or PlatformNotSupportedException or TypeLoadException)
        {
            return NetworkProfile.Unknown;
        }
    }

    [SupportedOSPlatform("windows")]
    private static NetworkProfile Read(Guid adapter)
    {
        var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: false);
        if (type is null || Activator.CreateInstance(type) is not INetworkListManager manager)
        {
            return NetworkProfile.Unknown;
        }

        IEnumNetworkConnections? connections = null;
        try
        {
            connections = manager.GetNetworkConnections();
            var one = new INetworkConnection?[1];
            for (var guard = 0; guard < 256; guard++)
            {
                if (connections.Next(1, one, out var fetched) != 0 || fetched == 0 || one[0] is not { } connection)
                {
                    break;
                }

                try
                {
                    if (connection.GetAdapterId() != adapter)
                    {
                        continue;
                    }

                    var network = connection.GetNetwork();
                    try
                    {
                        return FromCategory(network.GetCategory());
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(network);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(connection);
                    one[0] = null;
                }
            }

            return NetworkProfile.Unknown;
        }
        finally
        {
            if (connections is not null)
            {
                Marshal.ReleaseComObject(connections);
            }

            Marshal.ReleaseComObject(manager);
        }
    }

    // netlistmgr.idl. Dual interfaces: the methods follow IDispatch's in vtable order, so the order below is load-bearing
    // and every method up to the last one used is declared, used or not.
    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(int flags);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetwork(Guid networkId);

        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport]
    [Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNewEnum();

        [PreserveSig]
        int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection?[] items, out int fetched);
    }

    [ComImport]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();

        bool IsConnectedToInternet();

        bool IsConnected();

        int GetConnectivity();

        Guid GetConnectionId();

        Guid GetAdapterId();
    }

    [ComImport]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

        Guid GetNetworkId();

        int GetDomainType();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();

        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);

        bool IsConnectedToInternet();

        bool IsConnected();

        int GetConnectivity();

        int GetCategory();
    }
}
