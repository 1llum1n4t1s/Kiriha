using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kiriha.Services;

/// <summary>
/// <see cref="ShellMenuSession"/> が吸い出したメニュー項目 1 件分。
/// </summary>
internal sealed class ShellMenuItem
{
    /// <summary>ニーモニック（&amp;）と加速キー表記を取り除いた表示名。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>表示名に "\t" で付いていた加速キー表記。シェル項目にはほぼ付かない。</summary>
    public string? Shortcut { get; init; }

    /// <summary>QueryContextMenu を 1 起点で呼んだときのコマンド ID。0 はポップアップと区切り線。</summary>
    public int CommandId { get; init; }

    /// <summary>正規 verb 名（"open" 等）。取得できない項目は null。シェル拡張は基本的に取れない。</summary>
    public string? Verb { get; init; }

    public bool IsSeparator { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsChecked { get; init; }

    /// <summary>エクスプローラーで太字になる既定の項目。</summary>
    public bool IsDefault { get; init; }

    /// <summary>hbmpItem から起こしたアイコン。所有権はセッションにあるので呼び出し側は破棄しない。</summary>
    public Bitmap? Icon { get; init; }

    public IReadOnlyList<ShellMenuItem> Children { get; init; } = [];
}

/// <summary>
/// シェルコンテキストメニュー（IContextMenu）の「中身」だけを取り出して保持するセッション。
/// </summary>
/// <remarks>
/// 表示まで Win32 に任せる <see cref="ShellContextMenuService.Show"/> と違い、こちらは
/// QueryContextMenu が組み立てた HMENU を走査して <see cref="ShellMenuItem"/> へ変換するだけで、
/// 描画は Avalonia 側（<see cref="Controls.ExplorerContextMenu"/>）が行う。
/// <para>
/// 項目の実行にはシェル拡張の COM オブジェクトが生きている必要があるため、メニューを閉じて
/// 実行し終えるまで <see cref="Dispose"/> してはいけない。P/Invoke 宣言が
/// <see cref="ShellContextMenuService"/> と一部重複しているのは意図的で、実績のある
/// Win32 表示パス（設定「Windows 標準」で使う）には手を入れずに済ませるため。
/// </para>
/// </remarks>
internal sealed partial class ShellMenuSession : IDisposable
{
    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid IidIDataObject = new("0000010E-0000-0000-C000-000000000046");

    private const uint CmfNormal = 0x0000;
    private const uint CmfCanRename = 0x0010;
    private const uint CmfExtendedVerbs = 0x0100;

    private const uint MiimState = 0x0001;
    private const uint MiimId = 0x0002;
    private const uint MiimSubMenu = 0x0004;
    private const uint MiimString = 0x0040;
    private const uint MiimBitmap = 0x0080;
    private const uint MiimFType = 0x0100;

    private const uint MftSeparator = 0x0800;
    private const uint MftOwnerDraw = 0x0100;

    private const uint MfsGrayed = 0x0003;
    private const uint MfsChecked = 0x0008;
    private const uint MfsDefault = 0x1000;

    private const uint WmInitMenuPopup = 0x0117;

    private const uint GcsVerbW = 4;
    private const uint CmicMaskUnicode = 0x0000_4000;

    /// <summary>シェル拡張が暴走したときに無限ループしないための保険。</summary>
    private const int MaxDepth = 8;
    private const int MaxItemsPerMenu = 512;

    private readonly nint _hwnd;
    private readonly List<nint> _pidls;
    private readonly nint[] _children;
    private readonly string? _workingDirectory;
    private readonly List<Bitmap> _icons = [];

    private IShellFolder? _folder;
    private IContextMenu? _menu;
    private nint _hmenu;
    private bool _disposed;

    private ShellMenuSession(
        nint hwnd,
        List<nint> pidls,
        nint[] children,
        IShellFolder folder,
        IContextMenu menu,
        nint hmenu,
        string? workingDirectory)
    {
        _hwnd = hwnd;
        _pidls = pidls;
        _children = children;
        _folder = folder;
        _menu = menu;
        _hmenu = hmenu;
        _workingDirectory = workingDirectory;
        Items = [];
    }

    /// <summary>吸い出したメニュー項目（トップレベル）。</summary>
    public IReadOnlyList<ShellMenuItem> Items { get; private set; }

    /// <summary>対象パスの件数。複数選択のプロパティ表示の分岐に使う。</summary>
    public int PathCount => _pidls.Count;

