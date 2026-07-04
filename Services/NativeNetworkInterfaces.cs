using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CampusNetTraffic.Services;

internal static class NativeNetworkInterfaces
{
    private const int IfMaxStringSize = 256;
    private const int IfMaxPhysAddressLength = 32;
    private const int NoError = 0;

    public static IReadOnlyList<InterfaceRow> GetRows()
    {
        var result = GetIfTable2(out var table);
        if (result != NoError)
        {
            throw new Win32Exception(unchecked((int)result));
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var rowPtr = IntPtr.Add(table, IntPtr.Size);
            var rowSize = Marshal.SizeOf<MibIfRow2>();
            var rows = new List<InterfaceRow>(count);

            for (var i = 0; i < count; i++)
            {
                var nativeRow = Marshal.PtrToStructure<MibIfRow2>(IntPtr.Add(rowPtr, i * rowSize));
                rows.Add(new InterfaceRow(
                    nativeRow.InterfaceGuid,
                    TrimNull(nativeRow.Alias),
                    (IfType)nativeRow.Type,
                    (IfOperStatus)nativeRow.OperStatus,
                    (nativeRow.InterfaceAndOperStatusFlags & (byte)MibIfOperStatusFlags.FilterInterface) != 0,
                    nativeRow.InOctets,
                    nativeRow.OutOctets));
            }

            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static string TrimNull(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var nullIndex = value.IndexOf('\0');
        return nullIndex >= 0 ? value[..nullIndex] : value;
    }

    [DllImport("Iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("Iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    public sealed record InterfaceRow(
        Guid InterfaceGuid,
        string Name,
        IfType Type,
        IfOperStatus OperStatus,
        bool IsFilterInterface,
        ulong InOctets,
        ulong OutOctets);

    public enum IfOperStatus : uint
    {
        Up = 1
    }

    public enum IfType : uint
    {
        EthernetCsmacd = 6,
        SoftwareLoopback = 24,
        Ppp = 23,
        Tunnel = 131,
        Ieee80211 = 71
    }

    [Flags]
    private enum MibIfOperStatusFlags : byte
    {
        FilterInterface = 0x02
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = IfMaxStringSize + 1)]
        public string Alias;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = IfMaxStringSize + 1)]
        public string Description;

        public uint PhysicalAddressLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IfMaxPhysAddressLength)]
        public byte[] PhysicalAddress;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IfMaxPhysAddressLength)]
        public byte[] PermanentPhysicalAddress;

        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }
}
