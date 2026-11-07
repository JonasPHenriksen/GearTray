using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GearTray.Util;

/// <summary>
/// Opens a file or folder like a double-click in Explorer, but never with GearTray's admin rights. An elevated
/// ShellExecute picks the program for a file type from per-user registry keys that every program of the user can
/// change (HKCU\Software\Classes) and would start that program with admin rights. So an elevated GearTray asks the
/// desktop's Explorer, which runs with the user's normal rights, to open it: the way Raymond Chen describes in "How
/// can I launch an unelevated process from my elevated process and vice versa?" (The Old New Thing, 2013).
/// </summary>
internal static class ShellOpen
{
    private const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_BACKGROUND = 0, SW_SHOWNORMAL = 1;
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>Opens it with the user's normal rights. Throws when that isn't possible.</summary>
    public static void Open(string path)
    {
        if (Elevation.IsAdmin) ThroughExplorer(path);
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>ShellExecute inside the desktop's Explorer process. Throws when there's no desktop Explorer to ask.</summary>
    public static void ThroughExplorer(string path)
    {
        var windows = (IShellWindows)new ShellWindows();
        try
        {
            object location = CSIDL_DESKTOP, root = null!;
            object? desktop = windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            if (desktop is not IServiceProvider services) throw new InvalidOperationException("Explorer's desktop isn't running");
            Guid service = SID_STopLevelBrowser, browserId = typeof(IShellBrowser).GUID, dispatchId = IID_IDispatch;
            var browser = (IShellBrowser)services.QueryService(ref service, ref browserId);
            dynamic folderView = browser.QueryActiveShellView().GetItemObject(SVGIO_BACKGROUND, ref dispatchId);
            dynamic shell = folderView.Application; // IShellDispatch2, in Explorer's process
            shell.ShellExecute(path, "", "", "", SW_SHOWNORMAL);
        }
        finally { Marshal.FinalReleaseComObject(windows); }
    }

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindows { }

    // Only the methods called here are declared with their real signatures; the others hold their vtable slots.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void _Count(); void _Item(); void _NewEnum(); void _Register(); void _RegisterPending(); void _Revoke();
        void _OnNavigate(); void _OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _GetWindow(); void _ContextSensitiveHelp(); // IOleWindow
        void _InsertMenusSB(); void _SetMenuSB(); void _RemoveMenusSB(); void _SetStatusTextSB(); void _EnableModelessSB();
        void _TranslateAcceleratorSB(); void _BrowseObject(); void _GetViewStateStream(); void _GetControlWindow();
        void _SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void _GetWindow(); void _ContextSensitiveHelp(); // IOleWindow
        void _TranslateAccelerator(); void _EnableModeless(); void _UIActivate(); void _Refresh(); void _CreateViewWindow();
        void _DestroyViewWindow(); void _GetCurrentInfo(); void _AddPropertySheetPages(); void _SaveViewState();
        void _SelectItem();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(uint uItem, ref Guid riid);
    }
}
