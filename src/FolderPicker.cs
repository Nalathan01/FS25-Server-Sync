using System.Collections.Generic;
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class FolderPicker
{
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCom { }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

    private static IntPtr FindDialog()
    {
        uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != me || !IsWindowVisible(h)) return true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != "#32770") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static void ForceFront(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == h) return;
        uint pid;
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        ShowWindow(h, 5);
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0003);
        SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0003);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    private static List<IntPtr> ExplorerWindows(string title)
    {
        List<IntPtr> result = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != "CabinetWClass") return true;
            if (title != null)
            {
                System.Text.StringBuilder tb = new System.Text.StringBuilder(512);
                GetWindowText(h, tb, tb.Capacity);
                string t = tb.ToString();
                if (!string.Equals(t, title, StringComparison.OrdinalIgnoreCase) && t.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) return true;
            }
            result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void OpenFolderInFront(string dir)
    {
        HashSet<IntPtr> before = new HashSet<IntPtr>(ExplorerWindows(null));
        System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
        string name = Path.GetFileName(dir.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = dir;
        System.Threading.Thread t = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                System.Threading.Thread.Sleep(100);
                IntPtr target = IntPtr.Zero;
                foreach (IntPtr h in ExplorerWindows(null)) if (!before.Contains(h)) { target = h; break; }
                if (target == IntPtr.Zero && i >= 15)
                {
                    List<IntPtr> named = ExplorerWindows(name);
                    if (named.Count > 0) target = named[0];
                }
                if (target == IntPtr.Zero) continue;
                ForceFront(target);
                if (GetForegroundWindow() == target) return;
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void WatchForDialog()
    {
        System.Threading.Thread t = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(100);
                IntPtr h = FindDialog();
                if (h == IntPtr.Zero) continue;
                ForceFront(h);
                if (GetForegroundWindow() == h) return;
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint SigdnFileSysPath = 0x80058000;

    public static string Pick(string title, string start)
    {
        using (Form owner = new Form())
        {
            owner.TopMost = true;
            owner.ShowInTaskbar = false;
            owner.FormBorderStyle = FormBorderStyle.None;
            owner.StartPosition = FormStartPosition.Manual;
            owner.Size = new Size(1, 1);
            owner.Location = Cursor.Position;
            owner.Opacity = 0;
            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            owner.Show();
            SetForegroundWindow(owner.Handle);
            BringWindowToTop(owner.Handle);
            owner.Activate();
            if (attached) AttachThreadInput(me, fgThread, false);

            IFileDialog dlg = (IFileDialog)new FileOpenDialogCom();
            try
            {
                uint options;
                dlg.GetOptions(out options);
                dlg.SetOptions(options | FosPickFolders | FosForceFileSystem);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                string folder = start;
                while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
                if (!string.IsNullOrEmpty(folder))
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        IShellItem item;
                        SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out item);
                        dlg.SetFolder(item);
                    }
                    catch (Exception) { }
                }
                WatchForDialog();
                if (dlg.Show(owner.Handle) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(SigdnFileSysPath, out path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dlg);
            }
        }
    }
}