    /// <summary>先読みを二重に走らせないための実行済みフラグ（0 = 未起動）。</summary>
    private static int _warmedUp;

    /// <summary>先読みの完了（成功・失敗どちらでも）を待つための合図。</summary>
    private static readonly ManualResetEventSlim WarmUpCompleted = new(initialState: false);

    /// <summary>いま実行中のスレッドが先読みスレッド自身か。先読みは <see cref="Create"/> を
    /// 呼ぶので、これが無いと自分の完了を自分で待って永久に固まる。</summary>
    [ThreadStatic]
    private static bool _isWarmUpThread;

    /// <summary>先読み待ちの上限。壊れたシェル拡張が固まっても UI を巻き込まないための保険で、
    /// 待ち切れなかった場合は温まっていない可能性を承知でそのまま進む。</summary>
    private static readonly TimeSpan WarmUpWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// シェルメニューの先読み。プロセス内で最初の <c>QueryContextMenu</c> だけ、Windows 11 系の
    /// <c>IExplorerCommand</c> / パッケージ登録型ハンドラー（「ターミナルで開く」「Code で開く」等）が
    /// 列挙に間に合わず結果から丸ごと抜け落ちる。2 回目以降は同じ引数でも全部そろうため、
    /// 起動直後に捨て呼び出しを 1 回だけ済ませて、ユーザーの最初の右クリックを 2 回目にする。
    /// </summary>
    /// <remarks>
    /// 実測（同一プロセス内で条件を変えて計測）:
    /// 初回のみ 26 項目・240ms、2 回目以降は 30 項目・42〜51ms。CMF フラグや
    /// <c>WM_INITMENUPOPUP</c> の有無は無関係で、「プロセス内で 2 回目かどうか」だけが効く。
    /// 5 秒待ってから初回を呼んでも欠けたままなので、時間ではなく呼び出しが引き金。
    /// 温まりはプロセス単位なので、対象パスは実際に右クリックするフォルダーでなくてよい。
    /// <para>
    /// シェル拡張は STA を要求するものが多いので専用 STA スレッドで動かす
    /// （<see cref="QuickAccessService"/> と同じ理由）。起動を遅らせないため Join はしない。
    /// </para>
    /// </remarks>
    public static void WarmUp()
    {
        if (Interlocked.Exchange(ref _warmedUp, 1) != 0)
        {
            return;
        }

        var thread = new Thread(WarmUpCore)
        {
            IsBackground = true,
            Name = "Kiriha-ShellMenuWarmUp",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 先読みが走っている最中なら、その完了まで待つ。起動直後（先読みは実測 ~290ms）に
    /// ユーザーが右クリックした場合だけ効き、それ以外は即座に返る。
    /// 待たずに進むと、その 1 回だけ「ターミナルで開く」等が欠けたメニューになる。
    /// </summary>
    internal static void WaitForWarmUp()
    {
        // 先読みを起動していない（テストや先読み前）、既に完了、先読みスレッド自身なら待たない
        if (Volatile.Read(ref _warmedUp) == 0 || WarmUpCompleted.IsSet || _isWarmUpThread)
        {
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var completed = WarmUpCompleted.Wait(WarmUpWaitTimeout);
        Logger.Log(
            completed
                ? $"シェルメニューの先読み完了を待機: {sw.ElapsedMilliseconds}ms"
                : $"シェルメニューの先読み待ちがタイムアウト: {sw.ElapsedMilliseconds}ms"
                  + "（このメニューだけ一部のシェル拡張が出ない可能性）",
            completed ? LogLevel.Info : LogLevel.Warning);
    }

    private static void WarmUpCore()
    {
        _isWarmUpThread = true;
        try
        {
            // 対象は「必ず存在してシェル拡張が普通に反応するフォルダー」であれば何でもよい。
            var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.Length == 0 || !Directory.Exists(path))
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }

            if (path.Length == 0)
            {
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var session = Create(0, [path], extendedVerbs: false);
            Logger.Log(
                session is null
                    ? "シェルメニューの先読みに失敗（初回の右クリックで一部のシェル拡張が出ない可能性）"
                    : $"シェルメニューを先読み: {sw.ElapsedMilliseconds}ms, {session.Items.Count} 項目",
                session is null ? LogLevel.Warning : LogLevel.Info);
        }
        catch (Exception ex)
        {
            // 先読みは無くても動くので、失敗しても起動は続ける
            Logger.Log($"シェルメニューの先読みに失敗: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            // 失敗しても必ず合図する（待っている右クリックを取り残さない）
            WarmUpCompleted.Set();
        }
    }

    /// <summary>
    /// 指定パス（同一フォルダー内の複数選択可）のシェルメニューを組み立てて中身を取り出す。
    /// 失敗したら null を返すので、呼び出し側は Win32 表示へフォールバックする。
    /// </summary>
    /// <param name="extendedVerbs">Shift 押下時。エクスプローラーと同じく拡張 verb も含める。</param>
    public static ShellMenuSession? Create(nint hwnd, IReadOnlyList<string> paths, bool extendedVerbs)
    {
        // 起動直後に右クリックされた場合だけ、先読みの完了を待ってから組み立てる
        // （待たないと、その 1 回だけ Windows 11 系のハンドラーが欠ける）。
        WaitForWarmUp();

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

            if (pidls.Count == 0)
            {
                return null;
            }

            var session = CreateCore(hwnd, pidls, ResolveWorkingDirectory(paths[0]), extendedVerbs);
            if (session is null)
            {
                foreach (var pidl in pidls)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }

            return session;
        }
        catch (Exception ex)
        {
            Logger.Log($"シェルメニューの取得に失敗: {ex.Message}", LogLevel.Warning);
            foreach (var pidl in pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            return null;
        }
    }

    private static ShellMenuSession? CreateCore(
        nint hwnd,
        List<nint> pidls,
        string? workingDirectory,
        bool extendedVerbs)
    {
        // シェル拡張（クラウドストレージ・AV 等）の不調で QueryContextMenu が数十秒ブロックすることが
        // あるため、Win32 表示パスと同じく所要時間を測って遅延時だけ記録する。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (SHBindToParent(pidls[0], IidIShellFolder, out var folderPtr, out var firstChild) < 0 || folderPtr == 0)
        {
            return null;
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
                return null;
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
                    return null;
                }
            }
        }

        var menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(ctxPtr, CreateObjectFlags.None);
        Marshal.Release(ctxPtr);

        var hmenu = CreatePopupMenu();
        if (hmenu == 0)
        {
            ComRelease.Release(menu);
            ComRelease.Release(folder);
            return null;
        }

        var flags = CmfNormal | CmfCanRename | (extendedVerbs ? CmfExtendedVerbs : 0);
        if (menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, flags) < 0)
        {
            DestroyMenu(hmenu);
            ComRelease.Release(menu);
            ComRelease.Release(folder);
            return null;
        }

        if (sw.ElapsedMilliseconds > 1000)
        {
            Logger.Log(
                $"シェルメニューの構築が遅延: {sw.ElapsedMilliseconds}ms"
                + " (シェル拡張の不調の可能性。クラウドストレージや AV の常駐プロセス再起動で直ることがあります)",
                LogLevel.Warning);
        }

        var session = new ShellMenuSession(hwnd, pidls, children, folder, menu, hmenu, workingDirectory);
        session.RemoveOwnVerbItems();
        session.Items = session.ReadMenu(hmenu, depth: 0);
        return session;
    }

