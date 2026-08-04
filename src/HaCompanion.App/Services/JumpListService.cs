// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// Taskbar jump list (right-click on the taskbar icon) for the unpackaged app.
/// <c>Windows.UI.StartScreen.JumpList</c> requires package identity, so this uses the
/// classic COM surface (<c>ICustomDestinationList</c>) that Explorer itself speaks.
/// The entries start a second instance with an argument; single-instancing redirects
/// that into the running app (see <c>Program</c> / <c>App.OnRedirected</c>).
/// Everything here is best-effort — a jump list must never break app startup.
/// </summary>
public sealed class JumpListService
{
    /// <summary>Argument that toggles the quick panel in the running instance.</summary>
    public const string QuickPanelArg = "--quick-panel";

    /// <summary>Argument that re-runs the stored-settings connect in the running instance.</summary>
    public const string ReconnectArg = "--reconnect";

    private readonly LocalizationService _loc;
    private readonly ILogger<JumpListService> _logger;

    public JumpListService(LocalizationService loc, ILogger<JumpListService> logger)
    {
        _loc = loc;
        _logger = logger;
    }

    /// <summary>Build the jump list and keep its titles in the app language.</summary>
    public void Initialize()
    {
        Rebuild();
        _loc.LanguageChanged += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        try
        {
            Build();
        }
        catch (Exception ex) // best-effort by design: never let the shell surface break startup
        {
            _logger.LogWarning(ex, "Jump list could not be built");
        }
    }

    private void Build()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("process path unavailable");

        var list = (ICustomDestinationList)new DestinationList();
        var riid = typeof(IObjectArray).GUID;
        list.BeginList(out _, ref riid, out _);

        var tasks = (IObjectCollection)new EnumerableObjectCollection();
        // Same wording as the tray menu — one vocabulary for the same two actions.
        tasks.AddObject(CreateTask(exe, QuickPanelArg, _loc["Tray_Panel"]));
        tasks.AddObject(CreateTask(exe, ReconnectArg, _loc["Tray_Reconnect"]));

        list.AddUserTasks((IObjectArray)tasks);
        list.CommitList();
    }

    private static IShellLinkW CreateTask(string exe, string arguments, string title)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetArguments(arguments);
        link.SetIconLocation(exe, 0); // the embedded ApplicationIcon

        // The visible task name lives in the link's property store (PKEY_Title).
        var store = (IPropertyStore)link;
        var titleKey = new PropertyKey(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
        var value = PropVariant.FromString(title);
        try
        {
            store.SetValue(ref titleKey, ref value);
            store.Commit();
        }
        finally
        {
            value.Clear();
        }
        return link;
    }

    // ----- COM interop (vtable order matters — do not reorder members) -----

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6"), ClassInterface(ClassInterfaceType.None)]
    private class DestinationList { }

    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a"), ClassInterface(ClassInterfaceType.None)]
    private class EnumerableObjectCollection { }

    [ComImport, Guid("00021401-0000-0000-c000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    private class ShellLink { }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid,
                       [MarshalAs(UnmanagedType.Interface)] out object removedItems);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray items);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid,
                                    [MarshalAs(UnmanagedType.Interface)] out object removedItems);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // IObjectArray (COM interface inheritance must be re-declared in vtable order)
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.Interface)] object item);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("000214f9-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] char[] file, int maxPath,
                     IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] char[] name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] char[] dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] char[] args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] char[] iconPath, int maxPath,
                             out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>Minimal PROPVARIANT: only VT_LPWSTR is ever needed here.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        private const ushort VtLpwstr = 31;

        [FieldOffset(0)] private ushort _vt;
        [FieldOffset(8)] private IntPtr _pointerValue; // x64 union offset

        public static PropVariant FromString(string value) => new()
        {
            _vt = VtLpwstr,
            _pointerValue = Marshal.StringToCoTaskMemUni(value),
        };

        public void Clear() => _ = PropVariantClear(ref this);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }
}
