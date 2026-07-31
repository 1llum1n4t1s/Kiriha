using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// シェルコンテキストメニューの末尾へ足すアプリ独自の項目（区切り線付きで 1 件だけ）。
/// シェル側の verb ではないので、選択時は <see cref="Invoke"/> をそのまま呼ぶ。
/// </summary>
/// <param name="Text">メニューに出す表示名（ローカライズ済みの文字列）。</param>
/// <param name="Invoke">選択されたときに実行する処理。</param>
/// <param name="StockIconId">項目の左に出すシェル標準アイコン（SIID_*）。</param>
internal sealed record ShellMenuExtraItem(string Text, Action Invoke, uint StockIconId);

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

    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfByPosition = 0x0400;
    private const uint MiimBitmap = 0x0080;

    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const uint DiNormal = 0x0003;

    /// <summary>SIID_FOLDER。エクスプローラーと同じ標準のフォルダーアイコン。</summary>
    public const uint StockIconFolder = 3;
    private const uint ShgsiIcon = 0x0000_0100;
    private const uint ShgsiSmallIcon = 0x0000_0001;

    /// <summary>アプリ独自項目のコマンド ID。シェル項目は 1〜0x7FFF を使うので、その外側を割り当てる。</summary>
    private const int ExtraItemCommandId = 0x8000;

    /// <summary>CMIC_MASK_UNICODE。立てると *W フィールドが使われる。</summary>
    private const uint CmicMaskUnicode = 0x0000_4000;

    /// <summary>
    /// lpDirectory（作業ディレクトリ）を渡して IContextMenu.InvokeCommand を実行する。
    /// これを省くと、起動されるプログラムのカレントディレクトリが Kiriha のプロセスのものになる。
    /// 「管理者として実行」では昇格した別プロセスとして起動されるため、そのカレントは
    /// C:\Windows\System32 になり、`ESETUninstaller.exe /force` のように自分と同じフォルダーの
    /// ファイルを相対参照するバッチが「認識されていません」で失敗する。エクスプローラーは
    /// 対象フォルダーを lpDirectory として渡しており、それに合わせる。
    /// </summary>
    /// <param name="verbName">verb 名。メニュー項目をコマンド ID で実行するときは null。</param>
    /// <param name="commandId">MAKEINTRESOURCE 相当のコマンド ID（verbName が null のときに使う）。</param>
    private static unsafe int InvokeCommandInDirectory(
        IContextMenu menu,
        nint hwnd,
        string? verbName,
        nint commandId,
        string? workingDirectory)
    {
        var verbAnsi = verbName is null ? 0 : Marshal.StringToHGlobalAnsi(verbName);
        var verbUnicode = verbName is null ? 0 : Marshal.StringToHGlobalUni(verbName);
        var directoryAnsi = workingDirectory is null ? 0 : Marshal.StringToHGlobalAnsi(workingDirectory);
        var directoryUnicode = workingDirectory is null ? 0 : Marshal.StringToHGlobalUni(workingDirectory);
        try
        {
            var info = new CmInvokeCommandInfoEx
            {
                Size = (uint)sizeof(CmInvokeCommandInfoEx),
                Mask = CmicMaskUnicode,
                Hwnd = hwnd,
                // コマンド ID 指定（MAKEINTRESOURCE）のときは ANSI・Unicode どちらも同じ値を入れる。
                Verb = verbName is null ? commandId : verbAnsi,
                VerbW = verbName is null ? commandId : verbUnicode,
                Directory = directoryAnsi,
                DirectoryW = directoryUnicode,
                Show = 1, // SW_SHOWNORMAL
            };
            return menu.InvokeCommand((nint)(&info));
        }
        finally
        {
            if (verbAnsi != 0) Marshal.FreeHGlobal(verbAnsi);
            if (verbUnicode != 0) Marshal.FreeHGlobal(verbUnicode);
            if (directoryAnsi != 0) Marshal.FreeHGlobal(directoryAnsi);
            if (directoryUnicode != 0) Marshal.FreeHGlobal(directoryUnicode);
        }
    }

    /// <summary>対象の項目が入っているフォルダー（= 起動するプログラムの作業ディレクトリ）。
    /// ドライブ直下などで親が取れない場合はそのパス自身を使う。</summary>
    private static string? ResolveWorkingDirectory(string path)
    {
        try
        {
            if (path.Length == 0 || path == FileSystemService.ComputerPath)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(path);
            return parent is { Length: > 0 } ? parent : path;
        }
        catch (ArgumentException)
        {
            // シェル名前空間の仮想パス（::{GUID} 等）は実フォルダーではないので指定しない
            return null;
        }
    }

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

            // IContextMenu の実体はサードパーティのシェル拡張。使い終えたら必ず手放す
            try
            {
                var hmenu = CreatePopupMenu();
                if (hmenu == 0)
                {
                    LogVerbFailure(verb, path, "CreatePopupMenu");
                    return false;
                }

                try
                {
                    if (menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0) < 0)
                    {
                        LogVerbFailure(verb, path, "QueryContextMenu");
                        return false;
                    }

                    var hr = InvokeCommandInDirectory(
                        menu, hwnd, verb, commandId: 0, ResolveWorkingDirectory(path));
                    if (hr < 0)
                    {
                        LogVerbFailure(verb, path, $"InvokeCommand (hr=0x{hr:X8})");
                    }

                    return hr >= 0;
                }
                finally
                {
                    DestroyMenu(hmenu);
                }
            }
            finally
            {
                ComRelease.Release(menu);
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

            IShellFolder? folder = null;
            IContextMenu? menu = null;
            try
            {
                result = parent.BindToObject(childPidl, 0, IidIShellFolder, out var folderPtr);
                if (result < 0 || folderPtr == 0)
                {
                    return LogBackgroundVerbFailure("IShellFolder.BindToObject", result);
                }

                folder = (IShellFolder)wrappers.GetOrCreateObjectForComInstance(
                    folderPtr,
                    CreateObjectFlags.None);
                Marshal.Release(folderPtr);

                result = folder.CreateViewObject(hwnd, IidIContextMenu, out var contextMenuPtr);
                if (result < 0 || contextMenuPtr == 0)
                {
                    return LogBackgroundVerbFailure("IShellFolder.CreateViewObject", result);
                }

                menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(
                    contextMenuPtr,
                    CreateObjectFlags.None);
                Marshal.Release(contextMenuPtr);
                return InvokeVerb(menu, hwnd, verb, directoryPath);
            }
            finally
            {
                ComRelease.Release(menu);
                ComRelease.Release(folder);
                ComRelease.Release(parent);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static bool InvokeVerb(IContextMenu menu, nint hwnd, string verb, string? workingDirectory)
    {
        var hmenu = CreatePopupMenu();
        if (hmenu == 0)
        {
            return false;
        }

        try
        {
            var result = menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
            if (result < 0)
            {
                return LogBackgroundVerbFailure("IContextMenu.QueryContextMenu", result);
            }

            // 背景メニューはそのフォルダー自身が作業ディレクトリ（貼り付け先もここ）。
            result = InvokeCommandInDirectory(menu, hwnd, verb, commandId: 0, workingDirectory);
            return result >= 0 || LogBackgroundVerbFailure("IContextMenu.InvokeCommand", result);
        }
        finally
        {
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

        // 返した IContextMenu は呼び出し側が ComRelease.Release する
        try
        {
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
        finally
        {
            ComRelease.Release(folder);
        }
    }

    /// <summary>指定パスのシェルコンテキストメニューをスクリーン座標 (x, y) に表示する。コマンド実行時 true。</summary>
    public static bool Show(nint hwnd, string path, int x, int y, params ShellMenuExtraItem?[] extraItems)
        => Show(hwnd, [path], x, y, extraItems);

    /// <summary>
    /// 複数パス（同一フォルダー内の複数選択）のシェルコンテキストメニューを表示する。
    /// Explorer と同じく、削除・コピー・送る等の verb は選択全体に対して実行される。
    /// <paramref name="extraItems"/> を渡すと、区切り線を挟んでアプリ独自の項目を末尾に足す
    /// （渡した順に並ぶ）。null 要素は無視するので、条件付きの項目はそのまま並べてよい。
    /// </summary>
    public static bool Show(nint hwnd, IReadOnlyList<string> paths, int x, int y, params ShellMenuExtraItem?[] extraItems)
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

            return pidls.Count > 0
                   && ShowForPidls(
                       hwnd, pidls, x, y,
                       [.. extraItems.Where(item => item is not null).Select(item => item!)],
                       ResolveWorkingDirectory(paths[0]));
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static bool ShowForPidls(
        nint hwnd,
        IReadOnlyList<nint> pidls,
        int x,
        int y,
        IReadOnlyList<ShellMenuExtraItem> extraItems,
        string? workingDirectory)
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
                ComRelease.Release(folder);
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
                    ComRelease.Release(folder);
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
            ComRelease.Release(menu);
            ComRelease.Release(folder);
            return false;
        }

        // アプリ独自項目のアイコン。メニューを壊した後に解放する（表示中は必要）。
        var extraBitmaps = new List<nint>();
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

            // Kiriha 自身の中で「Kiriha で開く」は無意味なので取り除く（エクスプローラー側には残る）
            RemoveOwnVerbItems(menu, hmenu);

            // アプリ独自の項目（「新しいタブで開く」「エクスプローラーで開く」等）はシェル項目の後ろへ足す。
            // シェル拡張の項目を押しのけないよう、必ず QueryContextMenu の後に追加する。
            // コマンド ID は ExtraItemCommandId から連番。シェルが使う 1〜0x7FFF の外側なので衝突しない。
            if (extraItems.Count > 0)
            {
                AppendMenu(hmenu, MfSeparator, 0, string.Empty);
                for (var i = 0; i < extraItems.Count; i++)
                {
                    var id = ExtraItemCommandId + i;
                    AppendMenu(hmenu, MfString, (nuint)id, extraItems[i].Text);
                    var bitmap = SetMenuItemStockIcon(hmenu, id, extraItems[i].StockIconId);
                    if (bitmap != 0)
                    {
                        extraBitmaps.Add(bitmap);
                    }
                }
            }

            // 前面でないウィンドウからだとメニュー外クリックで閉じなくなるため前面化する
            SetForegroundWindow(hwnd);
            var cmd = TrackPopupMenuEx(hmenu, TpmReturnCmd | TpmRightButton, x, y, hwnd, 0);
            if (cmd <= 0)
            {
                return false;
            }

            if (cmd >= ExtraItemCommandId && cmd < ExtraItemCommandId + extraItems.Count)
            {
                extraItems[cmd - ExtraItemCommandId].Invoke();
                return true;
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

                // InvokeCommand ではサードパーティ拡張のコードが動く。フリーズ・クラッシュ時に
                // 「どの項目を実行した直後か」をログから追えるよう、実行前に記録し遅延も計測する
                Logger.Log($"シェルメニュー項目を実行: cmd={cmd}", LogLevel.Debug);
                var invokeStart = sw.ElapsedMilliseconds;
                var hr = InvokeCommandInDirectory(
                    menu, hwnd, verbName: null, commandId: cmd - 1, workingDirectory);
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
            foreach (var bitmap in extraBitmaps)
            {
                DeleteObject(bitmap);
            }

            // IContextMenu の実体はサードパーティのシェル拡張。GC 任せにせずここで手放す
            ComRelease.Release(menu);
            ComRelease.Release(folder);
        }
    }

    /// <summary>
    /// Kiriha 自身が登録したエクスプローラー用の verb（「Kiriha で開く」）をメニューから取り除く。
    /// 自分の中で自分を開き直す項目は意味が無いため。判定は表示名ではなく正規 verb 名で行う
    /// （表示名は 17 言語で変わるが、verb 名はレジストリのキー名で固定）。
    /// </summary>
    private static void RemoveOwnVerbItems(IContextMenu menu, nint hmenu)
    {
        for (var i = GetMenuItemCount(hmenu) - 1; i >= 0; i--)
        {
            var id = GetMenuItemID(hmenu, i);
            if (id > 0 && IsVerb(menu, id, WindowsIntegrationService.ContextMenuVerb))
            {
                DeleteMenu(hmenu, (uint)i, MfByPosition);
            }
        }
    }

    /// <summary>
    /// メニュー項目の左へシェルの標準アイコンを付ける。戻り値は作成した HBITMAP
    /// （メニューを破棄した後に <c>DeleteObject</c> する。0 なら付けられなかった）。
    /// </summary>
    private static unsafe nint SetMenuItemStockIcon(nint hmenu, int commandId, uint stockIconId)
    {
        var bitmap = CreateStockIconBitmap(stockIconId);
        if (bitmap == 0)
        {
            return 0;
        }

        var info = new MenuItemInfoW
        {
            Size = (uint)sizeof(MenuItemInfoW),
            Mask = MiimBitmap,
            BitmapItem = bitmap,
        };
        if (!SetMenuItemInfo(hmenu, (uint)commandId, false, ref info))
        {
            DeleteObject(bitmap);
            return 0;
        }

        return bitmap;
    }

    /// <summary>シェルの標準アイコンを、メニューが扱える 32bpp のビットマップへ描き起こす。</summary>
    private static unsafe nint CreateStockIconBitmap(uint stockIconId)
    {
        var icon = new ShStockIconInfo { Size = (uint)sizeof(ShStockIconInfo) };
        if (SHGetStockIconInfo(stockIconId, ShgsiIcon | ShgsiSmallIcon, ref icon) < 0 || icon.Icon == 0)
        {
            return 0;
        }

        try
        {
            var cx = GetSystemMetrics(SmCxSmIcon);
            var cy = GetSystemMetrics(SmCySmIcon);
            var hdc = CreateCompatibleDC(0);
            if (hdc == 0)
            {
                return 0;
            }

            try
            {
                // 高さを負にしてトップダウン DIB にする（アルファ付きのままメニューへ渡せる）
                var header = new BitmapInfoHeader
                {
                    Size = (uint)sizeof(BitmapInfoHeader),
                    Width = cx,
                    Height = -cy,
                    Planes = 1,
                    BitCount = 32,
                };
                var bitmap = CreateDIBSection(hdc, ref header, 0, out _, 0, 0);
                if (bitmap == 0)
                {
                    return 0;
                }

                var previous = SelectObject(hdc, bitmap);
                DrawIconEx(hdc, 0, 0, icon.Icon, cx, cy, 0, 0, DiNormal);
                SelectObject(hdc, previous);
                return bitmap;
            }
            finally
            {
                DeleteDC(hdc);
            }
        }
        finally
        {
            DestroyIcon(icon.Icon);
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

    /// <summary>CMINVOKECOMMANDINFOEX。Unicode 版のフィールド（*W）まで持つ拡張版で、
    /// 作業ディレクトリを Unicode で渡すために使う（ANSI の lpDirectory だけだと、
    /// システム ANSI コードページで表せないフォルダー名が壊れる）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CmInvokeCommandInfoEx
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
        public nint Title;
        public nint VerbW;
        public nint ParametersW;
        public nint DirectoryW;
        public nint TitleW;
        public int InvokeX;
        public int InvokeY;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(nint pidl, in Guid riid, out nint ppv, out nint ppidlLast);


    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint hMenu, uint flags, nuint idNewItem, string item);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(nint hMenu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemID(nint hMenu, int pos);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteMenu(nint hMenu, uint position, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuItemInfo(nint hMenu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfoW info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(nint hdc, int x, int y, nint icon,
        int width, int height, uint step, nint brush, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint hdc, ref BitmapInfoHeader header, uint usage,
        out nint bits, nint section, uint offset);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint stockIconId, uint flags, ref ShStockIconInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfoW
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint SubMenu;
        public nint BitmapChecked;
        public nint BitmapUnchecked;
        public nuint ItemData;
        public nint TypeData;
        public uint Cch;
        public nint BitmapItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShStockIconInfo
    {
        public uint Size;
        public nint Icon;
        public int SysImageIndex;
        public int IconIndex;
        public fixed char Path[260];
    }

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
