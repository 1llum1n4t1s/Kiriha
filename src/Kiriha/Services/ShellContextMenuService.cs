using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// Windows 標準のシェルコンテキストメニュー（IContextMenu）を表示する。
/// Windows 11 の新デザインメニューは Explorer 内部実装で公開 API が無いため、
/// 機能同一の OS 標準メニュー（「その他のオプションを確認」相当）を使用する。
/// </summary>
internal static partial class ShellContextMenuService
{
    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

    /// <summary>
    /// 指定パスに対してシェル verb を直接実行する（例: "unpinfromhome" = クイックアクセスから外す、
    /// "pintohome" = クイックアクセスにピン留め）。
    /// </summary>
    public static bool InvokeVerb(nint hwnd, string path, string verb)
    {
        if (SHParseDisplayName(path, 0, out var pidl, 0, out _) < 0 || pidl == 0)
        {
            return false;
        }

        try
        {
            if (GetContextMenu(hwnd, pidl) is not { } menu)
            {
                return false;
            }

            var hmenu = CreatePopupMenu();
            if (hmenu == 0)
            {
                return false;
            }

            var verbPtr = Marshal.StringToHGlobalAnsi(verb);
            try
            {
                if (menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0) < 0)
                {
                    return false;
                }

                unsafe
                {
                    var info = new CmInvokeCommandInfo
                    {
                        Size = (uint)sizeof(CmInvokeCommandInfo),
                        Hwnd = hwnd,
                        Verb = verbPtr,
                        Show = 1,
                    };
                    return menu.InvokeCommand((nint)(&info)) >= 0;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(verbPtr);
                DestroyMenu(hmenu);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>pidl の親フォルダー経由で IContextMenu を取得する。</summary>
    private static IContextMenu? GetContextMenu(nint hwnd, nint pidl)
    {
        if (SHBindToParent(pidl, IidIShellFolder, out var folderPtr, out var childPidl) < 0 || folderPtr == 0)
        {
            return null;
        }

        var wrappers = new StrategyBasedComWrappers();
        var folder = (IShellFolder)wrappers.GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.None);
        Marshal.Release(folderPtr);

        nint ctxPtr;
        unsafe
        {
            var child = childPidl;
            if (folder.GetUIObjectOf(hwnd, 1, (nint)(&child), IidIContextMenu, 0, out ctxPtr) < 0 || ctxPtr == 0)
            {
                return null;
            }
        }

        var menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(ctxPtr, CreateObjectFlags.None);
        Marshal.Release(ctxPtr);
        return menu;
    }

    /// <summary>指定パスのシェルコンテキストメニューをスクリーン座標 (x, y) に表示する。コマンド実行時 true。</summary>
    public static bool Show(nint hwnd, string path, int x, int y)
    {
        if (SHParseDisplayName(path, 0, out var pidl, 0, out _) < 0 || pidl == 0)
        {
            return false;
        }

        try
        {
            return ShowForPidl(hwnd, pidl, x, y);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static bool ShowForPidl(nint hwnd, nint pidl, int x, int y)
    {
        if (SHBindToParent(pidl, IidIShellFolder, out var folderPtr, out var childPidl) < 0 || folderPtr == 0)
        {
            return false;
        }

        var wrappers = new StrategyBasedComWrappers();
        var folder = (IShellFolder)wrappers.GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.None);
        Marshal.Release(folderPtr);

        nint ctxPtr;
        unsafe
        {
            // childPidl は pidl 内を指すため個別解放は不要
            var child = childPidl;
            if (folder.GetUIObjectOf(hwnd, 1, (nint)(&child), IidIContextMenu, 0, out ctxPtr) < 0 || ctxPtr == 0)
            {
                return false;
            }
        }

        var menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(ctxPtr, CreateObjectFlags.None);
        Marshal.Release(ctxPtr);

        var hmenu = CreatePopupMenu();
        if (hmenu == 0)
        {
            return false;
        }

        try
        {
            if (menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0) < 0)
            {
                return false;
            }

            // 前面でないウィンドウからだとメニュー外クリックで閉じなくなるため前面化する
            SetForegroundWindow(hwnd);
            var cmd = TrackPopupMenuEx(hmenu, TpmReturnCmd | TpmRightButton, x, y, hwnd, 0);
            if (cmd <= 0)
            {
                return false;
            }

            unsafe
            {
                var info = new CmInvokeCommandInfo
                {
                    Size = (uint)sizeof(CmInvokeCommandInfo),
                    Hwnd = hwnd,
                    Verb = cmd - 1,
                    Show = 1, // SW_SHOWNORMAL
                };
                return menu.InvokeCommand((nint)(&info)) >= 0;
            }
        }
        finally
        {
            DestroyMenu(hmenu);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmInvokeCommandInfo
    {
        public uint Size;
        public uint Mask;
        public nint Hwnd;
        public nint Verb;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(nint pidl, in Guid riid, out nint ppv, out nint ppidlLast);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint hMenu, uint flags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}

[GeneratedComInterface]
[Guid("000214E6-0000-0000-C000-000000000046")]
internal partial interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(nint hwnd, nint pbc, nint pszDisplayName, nint pchEaten, out nint ppidl, nint pdwAttributes);

    [PreserveSig]
    int EnumObjects(nint hwnd, uint grfFlags, out nint ppenumIDList);

    [PreserveSig]
    int BindToObject(nint pidl, nint pbc, in Guid riid, out nint ppv);

    [PreserveSig]
    int BindToStorage(nint pidl, nint pbc, in Guid riid, out nint ppv);

    [PreserveSig]
    int CompareIDs(nint lParam, nint pidl1, nint pidl2);

    [PreserveSig]
    int CreateViewObject(nint hwndOwner, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetAttributesOf(uint cidl, nint apidl, ref uint rgfInOut);

    [PreserveSig]
    int GetUIObjectOf(nint hwndOwner, uint cidl, nint apidl, in Guid riid, nint rgfReserved, out nint ppv);

    [PreserveSig]
    int GetDisplayNameOf(nint pidl, uint uFlags, nint pName);

    [PreserveSig]
    int SetNameOf(nint hwnd, nint pidl, nint pszName, uint uFlags, out nint ppidlOut);
}

[GeneratedComInterface]
[Guid("000214E4-0000-0000-C000-000000000046")]
internal partial interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(nint hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

    [PreserveSig]
    int InvokeCommand(nint pici);

    [PreserveSig]
    int GetCommandString(nuint idCmd, uint uType, nint pReserved, nint pszName, uint cchMax);
}
