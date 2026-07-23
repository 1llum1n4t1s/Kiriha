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
    private static readonly Guid IidIDataObject = new("0000010E-0000-0000-C000-000000000046");

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
            LogVerbFailure(verb, path, "SHParseDisplayName");
            return false;
        }

        try
        {
            if (GetContextMenu(hwnd, pidl) is not { } menu)
            {
                LogVerbFailure(verb, path, "GetUIObjectOf(IContextMenu)");
                return false;
            }

            var hmenu = CreatePopupMenu();
            if (hmenu == 0)
            {
                LogVerbFailure(verb, path, "CreatePopupMenu");
                return false;
            }

            var verbPtr = Marshal.StringToHGlobalAnsi(verb);
            try
            {
                if (menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0) < 0)
                {
                    LogVerbFailure(verb, path, "QueryContextMenu");
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
                    var hr = menu.InvokeCommand((nint)(&info));
                    if (hr < 0)
                    {
                        LogVerbFailure(verb, path, $"InvokeCommand (hr=0x{hr:X8})");
                    }
                    return hr >= 0;
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

    /// <summary>
    /// 指定フォルダーの背景コンテキストメニューにある Shell verb を実行する。
    /// RDP の FileGroupDescriptorW / FileContents など、パスを持たない仮想ファイルの
    /// 貼り付けは Explorer と同じ "paste" verb に処理を任せる。
    /// </summary>
    public static bool InvokeDirectoryBackgroundVerb(nint hwnd, string directoryPath, string verb)
    {
        if (hwnd == 0)
        {
            hwnd = GetActiveWindow();
        }

        var result = SHParseDisplayName(directoryPath, 0, out var pidl, 0, out _);
        if (result < 0 || pidl == 0)
        {
            return LogBackgroundVerbFailure("SHParseDisplayName", result);
        }

        try
        {
            result = SHBindToParent(pidl, IidIShellFolder, out var parentPtr, out var childPidl);
            if (result < 0 || parentPtr == 0)
            {
                return LogBackgroundVerbFailure("SHBindToParent", result);
            }

            var wrappers = new StrategyBasedComWrappers();
            var parent = (IShellFolder)wrappers.GetOrCreateObjectForComInstance(
                parentPtr,
                CreateObjectFlags.None);
            Marshal.Release(parentPtr);

            result = parent.BindToObject(childPidl, 0, IidIShellFolder, out var folderPtr);
            if (result < 0 || folderPtr == 0)
            {
                return LogBackgroundVerbFailure("IShellFolder.BindToObject", result);
            }

            var folder = (IShellFolder)wrappers.GetOrCreateObjectForComInstance(
                folderPtr,
                CreateObjectFlags.None);
            Marshal.Release(folderPtr);

            result = folder.CreateViewObject(hwnd, IidIContextMenu, out var contextMenuPtr);
            if (result < 0 || contextMenuPtr == 0)
            {
                return LogBackgroundVerbFailure("IShellFolder.CreateViewObject", result);
            }

            var menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(
                contextMenuPtr,
                CreateObjectFlags.None);
            Marshal.Release(contextMenuPtr);
            return InvokeVerb(menu, hwnd, verb);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static bool InvokeVerb(IContextMenu menu, nint hwnd, string verb)
    {
        var hmenu = CreatePopupMenu();
        if (hmenu == 0)
        {
            return false;
        }

        var verbPtr = Marshal.StringToHGlobalAnsi(verb);
        try
        {
            var result = menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
            if (result < 0)
            {
                return LogBackgroundVerbFailure("IContextMenu.QueryContextMenu", result);
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
                result = menu.InvokeCommand((nint)(&info));
                return result >= 0 || LogBackgroundVerbFailure("IContextMenu.InvokeCommand", result);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(verbPtr);
            DestroyMenu(hmenu);
        }
    }

    private static bool LogBackgroundVerbFailure(string step, int result)
    {
        Logger.Log($"フォルダー背景の Shell verb 実行失敗: {step}, HRESULT=0x{result:X8}", LogLevel.Warning);
        return false;
    }

    private static void LogVerbFailure(string verb, string path, string step)
        => Logger.Log($"Shell verb \"{verb}\" の実行失敗: {step}, path={path}", LogLevel.Warning);

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
        => Show(hwnd, [path], x, y);

    /// <summary>
    /// 複数パス（同一フォルダー内の複数選択）のシェルコンテキストメニューを表示する。
    /// Explorer と同じく、削除・コピー・送る等の verb は選択全体に対して実行される。
    /// </summary>
    public static bool Show(nint hwnd, IReadOnlyList<string> paths, int x, int y)
    {
        var pidls = new List<nint>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, 0, out var pidl, 0, out _) >= 0 && pidl != 0)
                {
                    pidls.Add(pidl);
                }
            }

            return pidls.Count > 0 && ShowForPidls(hwnd, pidls, x, y);
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static bool ShowForPidls(nint hwnd, IReadOnlyList<nint> pidls, int x, int y)
    {
        // シェル拡張（クラウドストレージ・AV 等）の不調で QueryContextMenu が数十秒ブロックすることが
        // あるため、所要時間を計測して遅延時だけ記録する（犯人特定の手掛かりを残す）。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 親フォルダーは先頭項目から取得する（ファイル一覧の複数選択は常に同一フォルダー内）
        if (SHBindToParent(pidls[0], IidIShellFolder, out var folderPtr, out var firstChild) < 0 || folderPtr == 0)
        {
            return false;
        }

        var wrappers = new StrategyBasedComWrappers();
        var folder = (IShellFolder)wrappers.GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.None);
        Marshal.Release(folderPtr);

        // 各絶対 pidl の末尾（子 pidl）を集める。子 pidl は元 pidl 内を指すため個別解放は不要。
        var children = new nint[pidls.Count];
        children[0] = firstChild;
        for (var i = 1; i < pidls.Count; i++)
        {
            if (SHBindToParent(pidls[i], IidIShellFolder, out var parentPtr, out var child) < 0 || parentPtr == 0)
            {
                return false;
            }

            Marshal.Release(parentPtr);
            children[i] = child;
        }

        nint ctxPtr;
        unsafe
        {
            fixed (nint* pChildren = children)
            {
                if (folder.GetUIObjectOf(hwnd, (uint)children.Length, (nint)pChildren, IidIContextMenu, 0, out ctxPtr) < 0
                    || ctxPtr == 0)
                {
                    return false;
                }
            }
        }

        var menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(ctxPtr, CreateObjectFlags.None);
        Marshal.Release(ctxPtr);
        var uiObjectMs = sw.ElapsedMilliseconds;

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

            if (sw.ElapsedMilliseconds > 1000)
            {
                Logger.Log(
                    $"シェルメニューの構築が遅延: GetUIObjectOf まで {uiObjectMs}ms, QueryContextMenu まで {sw.ElapsedMilliseconds}ms"
                    + " (シェル拡張の不調の可能性。クラウドストレージや AV の常駐プロセス再起動で直ることがあります)",
                    LogLevel.Warning);
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
                // 複数選択のプロパティは IContextMenu.InvokeCommand だと、特殊フォルダー
                // （Contacts 等のカスタムプロパティシート持ち）が混ざったときに S_OK のまま
                // 何も表示されないことがある。Explorer 自身が使う公開 API の
                // SHMultiFileProperties へ切り替えて確実に表示する。
                if (pidls.Count > 1 && IsVerb(menu, cmd, "properties"))
                {
                    fixed (nint* pChildren2 = children)
                    {
                        if (folder.GetUIObjectOf(hwnd, (uint)children.Length, (nint)pChildren2, IidIDataObject, 0, out var dataPtr) >= 0
                            && dataPtr != 0)
                        {
                            var mfHr = SHMultiFileProperties(dataPtr, 0);
                            Marshal.Release(dataPtr);
                            if (mfHr < 0)
                            {
                                Logger.Log($"複数選択のプロパティ表示に失敗: HRESULT=0x{mfHr:X8}", LogLevel.Warning);
                            }
                            return mfHr >= 0;
                        }
                    }
                }

                var info = new CmInvokeCommandInfo
                {
                    Size = (uint)sizeof(CmInvokeCommandInfo),
                    Hwnd = hwnd,
                    Verb = cmd - 1,
                    Show = 1, // SW_SHOWNORMAL
                };
                // InvokeCommand ではサードパーティ拡張のコードが動く。フリーズ・クラッシュ時に
                // 「どの項目を実行した直後か」をログから追えるよう、実行前に記録し遅延も計測する
                Logger.Log($"シェルメニュー項目を実行: cmd={cmd}", LogLevel.Debug);
                var invokeStart = sw.ElapsedMilliseconds;
                var hr = menu.InvokeCommand((nint)(&info));
                var invokeMs = sw.ElapsedMilliseconds - invokeStart;
                if (invokeMs > 1000 || hr < 0)
                {
                    Logger.Log(
                        $"シェルメニュー項目の実行結果: cmd={cmd}, HRESULT=0x{hr:X8}, 所要 {invokeMs}ms"
                        + (invokeMs > 1000 ? " (シェル拡張の不調の可能性)" : string.Empty),
                        LogLevel.Warning);
                }
                return hr >= 0;
            }
        }
        finally
        {
            DestroyMenu(hmenu);
        }
    }

    /// <summary>メニュー項目 cmd の正規 verb 名が指定の名前と一致するかを調べる。</summary>
    private static bool IsVerb(IContextMenu menu, int cmd, string verb)
    {
        unsafe
        {
            const uint GcsVerbW = 4;
            var buffer = stackalloc char[260];
            if (menu.GetCommandString((nuint)(cmd - 1), GcsVerbW, 0, (nint)buffer, 260) < 0)
            {
                return false;
            }

            return string.Equals(new string(buffer), verb, StringComparison.OrdinalIgnoreCase);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHMultiFileProperties(nint pdtobj, uint flags);

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

    [LibraryImport("user32.dll")]
    private static partial nint GetActiveWindow();
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