    /// <summary>
    /// Kiriha 自身が登録したエクスプローラー用 verb（「Kiriha で開く」）を落とす。判定は表示名ではなく
    /// 正規 verb 名で行う（表示名は 17 言語で変わるが、verb 名はレジストリのキー名で固定）。
    /// </summary>
    private void RemoveOwnVerbItems()
    {
        const uint mfByPosition = 0x0400;
        for (var i = GetMenuItemCount(_hmenu) - 1; i >= 0; i--)
        {
            var id = GetMenuItemID(_hmenu, i);
            if (id > 0 && string.Equals(
                    TryGetVerb(id), WindowsIntegrationService.ContextMenuVerb, StringComparison.OrdinalIgnoreCase))
            {
                DeleteMenu(_hmenu, (uint)i, mfByPosition);
            }
        }
    }

    /// <summary>HMENU を 1 段ぶん走査して項目データへ変換する（サブメニューは再帰）。</summary>
    private unsafe List<ShellMenuItem> ReadMenu(nint hmenu, int depth)
    {
        var results = new List<ShellMenuItem>();
        var count = Math.Min(GetMenuItemCount(hmenu), MaxItemsPerMenu);
        for (var i = 0; i < count; i++)
        {
            var info = new MenuItemInfoW
            {
                Size = (uint)sizeof(MenuItemInfoW),
                Mask = MiimFType | MiimState | MiimId | MiimSubMenu | MiimBitmap,
            };
            if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info))
            {
                continue;
            }

