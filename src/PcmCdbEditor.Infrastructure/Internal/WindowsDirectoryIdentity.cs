using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcmCdbEditor.Infrastructure.Internal;

internal static class WindowsDirectoryIdentity
{
    private const uint BackupSemantics = 0x02000000;
    private const int FileIdInfo = 18;

    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string extendedPath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
            path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using SafeFileHandle handle = CreateFileW(extendedPath, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("The save staging directory could not be opened for verification.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FileIdentity identity, (uint)Marshal.SizeOf<FileIdentity>()))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 1 or 50 or 87) // Some file systems cannot report a stable ID.
            {
                return null;
            }
            throw new IOException("The save staging directory could not be identified.", new Win32Exception(error));
        }

        return identity.IdLow == 0 && identity.IdHigh == 0 ? null :
            $"{identity.Volume:X16}:{identity.IdLow:X16}{identity.IdHigh:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint desiredAccess, FileShare sharing, IntPtr attributes, FileMode disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileIdentity identity, uint size);
}
