using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CampusNetTraffic.TrafficService;

internal static class DriverTrafficSource
{
    private const string DevicePath = @"\\.\CampusNetTrafficNet";
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private const uint CntDeviceType = 0x8337;
    private const uint FileReadData = 0x0001;
    private const uint MethodBuffered = 0;
    private static readonly uint IoctlGetCounters = CtlCode(CntDeviceType, 0x801, MethodBuffered, FileReadData);

    public static bool TryReadCounters(out long received, out long sent)
    {
        received = 0;
        sent = 0;

        try
        {
            using var device = CreateFile(
                DevicePath,
                GenericRead,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (device.IsInvalid)
            {
                return false;
            }

            var counters = new DriverCounters();
            var size = Marshal.SizeOf<DriverCounters>();
            var ok = DeviceIoControl(
                device,
                IoctlGetCounters,
                IntPtr.Zero,
                0,
                out counters,
                size,
                out var bytesReturned,
                IntPtr.Zero);

            if (!ok || bytesReturned < size)
            {
                return false;
            }

            received = unchecked((long)Math.Min(counters.InboundBytes, long.MaxValue));
            sent = unchecked((long)Math.Min(counters.OutboundBytes, long.MaxValue));
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
    {
        return (deviceType << 16) | (access << 14) | (function << 2) | method;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        int inBufferSize,
        out DriverCounters outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct DriverCounters
    {
        public ulong InboundBytes;
        public ulong OutboundBytes;
    }
}