            if ((info.Type & MftSeparator) != 0)
            {
                results.Add(new ShellMenuItem { IsSeparator = true });
                continue;
            }

            var text = ReadMenuText(hmenu, i);
            if (text is null && (info.Type & MftOwnerDraw) != 0)
            {
                // オーナードローの項目はテキストを持たない。ヘルプ文字列で代替できなければ捨てる
                // （代替できないのは主に区切り用の装飾項目で、押せる項目ではない）。
                text = TryGetHelpText(info.Id);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var (display, shortcut) = SplitAccelerator(text);
            var children = info.SubMenu != 0 && depth < MaxDepth
                ? ReadSubMenu(info.SubMenu, i, depth)
                : [];

            // 中身が空のポップアップは押しても何も起きないので出さない
            if (info.SubMenu != 0 && children.Count == 0)
            {
                continue;
            }

            results.Add(new ShellMenuItem
            {
                Text = display,
                Shortcut = shortcut,
                CommandId = info.SubMenu != 0 ? 0 : (int)info.Id,
                Verb = info.SubMenu != 0 ? null : TryGetVerb((int)info.Id),
                IsEnabled = (info.State & MfsGrayed) == 0,
                IsChecked = (info.State & MfsChecked) != 0,
                IsDefault = (info.State & MfsDefault) != 0,
                Icon = ConvertMenuBitmap(info.BitmapItem),
                Children = children,
            });
        }

        return results;
    }

    /// <summary>
    /// サブメニューを読む。「送る」「新規作成」のように WM_INITMENUPOPUP を受けて初めて中身を作る
    /// 実装があるため、IContextMenu2/3 を持っていれば先に通知してから走査する。
    /// </summary>
    private List<ShellMenuItem> ReadSubMenu(nint hSubMenu, int position, int depth)
    {
        try
        {
            if (_menu is IContextMenu3 menu3)
            {
                menu3.HandleMenuMsg2(WmInitMenuPopup, hSubMenu, position, out _);
            }
            else if (_menu is IContextMenu2 menu2)
            {
                menu2.HandleMenuMsg(WmInitMenuPopup, hSubMenu, position);
            }
        }
        catch (Exception ex)
        {
            // サードパーティ製の実装。落ちてもサブメニューが空になるだけで済ませる
            Logger.Log($"サブメニューの初期化に失敗: {ex.Message}", LogLevel.Debug);
        }

        return ReadMenu(hSubMenu, depth + 1);
    }

