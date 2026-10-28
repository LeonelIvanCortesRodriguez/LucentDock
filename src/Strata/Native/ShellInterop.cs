using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Strata.Native;

/// <summary>Shell helpers: high-resolution icons and AppUserModelIDs.</summary>
internal static class ShellInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04,
        ScaleUp = 0x100,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    /// <summary>Icon for a file path or shell parsing name (e.g. shell:AppsFolder\AUMID).</summary>
    public static ImageSource? GetShellImage(string parsingName, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;
        try
        {
            var sz = new SIZE { cx = size, cy = size };
            if (factory.GetImage(sz, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out IntPtr hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return HBitmapToSource(hbm); }
            finally { Win32.DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    private static BitmapSource? HBitmapToSource(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
        int w = bmp.bmWidth, h = bmp.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, bits, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        // A few legacy shell bitmaps have no alpha. Infer coverage from their color while preserving
        // transparent black corners; never turn every zero-alpha pixel into an opaque rectangle.
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        NormalizePremultipliedAlpha(bits, anyAlpha);

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
        src.Freeze();
        return src;
    }

    public static void NormalizePremultipliedAlpha(byte[] bgra, bool hasAlpha)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (!hasAlpha) a = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            bgra[i + 3] = a;
            bgra[i] = Math.Min(bgra[i], a);
            bgra[i + 1] = Math.Min(bgra[i + 1], a);
            bgra[i + 2] = Math.Min(bgra[i + 2], a);
        }
    }

    public static ImageSource? HIconToSource(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    // ---- AppUserModelID of a window ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(16)] public IntPtr p2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hWnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    private const ushort VT_LPWSTR = 31;
    private static readonly Guid AppUserModelFmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static string? GetWindowAppUserModelId(IntPtr hWnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hWnd, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = AppUserModelFmtid, pid = 5 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try
            {
                return pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null;
            }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>Resolve a .lnk shortcut's target and arguments via the Windows Script Host COM object.</summary>
    public static (string Target, string Arguments)? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(lnkPath);
                string target = link.TargetPath ?? "";
                string args = link.Arguments ?? "";
                Marshal.FinalReleaseComObject(link);
                return (target, args);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCom { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription(StringBuilder text, int max);
        void SetDescription(string text);
        void GetWorkingDirectory(StringBuilder text, int max);
        void SetWorkingDirectory(string text);
        void GetArguments(StringBuilder text, int max);
        void SetArguments(string text);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation(StringBuilder text, int max, out int index);
        void SetIconLocation(string text, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath(string path);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr itemIdList, uint displayName, out IntPtr name);

    /// <summary>The canonical shell parsing target of a shortcut PIDL. No folder path, title or history is read.</summary>
    public static string? GetShortcutShellTarget(string lnkPath)
    {
        object? link = null;
        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            link = new ShellLinkCom();
            ((IPersistFile)link).Load(lnkPath, 0);
            ((IShellLinkW)link).GetIDList(out itemIdList);
            if (itemIdList == IntPtr.Zero || SHGetNameFromIDList(itemIdList, 0x80028000, out IntPtr name) != 0) return null;
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException) { return null; }
        finally
        {
            if (itemIdList != IntPtr.Zero) Marshal.FreeCoTaskMem(itemIdList);
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
        }
    }
}
