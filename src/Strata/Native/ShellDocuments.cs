using System.Runtime.InteropServices;
using System.Text;

namespace Strata.Native;

/// <summary>
/// Files and apps through the Windows shell's own documented interfaces:
/// - an app's recent files, from its jump list (<c>IApplicationDocumentLists</c>), read when its menu opens and never stored;
/// - opening files with a Store (packaged) app (<c>IApplicationActivationManager.ActivateForFile</c>);
/// - the AppUserModelID a shortcut carries, and the implicit one Windows gives apps that don't set their own.
/// </summary>
public static class ShellDocuments
{
    // ---------------- Recent files ----------------

    [ComImport, Guid("86bec222-30f2-47e0-9f25-60d11cd75c28")] private class ApplicationDocumentListsCom { }

    [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint itemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object list);
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
    }

    private const int RecentList = 0;            // ADLT_RECENT
    private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH

    /// <summary>
    /// Up to <paramref name="max"/> recent files of the first of <paramref name="appIds"/> that has any, newest first.
    /// Only real files that still exist. Empty in previews, and on any shell error.
    /// </summary>
    public static IReadOnlyList<string> Recent(IEnumerable<string> appIds, int max = 8)
    {
        if (Core.PrivacyGuard.PreviewActive) return [];
        foreach (var id in appIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var files = RecentFor(id, max);
            if (files.Count > 0) return files;
        }
        return [];
    }

    private static List<string> RecentFor(string appId, int max)
    {
        var files = new List<string>();
        object? lists = null, array = null;
        try
        {
            lists = new ApplicationDocumentListsCom();
            var docs = (IApplicationDocumentLists)lists;
            if (docs.SetAppID(appId) != 0) return files;
            var iid = typeof(IObjectArray).GUID;
            if (docs.GetList(RecentList, (uint)(max * 2), ref iid, out array) != 0 || array is not IObjectArray items) return files;
            if (items.GetCount(out uint count) != 0) return files;
            for (uint i = 0; i < count && files.Count < max; i++)
            {
                string? path = ItemPath(items, i);
                if (path is not null && File.Exists(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        finally
        {
            if (array is not null && Marshal.IsComObject(array)) Marshal.ReleaseComObject(array);
            if (lists is not null) Marshal.ReleaseComObject(lists);
        }
        return files;
    }

    private static string? ItemPath(IObjectArray items, uint index)
    {
        var itemIid = typeof(IShellItem).GUID;
        if (items.GetAt(index, ref itemIid, out object o) == 0 && o is IShellItem item)
        {
            try
            {
                if (item.GetDisplayName(FileSystemPath, out IntPtr p) == 0)
                {
                    try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        var linkIid = typeof(IShellLinkW).GUID;
        if (items.GetAt(index, ref linkIid, out object l) == 0 && l is IShellLinkW link)
        {
            try
            {
                var sb = new StringBuilder(1024);
                return link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) == 0 && sb.Length > 0 ? sb.ToString() : null;
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        return null;
    }

    /// <summary>
    /// Windows' "Show recently opened items in Start, Jump Lists, and File Explorer". Off, Windows keeps no recent
    /// files for any app. True when unset (Windows' default) and in previews.
    /// </summary>
    public static bool RecentTrackingOn()
    {
        if (Core.PrivacyGuard.PreviewActive) return true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("Start_TrackDocs") is not int v || v != 0;
        }
        catch (Exception) { return true; }
    }

    // ---------------- App IDs ----------------

    /// <summary>Known folders Windows uses in the implicit AppUserModelIDs of apps that don't set one:
    /// "{folder id}\relative\path.exe".</summary>
    public static IReadOnlyList<(Guid Id, string Path)> KnownFolders() =>
    [
        (new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), Environment.SystemDirectory),
        (new Guid("F38BF404-1D43-42F2-9305-67DE0B28FC23"), Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
        (new Guid("6D809377-6AF0-444B-8957-A3773F02200E"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
        (new Guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        (new Guid("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")),
        (new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        (new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
    ];

    /// <summary>The implicit AppUserModelID for <paramref name="exe"/>: the deepest known folder containing it, as its id.</summary>
    public static string? ImplicitAppId(string? exe, IReadOnlyList<(Guid Id, string Path)> folders)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        var best = folders.Where(f => f.Path.Length > 0 && exe.StartsWith(f.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Path.Length).Select(f => (f.Id, f.Path)).FirstOrDefault();
        return best.Path is null ? null : "{" + best.Id.ToString().ToUpperInvariant() + "}" + exe[best.Path.TrimEnd('\\').Length..];
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindCtx, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    /// <summary>The AppUserModelID a shortcut (.lnk) declares, if any (System.AppUserModel.ID).</summary>
    public static string? ShortcutAppId(string? lnk)
    {
        if (string.IsNullOrEmpty(lnk) || !lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnk)) return null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 0, ref iid, out var store) != 0) return null;
            try
            {
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                if (store.GetValue(ref key, out var v) != 0) return null;
                try { return v.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.Pointer) : null; }
                finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return null; }
    }

    // ---------------- Opening files with a Store app ----------------

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManagerCom { }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
    }

    // Explorer's "Open with": the handlers registered for an extension, each with its app's id.
    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler handler, out uint fetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(IntPtr dataObject);
    }

    [ComImport, Guid("36db0196-9665-46d1-9ba7-d3709eecf9ed"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithAppUserModelID
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int GetAppID([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid handler, ref Guid riid, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, int filter, out IEnumAssocHandlers handlers);

    private static readonly Guid DataObjectHandler = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4"); // BHID_DataObject
    private static readonly Guid DataObjectIid = new("0000010e-0000-0000-C000-000000000046");     // IDataObject

    /// <summary>Open the items with the "Open with" handler of app <paramref name="appId"/> for this file type, as
    /// Explorer does. Packaged desktop apps (Windows 11's Paint, Photos, Notepad) take files this way.</summary>
    private static int InvokeHandler(string appId, string extension, IntPtr itemArray)
    {
        if (string.IsNullOrEmpty(extension) || SHAssocEnumHandlers(extension, 0, out var handlers) != 0) return -1;
        try
        {
            while (handlers.Next(1, out var h, out uint n) == 0 && n == 1)
            {
                try
                {
                    if (h is not IObjectWithAppUserModelID withId || withId.GetAppID(out var id) != 0 || !string.Equals(id, appId, StringComparison.OrdinalIgnoreCase)) continue;
                    var array = (IShellItemArray)Marshal.GetObjectForIUnknown(itemArray);
                    Guid bhid = DataObjectHandler, iid = DataObjectIid;
                    int hr = array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out IntPtr data);
                    if (hr != 0) return hr;
                    try { return h.Invoke(data); } finally { Marshal.Release(data); }
                }
                finally { Marshal.ReleaseComObject(h); }
            }
            return -1;
        }
        finally { Marshal.ReleaseComObject(handlers); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IntPtr itemArray);

    /// <summary>Open <paramref name="files"/> with the packaged app <paramref name="appId"/>: by file activation (UWP
    /// apps), or through its "Open with" handler for the first file's type (packaged desktop apps).</summary>
    public static bool ActivateForFiles(string appId, IReadOnlyList<string> files)
    {
        var pidls = new List<IntPtr>();
        IntPtr array = IntPtr.Zero;
        object? manager = null;
        try
        {
            foreach (var f in files)
                if (SHParseDisplayName(f, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            int hr = pidls.Count == 0 ? -1 : SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out array);
            if (hr == 0)
            {
                manager = new ApplicationActivationManagerCom();
                hr = ((IApplicationActivationManager)manager).ActivateForFile(appId, array, null, out _);
                if (hr != 0) hr = InvokeHandler(appId, Path.GetExtension(files[0]), array);
            }
            if (hr != 0) Core.Log.Write($"Opening {files.Count} file(s) with a Store app failed: 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Core.Log.Write($"Opening {files.Count} file(s) with a Store app failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return false;
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.Release(array);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }
}