    /// <summary>メニュー項目の文字列を読む（長さ問い合わせ → バッファ確保の 2 段階）。</summary>
    private static unsafe string? ReadMenuText(nint hmenu, int position)
    {
        var probe = new MenuItemInfoW
        {
            Size = (uint)sizeof(MenuItemInfoW),
            Mask = MiimString,
        };
        if (!GetMenuItemInfo(hmenu, (uint)position, true, ref probe) || probe.Cch == 0)
        {
            return null;
        }

        var length = (int)probe.Cch + 1;
        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            var info = new MenuItemInfoW
            {
                Size = (uint)sizeof(MenuItemInfoW),
                Mask = MiimString,
                TypeData = (nint)p,
                Cch = (uint)length,
            };
            if (!GetMenuItemInfo(hmenu, (uint)position, true, ref info))
            {
                return null;
            }

            return new string(buffer, 0, (int)info.Cch);
        }
    }

    /// <summary>"名前(&amp;N)\tCtrl+N" のような表示名を、表示テキストとショートカット表記へ分ける。</summary>
    private static (string Text, string? Shortcut) SplitAccelerator(string raw)
    {
        var tab = raw.IndexOf('\t');
        var text = tab >= 0 ? raw[..tab] : raw;
        var shortcut = tab >= 0 && tab + 1 < raw.Length ? raw[(tab + 1)..].Trim() : null;
        return (StripMnemonic(text), string.IsNullOrWhiteSpace(shortcut) ? null : shortcut);
    }

    /// <summary>
    /// ニーモニック表記を取り除く（&amp;&amp; は文字としての &amp; なので 1 つに畳む）。
    /// </summary>
    /// <remarks>
    /// 日本語・中国語・韓国語のメニューは「プログラムから開く(&amp;H)」のようにアクセスキーを括弧で後置する。
    /// &amp; だけ落とすと "(H)" が本文として残り、Windows 11 のメニューと見た目が食い違うので括弧ごと外す。
    /// </remarks>
    private static string StripMnemonic(string text)
    {
        text = MnemonicSuffixRegex().Replace(text, string.Empty);
        if (!text.Contains('&'))
        {
            return text.Trim();
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                sb.Append('&');
                i++;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>「(&amp;H)」「（&amp;H）」形式の後置ニーモニック。半角・全角どちらの括弧も対象にする。</summary>
    [GeneratedRegex(@"[(（]\s*&.\s*[)）]")]
    private static partial Regex MnemonicSuffixRegex();

    /// <summary>メニュー項目 cmd の正規 verb 名。取得できなければ null（シェル拡張は大半が null）。</summary>
    private string? TryGetVerb(int cmd)
    {
        if (_menu is null || cmd <= 0)
        {
            return null;
        }

        unsafe
        {
            var buffer = stackalloc char[260];
            if (_menu.GetCommandString((nuint)(cmd - 1), GcsVerbW, 0, (nint)buffer, 260) < 0)
            {
                return null;
            }

            var verb = new string(buffer);
            return verb.Length == 0 ? null : verb;
        }
    }

    /// <summary>オーナードロー項目の代替テキストとしてヘルプ文字列を試す。</summary>
    private string? TryGetHelpText(uint cmd)
    {
        const uint gcsHelpTextW = 5;
        if (_menu is null || cmd == 0)
        {
            return null;
        }

        unsafe
        {
            var buffer = stackalloc char[260];
            if (_menu.GetCommandString((nuint)(cmd - 1), gcsHelpTextW, 0, (nint)buffer, 260) < 0)
            {
                return null;
            }

            var text = new string(buffer);
            return text.Length == 0 ? null : text;
        }
    }

    /// <summary>
    /// メニュー項目の HBITMAP を Avalonia のビットマップへ起こす。
    /// </summary>
    /// <remarks>
    /// アルファが全ピクセル 0 のものは「アルファ無しの 24bpp 相当」なので不透明として扱う。
    /// これをしないと、アルファ付き前提で描くと何も見えないアイコンが出る。
    /// </remarks>
    private unsafe Bitmap? ConvertMenuBitmap(nint hbitmap)
    {
        // HBMMENU_CALLBACK(-1) や HBMMENU_SYSTEM(1) 等の疑似ハンドルは実ビットマップではない
        if (hbitmap is 0 || (hbitmap >= -1 && hbitmap <= 11))
        {
            return null;
        }

        var bm = default(BitmapStruct);
        if (GetObject(hbitmap, sizeof(BitmapStruct), (nint)(&bm)) == 0 || bm.Width <= 0 || bm.Height == 0)
        {
            return null;
        }

        var width = bm.Width;
        var height = Math.Abs(bm.Height);
        if (width > 256 || height > 256)
        {
            return null;
        }

        var buffer = new byte[width * height * 4];
        var hdc = GetDC(0);
        if (hdc == 0)
        {
            return null;
        }

        try
        {
            // 高さを負にしてトップダウン・32bpp で受け取る
            var header = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            fixed (byte* p = buffer)
            {
                if (GetDIBits(hdc, hbitmap, 0, (uint)height, (nint)p, ref header, 0) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            ReleaseDC(0, hdc);
        }

        var hasAlpha = false;
        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (hasAlpha)
        {
            // Skia は乗算済みアルファを前提にするので、ここで畳んでおく
            for (var i = 0; i < buffer.Length; i += 4)
            {
                var a = buffer[i + 3];
                if (a == 255) continue;
                buffer[i] = (byte)(buffer[i] * a / 255);
                buffer[i + 1] = (byte)(buffer[i + 1] * a / 255);
                buffer[i + 2] = (byte)(buffer[i + 2] * a / 255);
            }
        }
        else
        {
            for (var i = 3; i < buffer.Length; i += 4)
            {
                buffer[i] = 255;
            }
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(buffer, y * stride, locked.Address + (y * locked.RowBytes), stride);
            }
        }

        _icons.Add(bitmap);
        return bitmap;
    }

    /// <summary>メニュー項目を実行する。ポップアップを閉じてから呼ぶこと。</summary>
    public bool Invoke(int commandId)
    {
        if (_disposed || _menu is null || commandId <= 0)
        {
            return false;
        }

        Logger.Log($"シェルメニュー項目を実行: cmd={commandId}", LogLevel.Debug);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hr = InvokeCommandCore(_menu, _hwnd, verbName: null, commandId - 1, _workingDirectory);
        if (sw.ElapsedMilliseconds > 1000 || hr < 0)
        {
            Logger.Log(
                $"シェルメニュー項目の実行結果: cmd={commandId}, HRESULT=0x{hr:X8}, 所要 {sw.ElapsedMilliseconds}ms"
                + (sw.ElapsedMilliseconds > 1000 ? " (シェル拡張の不調の可能性)" : string.Empty),
                LogLevel.Warning);
        }

        return hr >= 0;
    }

    /// <summary>verb 名を指定して実行する（自前項目からシェルの機能を借りるとき用）。</summary>
    public bool InvokeVerb(string verb)
    {
        if (_disposed || _menu is null)
        {
            return false;
        }

        var hr = InvokeCommandCore(_menu, _hwnd, verb, commandId: 0, _workingDirectory);
        if (hr < 0)
        {
            Logger.Log($"Shell verb \"{verb}\" の実行失敗: HRESULT=0x{hr:X8}", LogLevel.Warning);
        }

        return hr >= 0;
    }

    /// <summary>
    /// 複数選択のプロパティを表示する。IContextMenu 経由だとカスタムプロパティシートを持つ
    /// 特殊フォルダーが混ざったときに S_OK のまま何も出ないことがあるため、エクスプローラー自身が
    /// 使う SHMultiFileProperties へ切り替える。
    /// </summary>
    public unsafe bool TryShowMultiFileProperties()
    {
        if (_disposed || _folder is null || _pidls.Count <= 1)
        {
            return false;
        }

        fixed (nint* pChildren = _children)
        {
            if (_folder.GetUIObjectOf(_hwnd, (uint)_children.Length, (nint)pChildren, IidIDataObject, 0, out var dataPtr) < 0
                || dataPtr == 0)
            {
                return false;
            }

            var hr = SHMultiFileProperties(dataPtr, 0);
            Marshal.Release(dataPtr);
            if (hr < 0)
            {
                Logger.Log($"複数選択のプロパティ表示に失敗: HRESULT=0x{hr:X8}", LogLevel.Warning);
            }

            return hr >= 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Items = [];

        if (_hmenu != 0)
        {
            DestroyMenu(_hmenu);
            _hmenu = 0;
        }

        foreach (var icon in _icons)
        {
            icon.Dispose();
        }

        _icons.Clear();

        // IContextMenu の実体はサードパーティのシェル拡張。GC 任せにせずここで手放す
        ComRelease.Release(_menu);
        ComRelease.Release(_folder);
        _menu = null;
        _folder = null;

        foreach (var pidl in _pidls)
        {
            Marshal.FreeCoTaskMem(pidl);
        }

        _pidls.Clear();
    }

    /// <summary>対象の項目が入っているフォルダー（= 起動するプログラムの作業ディレクトリ）。</summary>
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

    /// <summary>lpDirectory 付きで InvokeCommand する（理由は ShellContextMenuService 側のコメント参照）。</summary>
    private static unsafe int InvokeCommandCore(
        IContextMenu menu,
        nint hwnd,
        string? verbName,
        int commandId,
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
    private struct BitmapStruct
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
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

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(nint pidl, in Guid riid, out nint ppv, out nint ppidlLast);

    [LibraryImport("shell32.dll")]
    private static partial int SHMultiFileProperties(nint pdtobj, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(nint hMenu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemID(nint hMenu, int pos);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteMenu(nint hMenu, uint position, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMenuItemInfo(nint hMenu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfoW info);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(nint handle, int c, nint pv);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint hdc, nint hbm, uint start, uint lines,
        nint bits, ref BitmapInfoHeader header, uint usage);
}

/// <summary>
/// IContextMenu2。「送る」のように WM_INITMENUPOPUP で初めて中身を作るサブメニューがあるため、
/// 中身を吸い出す前にこのインターフェイスで通知する。
/// </summary>
[GeneratedComInterface]
[Guid("000214F4-0000-0000-C000-000000000046")]
internal partial interface IContextMenu2 : IContextMenu
{
    [PreserveSig]
    int HandleMenuMsg(uint msg, nint wParam, nint lParam);
}

/// <summary>IContextMenu3。IContextMenu2 の戻り値付き版で、新しめのシェル拡張はこちらを実装する。</summary>
[GeneratedComInterface]
[Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
internal partial interface IContextMenu3 : IContextMenu2
{
    [PreserveSig]
    int HandleMenuMsg2(uint msg, nint wParam, nint lParam, out nint result);
}
