using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class NativeLinks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoByHandle
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoByHandle info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CopyFileExW(string existingFileName, string newFileName, IntPtr progress, IntPtr data, IntPtr cancel, uint flags);

    private const uint CopyFailIfExists = 0x1;
    private const uint CopyAllowDecryptedDestination = 0x8;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    private static bool Info(string path, out FileInfoByHandle info)
    {
        info = new FileInfoByHandle();
        using (SafeFileHandle h = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero))
        {
            if (h.IsInvalid) return false;
            return GetFileInformationByHandle(h, out info);
        }
    }

    public static int LinkCount(string path)
    {
        FileInfoByHandle info;
        return Info(path, out info) ? (int)info.NumberOfLinks : -1;
    }

    public static bool SameFile(string a, string b)
    {
        FileInfoByHandle ia, ib;
        if (!Info(a, out ia) || !Info(b, out ib)) return false;
        return ia.VolumeSerialNumber == ib.VolumeSerialNumber && ia.FileIndexHigh == ib.FileIndexHigh && ia.FileIndexLow == ib.FileIndexLow;
    }

    public static void CreateHardLink(string newPath, string existingPath)
    {
        if (!CreateHardLinkW(newPath, existingPath, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void CopyFile(string source, string dest)
    {
        if (!CopyFileExW(source, dest, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CopyFailIfExists | CopyAllowDecryptedDestination)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static string VolumeOf(string path)
    {
        string probe = path;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe) && !File.Exists(probe)) probe = Path.GetDirectoryName(probe);
        if (string.IsNullOrEmpty(probe)) probe = Path.GetPathRoot(Path.GetFullPath(path));
        StringBuilder sb = new StringBuilder(1024);
        if (!GetVolumePathNameW(probe, sb, sb.Capacity)) return Path.GetPathRoot(Path.GetFullPath(path));
        return sb.ToString().TrimEnd('\\').ToUpperInvariant();
    }

    public static bool SameVolume(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return VolumeOf(a) == VolumeOf(b);
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
        catch (Exception) { return false; }
    }
}
