// Windows functions Pinboard calls.
using System.Runtime.InteropServices;

static class N
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public delegate void WinEvent(IntPtr hk, uint ev, IntPtr h, int o, int c, uint th, uint t);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Blend { public byte op, flags, alpha, format; }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITaskbarList { void HrInit(); void AddTab(IntPtr h); void DeleteTab(IntPtr h); void ActivateTab(IntPtr h); void SetActiveAlt(IntPtr h); }
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] public class TaskbarList;

    // COM interfaces: methods in Windows' vtable order. ILLink.Descriptors.xml keeps every method in the trimmed build;
    // a trimmed unused method shifts the ones after it and calls land on the wrong method.
    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    // Shortcut (.lnk) file; saved and loaded through IPersistFile.
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] public class ShellLink;
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder f, int n, IntPtr fd, uint flags);
        void GetIDList(out IntPtr p); void SetIDList(IntPtr p);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder s, int n);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder s, int n);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder s, int n);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetHotkey(out short k); void SetHotkey(short k);
        void GetShowCmd(out int c); void SetShowCmd(int c);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder s, int n, out int i);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string s, int i);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string s, uint r);
        void Resolve(IntPtr h, uint f);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
    }

    public static string Title(IntPtr h)
    {
        var s = new System.Text.StringBuilder(256);
        GetWindowText(h, s, 256);
        return s.ToString();
    }

    const string U = "user32.dll", G = "gdi32.dll", D = "dwmapi.dll";
    [DllImport(U, CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport(U)] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport(U)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport(U)] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport(U)] public static extern bool IsWindow(IntPtr h);
    [DllImport(U)] public static extern bool IsIconic(IntPtr h);
    [DllImport(U)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport(U)] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport(U)] public static extern long GetWindowLongPtr(IntPtr h, int i);
    [DllImport(U)] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int c, uint f);
    [DllImport(U)] public static extern IntPtr BeginDeferWindowPos(int n);
    [DllImport(U)] public static extern IntPtr DeferWindowPos(IntPtr d, IntPtr h, IntPtr a, int x, int y, int w, int c, uint f);
    [DllImport(U)] public static extern bool EndDeferWindowPos(IntPtr d);
    [DllImport(U)] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport(U)] public static extern IntPtr GetForegroundWindow();
    [DllImport(U)] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport(U)] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport(U)] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
    [DllImport(U)] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport(U)] public static extern IntPtr SetWinEventHook(uint a, uint b, IntPtr m, WinEvent f, uint p, uint t, uint fl);
    [DllImport(U)] public static extern bool UnhookWinEvent(IntPtr h);
    [DllImport(U)] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport(U)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport(U)] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport(U)] public static extern IntPtr GetDC(IntPtr h);
    [DllImport(U)] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport(U)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point at, ref Size sz, IntPtr src, ref Point srcAt, int key, ref Blend b, int flags);
    [DllImport(G)] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport(G)] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport(G)] public static extern bool DeleteObject(IntPtr o);
    [DllImport(G)] public static extern bool DeleteDC(IntPtr dc);
    [DllImport(D)] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int size);
    [DllImport(D)] public static extern int DwmSetIconicThumbnail(IntPtr h, IntPtr bmp, uint f);
    [DllImport(D)] public static extern int DwmInvalidateIconicBitmaps(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);
    [DllImport("kernel32.dll")] public static extern bool SetDefaultDllDirectories(uint f);
    [DllImport(U)] public static extern bool IsZoomed(IntPtr h);
    [DllImport(U)] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport(U)] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
    [DllImport(G)] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport(U)] public static extern bool ReleaseCapture();
    [DllImport(U)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
