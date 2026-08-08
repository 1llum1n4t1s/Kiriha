using Kiriha.Models;
using Kiriha.Services;
using Kiriha.ViewModels;
using Xunit;

namespace Kiriha.Tests;

// 🌱 正常系（Happy Path）: 典型的な使い方で期待どおり動くことを保証する基礎テスト。
// @happypath

/// <summary>
/// パス同一性の正本。タブ重複判定・選択復元・フォルダー監視キーがすべてここに依存するため、
/// 「正規形」「Equals と GetHashCode の整合」「辞書コンパレーターとしての集約」の3層で固める。
/// </summary>
public class WindowsPathIdentityHappyTests
{
    [Theory]
    [InlineData(@"C:\Users\IMT\", @"C:\Users\IMT")]
    [InlineData(@"C:\Users\IMT", @"C:\Users\IMT")]
    [InlineData(@"C:\Users\IMT\Pictures\", @"C:\Users\IMT\Pictures")]
    public void 末尾に区切り文字があるとき区切りを除いた正規形になること(string input, string expected)
        => Assert.Equal(expected, WindowsPathIdentity.Normalize(input));

    [Fact]
    public void ドライブルートのとき区切り文字を保ったままになること()
        => Assert.Equal(@"C:\", WindowsPathIdentity.Normalize(@"C:\"));

    [Fact]
    public void 入力がnullまたはComputerPathのとき空文字の正規形になること()
    {
        Assert.Equal("", WindowsPathIdentity.Normalize(null));
        Assert.Equal("", WindowsPathIdentity.Normalize(FileSystemService.ComputerPath));
        Assert.True(WindowsPathIdentity.Instance.Equals(null, FileSystemService.ComputerPath));
    }

    [Fact]
    public void 大小文字と末尾区切りだけが違うとき同じパスと判定すること()
    {
        Assert.True(WindowsPathIdentity.Instance.Equals(@"C:\Temp\Work", @"c:\temp\work\"));
        Assert.True(WindowsPathIdentity.Instance.Equals(@"D:\A\B\", @"D:\A\B"));
    }

    [Fact]
    public void 異なるフォルダーのとき別のパスと判定すること()
    {
        Assert.False(WindowsPathIdentity.Instance.Equals(@"C:\Temp\Work", @"C:\Temp\Work2"));
        Assert.False(WindowsPathIdentity.Instance.Equals(@"C:\Temp", @"D:\Temp"));
    }

    [Fact]
    public void 同一視されるパスのときハッシュ値も一致すること()
    {
        var a = WindowsPathIdentity.Instance.GetHashCode(@"C:\Temp\Work");
        var b = WindowsPathIdentity.Instance.GetHashCode(@"c:\TEMP\work\");
        Assert.Equal(a, b);
    }

    [Fact]
    public void 辞書のコンパレーターに使うとき表記揺れが一つのキーにまとまること()
    {
        var map = new Dictionary<string, int>(WindowsPathIdentity.Instance)
        {
            [@"C:\Temp\Watch"] = 1,
        };
        map[@"c:\temp\watch\"] = 2;

        Assert.Single(map);
        Assert.Equal(2, map[@"C:\TEMP\WATCH"]);
    }
}

/// <summary>一覧の列表示（サイズ・種類・日時・ツールチップ）と、サムネイル完了フラグの契約。</summary>
public class FileSystemEntryHappyTests
{
    private static FileSystemEntry File(string name, long? size = null, DateTime? modified = null)
        => new()
        {
            Name = name,
            DisplayName = Path.GetFileNameWithoutExtension(name),
            FullPath = @"C:\Temp\" + name,
            IsDirectory = false,
            Size = size,
            Modified = modified,
        };

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(5368709120L, "5 GB")]
    public void サイズを渡したとき単位付きの表記を返すこと(long size, string expected)
        => CultureScope.With("", () => Assert.Equal(expected, FileSystemEntry.FormatSize(size)));

    [Fact]
    public void テラバイト級のときGB表記に桁区切りが入ること()
        => CultureScope.With("", () => Assert.Equal("1,024 GB", FileSystemEntry.FormatSize(1099511627776L)));

    [Fact]
    public void サイズ列は種別と上書き指定に応じた表示になること()
        => CultureScope.With("", () =>
        {
            var folder = new FileSystemEntry { Name = "work", DisplayName = "work", FullPath = @"C:\Temp\work", IsDirectory = true };
            var file = File("a.txt", size: 2048);
            var drive = new FileSystemEntry
            {
                Name = "C:", DisplayName = "C:", FullPath = @"C:\", IsDirectory = true, IsDrive = true,
                Size = 999, SizeTextOverride = "空き 1 GB / 2 GB",
            };

            Assert.Equal("", folder.SizeText);
            Assert.Equal("2 KB", file.SizeText);
            Assert.Equal("空き 1 GB / 2 GB", drive.SizeText);
        });

    [Fact]
    public void 種類列は項目種別に応じた日本語表記になること()
    {
        var drive = new FileSystemEntry
        {
            Name = "C:", DisplayName = "C:", FullPath = @"C:\",
            IsDirectory = true, IsDrive = true, DriveFormat = "NTFS",
        };
        var folder = new FileSystemEntry { Name = "work", DisplayName = "work", FullPath = @"C:\Temp\work", IsDirectory = true };

        Assert.Equal("ローカル ディスク (NTFS)", drive.TypeText);
        Assert.Equal("ファイル フォルダー", folder.TypeText);
        Assert.Equal("TXT ファイル", File("memo.txt").TypeText);
        Assert.Equal("ファイル", File("LICENSE").TypeText);
    }

    [Fact]
    public void 日時列は分までの書式になり未設定のとき空文字になること()
        => CultureScope.With("", () =>
        {
            var entry = File("memo.txt", modified: new DateTime(2026, 7, 25, 9, 5, 30));

            Assert.Equal("2026/07/25 09:05", entry.ModifiedText);
            Assert.Equal("", entry.CreatedText);
        });

    [Fact]
    public void ツールチップは名前と種類とサイズと秒付き日時を含むこと()
        => CultureScope.With("", () =>
        {
            var entry = File("memo.txt", size: 2048, modified: new DateTime(2026, 7, 25, 9, 5, 30));

            Assert.Equal(
                "memo.txt\n種類: TXT ファイル\nサイズ: 2 KB\n更新日時: 2026/07/25 09:05:30",
                entry.RowTooltip);
        });

    [Fact]
    public void サイズと日時がないときツールチップは名前と種類だけになること()
    {
        var entry = new FileSystemEntry { Name = "work", DisplayName = "work", FullPath = @"C:\Temp\work", IsDirectory = true };
        Assert.Equal("work\n種類: ファイル フォルダー", entry.RowTooltip);
    }

    [Fact]
    public void MarkThumbnailFinalを呼ぶとサムネイル完了扱いになること()
    {
        var entry = File("a.jpg");

        Assert.False(entry.IsThumbnailFinal);
        Assert.False(entry.HasThumbnail);

        entry.MarkThumbnailFinal();

        Assert.True(entry.IsThumbnailFinal);
        Assert.False(entry.HasThumbnail);
    }

    [Fact]
    public void 読み直しでサイズや日時が変わっても同じ行として扱われること()
    {
        // 行を作り直すと一覧ごと差し替わって点滅するため、値の変化は作り直す理由にしない
        var previous = File("memo.txt", size: 2048, modified: new DateTime(2026, 7, 25, 9, 5, 0));
        var changed = File("memo.txt", size: 4096, modified: new DateTime(2026, 7, 25, 9, 6, 0));

        Assert.True(previous.IsSameRowAs(changed));
    }

    [Fact]
    public void 種別や表示名が変わったときは同じ行として扱わないこと()
    {
        var previous = File("memo.txt", size: 2048);
        var asFolder = new FileSystemEntry
        {
            Name = "memo.txt", DisplayName = "memo", FullPath = @"C:\Temp\memo.txt", IsDirectory = true,
        };
        // 拡張子表示の切り替えで表示名が変わったときは作り直す
        var withExtension = new FileSystemEntry
        {
            Name = "memo.txt", DisplayName = "memo.txt", FullPath = @"C:\Temp\memo.txt", IsDirectory = false,
        };

        Assert.False(previous.IsSameRowAs(asFolder));
        Assert.False(previous.IsSameRowAs(withExtension));
    }

    [Fact]
    public void 読み直した内容を写すと表示が更新され中身の変化が返ること()
    {
        var entry = File("memo.txt", size: 2048, modified: new DateTime(2026, 7, 25, 9, 5, 0));
        var fresh = File("memo.txt", size: 4096, modified: new DateTime(2026, 7, 25, 9, 6, 0));
        fresh.IsCut = true;

        var contentChanged = entry.UpdateFrom(fresh);

        Assert.True(contentChanged);
        Assert.Equal(4096, entry.Size);
        Assert.Equal(new DateTime(2026, 7, 25, 9, 6, 0), entry.Modified);
        Assert.True(entry.IsCut);
    }

    [Fact]
    public void 中身が変わっていない読み直しでは変化なしと返ること()
    {
        var entry = File("memo.txt", size: 2048, modified: new DateTime(2026, 7, 25, 9, 5, 0));
        var fresh = File("memo.txt", size: 2048, modified: new DateTime(2026, 7, 25, 9, 5, 0));

        Assert.False(entry.UpdateFrom(fresh));
    }

    [Fact]
    public void サムネイルを無効化すると読み直し対象に戻ること()
    {
        var entry = File("a.jpg");
        entry.MarkThumbnailFinal();

        entry.InvalidateThumbnail();

        Assert.False(entry.IsThumbnailFinal);
    }

    [Fact]
    public void 切り取りと隠し属性のとき行の不透明度が下がること()
    {
        var normal = File("a");
        var hidden = new FileSystemEntry { Name = "b", DisplayName = "b", FullPath = @"C:\Temp\b", IsDirectory = false, IsHidden = true };
        var cut = new FileSystemEntry
        {
            Name = "c", DisplayName = "c", FullPath = @"C:\Temp\c", IsDirectory = false, IsHidden = true, IsCut = true,
        };

        Assert.Equal(1.0, normal.RowOpacity);
        Assert.Equal(0.6, hidden.RowOpacity);
        Assert.Equal(0.45, cut.RowOpacity);
    }
}

/// <summary>一覧で文字を打ったときの先頭一致ジャンプ（エクスプローラー互換）の移動先。</summary>
public class TypeAheadHappyTests
{
    // C:\Windows の並びを模した一覧（表示名の先頭一致で移動する）
    private static readonly IReadOnlyList<FileSystemEntry> Entries =
    [
        Folder("Fonts"), Folder("security"), Folder("ServiceProfiles"), Folder("servicing"),
        Folder("Setup"), Folder("SoftwareDistribution"), Folder("System32"), Folder("Web"),
    ];

    private static FileSystemEntry Folder(string name) => new()
    {
        Name = name,
        DisplayName = name,
        FullPath = @"C:\Windows\" + name,
        IsDirectory = true,
    };

    private static int Find(string prefix, int currentIndex)
        => Kiriha.ViewModels.TabViewModel.FindTypeAheadIndex(Entries, prefix, currentIndex);

    [Fact]
    public void SからOと打つと先頭一致でSoftwareDistributionへ移ること()
    {
        // S → 最初の s 始まり（security）へ移り、続けて O を打つと "so" の一致先へ進む
        var afterS = Find("s", -1);
        Assert.Equal("security", Entries[afterS].DisplayName);

        var afterSo = Find("so", afterS);
        Assert.Equal("SoftwareDistribution", Entries[afterSo].DisplayName);
    }

    [Fact]
    public void 打ち足した文字列が今の行にも一致するならその場に留まること()
    {
        // SoftwareDistribution を選んだ状態で "sof" まで打っても動かない（絞り込みの継続）
        var index = Array.FindIndex(Entries.ToArray(), e => e.DisplayName == "SoftwareDistribution");
        Assert.Equal(index, Find("sof", index));
    }

    [Fact]
    public void 同じ文字を続けて打つと次の候補へ送られること()
    {
        Assert.Equal("security", Entries[Find("s", -1)].DisplayName);
        Assert.Equal("ServiceProfiles", Entries[Find("ss", 1)].DisplayName);
        Assert.Equal("servicing", Entries[Find("sss", 2)].DisplayName);
    }

    [Fact]
    public void 一文字は常に次の候補へ進み末尾まで行くと先頭へ回り込むこと()
    {
        // System32（最後の s 始まり）から "s" を打つと security へ戻る
        var last = Array.FindIndex(Entries.ToArray(), e => e.DisplayName == "System32");
        Assert.Equal("security", Entries[Find("s", last)].DisplayName);
    }

    [Fact]
    public void 大文字小文字を区別せずに一致すること()
        => Assert.Equal("SoftwareDistribution", Entries[Find("SOFT", -1)].DisplayName);

    [Fact]
    public void 該当がないときは移動しないこと()
    {
        Assert.Equal(-1, Find("zz", -1));
        Assert.Equal(-1, Find("", 0));
        Assert.Equal(-1, Kiriha.ViewModels.TabViewModel.FindTypeAheadIndex([], "s", -1));
    }
}

/// <summary>タブ詳細操作メニューの定義。起動時検証と同じ不変条件をテストでも固定する。</summary>
public class ContextActionCatalogHappyTests
{
    [Fact]
    public void カタログは重複のない20件の有効な定義であること()
    {
        var all = ContextActionCatalog.All;

        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Id));
            Assert.False(string.IsNullOrWhiteSpace(x.Title));
            Assert.Equal("タブ", x.Category);
            Assert.Equal(ActionScope.Tab, x.Scope);
        });
    }

    [Fact]
    public void ForにTabスコープを渡すとカタログ全件を定義順で返すこと()
    {
        var tabActions = ContextActionCatalog.For(ActionScope.Tab).ToList();

        Assert.Equal(ContextActionCatalog.All.Select(x => x.Id), tabActions.Select(x => x.Id));
        Assert.Equal("tab.close-left", tabActions[0].Id);
    }
}

/// <summary>ファイル操作エラーの日本語化。</summary>
public class FileOperationServiceHappyTests
{
    [Theory]
    [InlineData(2, "パスが見つかりません")]
    [InlineData(3, "パスが見つかりません")]
    [InlineData(5, "アクセスが拒否されました")]
    [InlineData(19, "書き込み禁止です")]
    [InlineData(32, "他のプロセスが使用中です")]
    [InlineData(80, "同じ名前のファイルが既に存在します")]
    [InlineData(112, "ディスクの空き領域が不足しています")]
    [InlineData(123, "ファイル名に使えない文字が含まれています")]
    [InlineData(206, "パスが長すぎます")]
    public void 既知のエラーコードのとき対応する日本語の説明を返すこと(int code, string expected)
        => Assert.Equal(expected, FileOperationService.DescribeError(code));

    /// <summary>
    /// 旧 SHFileOperationW 時代のコピーエンジン固有コード（DE_SAMEFILE=113 / DE_INVALIDFILES=124）は
    /// 載せない。IFileOperation はそれらを HRESULT で返すため素の値は届かず、逆に Win32 の 113 / 124 が
    /// 同じ数値で来たときに誤った説明を出してしまうため。
    /// </summary>
    [Theory]
    [InlineData(113)]
    [InlineData(124)]
    public void コピーエンジン固有コードと衝突するWin32番号は説明を返さないこと(int code)
        => Assert.Equal(string.Empty, FileOperationService.DescribeError(code));

    [Fact]
    public void 未知のエラーコードのとき空文字を返すこと()
    {
        Assert.Equal(string.Empty, FileOperationService.DescribeError(0));
        Assert.Equal(string.Empty, FileOperationService.DescribeError(1234));
    }
}

/// <summary>フォルダー列挙。実際に一時ディレクトリを作って検証する（実ユーザーのデータには触れない）。</summary>
public class FileSystemServiceHappyTests
{
    [Fact]
    public void 通常のフォルダーを列挙するとフォルダーがファイルより先に並ぶこと()
    {
        using var temp = new TempDirectory("enumerate");
        temp.CreateSubDirectory("sub1");
        temp.CreateSubDirectory("sub2");
        temp.CreateFile("memo.txt", size: 10);

        var entries = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowExtensions = true });

        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { true, true, false }, entries.Select(x => x.IsDirectory));
        Assert.Equal("memo.txt", entries[2].Name);
        Assert.Equal(10L, entries[2].Size);
        Assert.Equal(temp.Combine("memo.txt"), entries[2].FullPath);
        Assert.Null(entries[0].Size);
    }

    [Fact]
    public void ShowHiddenがONのときだけ隠し項目を含めること()
    {
        using var temp = new TempDirectory("hidden");
        temp.CreateFile("visible.txt");
        temp.CreateFile("secret.txt", hidden: true);
        temp.CreateSubDirectory("hiddenDir", hidden: true);

        var without = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowHidden = false, ShowExtensions = true });
        var with = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowHidden = true, ShowExtensions = true });

        Assert.Equal(new[] { "visible.txt" }, without.Select(x => x.Name));
        Assert.Equal(
            new[] { "hiddenDir", "secret.txt", "visible.txt" },
            with.Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(with.Single(x => x.Name == "secret.txt").IsHidden);
    }

    [Fact]
    public void ShowExtensionsがOFFのとき表示名だけ拡張子なしになること()
    {
        using var temp = new TempDirectory("extension");
        temp.CreateFile("report.final.txt");

        var hiddenExt = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowExtensions = false }).Single();
        var shownExt = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowExtensions = true }).Single();

        Assert.Equal("report.final.txt", hiddenExt.Name);
        Assert.Equal("report.final", hiddenExt.DisplayName);
        Assert.Equal("report.final.txt", shownExt.DisplayName);
    }

    [Fact]
    public void 空のフォルダーを列挙すると空のリストを返すこと()
    {
        using var temp = new TempDirectory("empty");

        var entries = FileSystemService.GetEntries(temp.Root, new ShellOptions());

        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public void ComputerPathを渡すとドライブ一覧を返すこと()
        => CultureScope.With("", () =>
        {
            var entries = FileSystemService.GetEntries(FileSystemService.ComputerPath, new ShellOptions());

            Assert.NotEmpty(entries);
            Assert.All(entries, x =>
            {
                Assert.True(x.IsDrive);
                Assert.True(x.IsDirectory);
                // 「空き領域 180 GB/475 GB」形式。単位付きの数値は Windows の
                // StrFormatByteSize が作るのでここでは書式だけ確かめる。
                Assert.StartsWith("空き領域 ", x.SizeText);
                Assert.Contains("/", x.SizeText);
                Assert.InRange(x.DriveUsedPercent, 0.0, 100.0);
            });
            Assert.Contains(
                entries,
                x => string.Equals(x.FullPath, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        });
}

/// <summary>左ペインの表示モードの保存値解決（旧 bool フラグからの移行を含む）。</summary>
public class SidebarModeHappyTests
{
    [Theory]
    [InlineData("QuickAccess", SidebarMode.QuickAccess)]
    [InlineData("Tree", SidebarMode.Tree)]
    [InlineData("Bookmarks", SidebarMode.Bookmarks)]
    public void 保存済みのモード名はそのまま解決される(string saved, SidebarMode expected)
    {
        Assert.Equal(expected, SidebarModes.Resolve(saved, legacyShowTree: false));
        // 保存値がある限り旧フラグは見ない
        Assert.Equal(expected, SidebarModes.Resolve(saved, legacyShowTree: true));
    }

    [Theory]
    [InlineData(true, SidebarMode.Tree)]
    [InlineData(false, SidebarMode.QuickAccess)]
    public void 未設定なら旧ツリー表示フラグから移行する(bool legacyShowTree, SidebarMode expected)
    {
        Assert.Equal(expected, SidebarModes.Resolve("", legacyShowTree));
        Assert.Equal(expected, SidebarModes.Resolve(null, legacyShowTree));
    }

    [Theory]
    [InlineData("5")]        // Enum.TryParse は数値文字列も通してしまう
    [InlineData("Favorites")]
    public void 壊れた保存値は未設定と同じ扱いになる(string saved)
    {
        Assert.Equal(SidebarMode.QuickAccess, SidebarModes.Resolve(saved, legacyShowTree: false));
        Assert.Equal(SidebarMode.Tree, SidebarModes.Resolve(saved, legacyShowTree: true));
    }
}

/// <summary>絵文字アイコンセットの表示文字。お気に入りとファイル一覧が同じ解決を共有する。</summary>
public class EmojiIconHappyTests
{
    [Theory]
    [InlineData("C:\\", true, true, "💾")]
    [InlineData("dev", true, false, "📁")]
    [InlineData("photo.PNG", false, false, "🖼")]
    [InlineData("clip.mp4", false, false, "🎬")]
    [InlineData("setup.exe", false, false, "⚙")]
    [InlineData("CHANGELOG.md", false, false, "📄")]
    [InlineData("Program.cs", false, false, "📜")]
    [InlineData("なにか.unknown", false, false, "📄")]
    public void 種別と拡張子から絵文字を決める(string name, bool isDirectory, bool isDrive, string expected)
    {
        Assert.Equal(expected, FileSystemEntry.ResolveEmojiIcon(name, isDirectory, isDrive));
    }

    [Fact]
    public void ドライブとフォルダーは拡張子より優先される()
    {
        // 名前に拡張子があってもフォルダーならフォルダーのアイコンになる
        Assert.Equal("📁", FileSystemEntry.ResolveEmojiIcon("bundle.app", isDirectory: true));
        Assert.Equal("💾", FileSystemEntry.ResolveEmojiIcon("D:\\", isDirectory: true, isDrive: true));
    }
}

/// <summary>お気に入りの一括削除（左ペインで複数選択して削除する経路の本体）。</summary>
public class BookmarkBulkRemoveHappyTests
{
    private static BookmarkNode Link(string name) => new() { Name = name, Path = $@"C:\{name}" };

    private static BookmarkNode Folder(string name, params BookmarkNode[] children)
        => new() { Name = name, Children = [.. children] };

    [Fact]
    public void 選択した項目だけがまとめて消える()
    {
        var a = Link("a");
        var b = Link("b");
        var c = Link("c");
        List<BookmarkNode> list = [a, b, c];

        Assert.True(MainWindowViewModel.RemoveBookmarksRecursive(list, [a, c]));
        Assert.Equal([b], list);
    }

    [Fact]
    public void グループフォルダーの中の項目も消える()
    {
        var inner = Link("inner");
        var outer = Link("outer");
        var group = Folder("group", inner);
        List<BookmarkNode> list = [group, outer];

        Assert.True(MainWindowViewModel.RemoveBookmarksRecursive(list, [inner]));
        Assert.Equal([group, outer], list);
        Assert.Empty(group.Children!);
    }

    [Fact]
    public void 親フォルダーと中の項目を同時に選んでも親ごと消える()
    {
        var inner = Link("inner");
        var group = Folder("group", inner);
        var keep = Link("keep");
        List<BookmarkNode> list = [group, keep];

        // 親が先に消えると子の削除は空振りするが、結果は「親ごと消える」で変わらない
        Assert.True(MainWindowViewModel.RemoveBookmarksRecursive(list, [group, inner]));
        Assert.Equal([keep], list);

        // 逆順（子が先）でも同じ結果になること
        var inner2 = Link("inner2");
        var group2 = Folder("group2", inner2);
        List<BookmarkNode> list2 = [group2];
        Assert.True(MainWindowViewModel.RemoveBookmarksRecursive(list2, [inner2, group2]));
        Assert.Empty(list2);
    }

    [Fact]
    public void 登録されていない項目だけならfalseを返す()
    {
        var a = Link("a");
        List<BookmarkNode> list = [a];

        // 呼び出し側はこれを見て settings.json への保存を省く
        Assert.False(MainWindowViewModel.RemoveBookmarksRecursive(list, [Link("stranger")]));
        Assert.Equal([a], list);
    }
}

/// <summary>お気に入りの並べ替え（名前順 / パス名順 × 昇順 / 降順）。</summary>
public class BookmarkSortHappyTests
{
    private static BookmarkNode Link(string name, string? path = null)
        => new() { Name = name, Path = path ?? $@"C:\{name}" };

    private static BookmarkNode Folder(string name, params BookmarkNode[] children)
        => new() { Name = name, Children = [.. children] };

    // 表示（＝並べ替えの基準）はリンク項目なら実体名。node.Name はグループフォルダーだけが持つ
    private static string[] Names(List<BookmarkNode> list) => [.. list.Select(n => n.DisplayName)];

    [Fact]
    public void 名前の昇順と降順が逆順になる()
    {
        List<BookmarkNode> list = [Link("c"), Link("a"), Link("b")];

        Assert.Equal(
            ["a", "b", "c"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: false, ascending: true)));
        Assert.Equal(
            ["c", "b", "a"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: false, ascending: false)));
    }

    [Fact]
    public void パス名順は名前ではなくパスで並ぶ()
    {
        // 名前順なら a → b だが、パス名順では z\a より m\b が先に来る
        List<BookmarkNode> list = [Link("a", @"C:\z\a"), Link("b", @"C:\m\b")];

        Assert.Equal(
            ["b", "a"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: true, ascending: true)));
        Assert.Equal(
            ["a", "b"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: true, ascending: false)));
    }

    [Fact]
    public void グループフォルダーは降順でも先頭に集まる()
    {
        // 降順で反転させるとフォルダーが下に落ちてツリーの見え方が変わってしまうため、
        // フォルダー優先の規則だけは昇順・降順で共通にしてある
        List<BookmarkNode> list = [Link("b"), Folder("g1"), Link("a"), Folder("g2")];

        Assert.Equal(
            ["g1", "g2", "a", "b"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: false, ascending: true)));
        Assert.Equal(
            ["g2", "g1", "b", "a"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: false, ascending: false)));
    }

    [Fact]
    public void グループフォルダーの中も同じ向きで並ぶ()
    {
        var group = Folder("g", Link("y"), Link("x"));
        List<BookmarkNode> list = [group];

        MainWindowViewModel.SortBookmarkList(list, byPath: false, ascending: false);
        Assert.Equal(["y", "x"], Names(group.Children!));
    }

    [Fact]
    public void 名前順はリンクに残った独自名ではなく実体名で並ぶ()
    {
        // 出荷済みの settings.json には独自に付けた名前が残っているが、表示も並べ替えも実体名で行う
        List<BookmarkNode> list = [Link("zzz", @"C:\a"), Link("aaa", @"C:\z")];

        Assert.Equal(
            ["a", "z"],
            Names(MainWindowViewModel.SortBookmarkList(list, byPath: false, ascending: true)));
    }
}

/// <summary>お気に入りの表示名は実体から導く（独自に保持しない）。</summary>
public class BookmarkDisplayNameHappyTests
{
    [Fact]
    public void リンク項目はパスの末尾を表示名にする()
    {
        Assert.Equal("Kiriha", new BookmarkNode { Name = "古い名前", Path = @"C:\dev\Kiriha" }.DisplayName);
        Assert.Equal("memo.txt", new BookmarkNode { Path = @"C:\dev\memo.txt" }.DisplayName);
    }

    [Fact]
    public void 末尾の区切りは無視される()
    {
        Assert.Equal("Kiriha", new BookmarkNode { Path = @"C:\dev\Kiriha\" }.DisplayName);
    }

    [Fact]
    public void ドライブ直下はパスをそのまま出す()
    {
        // ファイル名部分が空になるので、そのままだと名前が消える
        Assert.Equal(@"C:\", new BookmarkNode { Path = @"C:\" }.DisplayName);
    }

    [Fact]
    public void 実体を持たないノードは保持した名前を使う()
    {
        // グループ分けフォルダー（Path なし）と「PC」（Path が空文字）
        Assert.Equal("仕事", new BookmarkNode { Name = "仕事", Children = [] }.DisplayName);
        Assert.Equal("PC", new BookmarkNode { Name = "PC", Path = "" }.DisplayName);
    }

    [Fact]
    public void パスを付け替えると表示名も変わる()
    {
        var node = new BookmarkNode { Path = @"C:\dev\old" };
        var changed = false;
        node.PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(BookmarkNode.DisplayName);

        node.Path = @"C:\dev\new";

        Assert.Equal("new", node.DisplayName);
        Assert.True(changed);
    }
}

/// <summary>
/// お気に入りが settings.json を往復しても壊れない。
/// </summary>
/// <remarks>
/// <see cref="BookmarkNode.Path"/> を <c>[ObservableProperty]</c> にした瞬間、MVVM Toolkit が生成した
/// プロパティを System.Text.Json のジェネレーターが見られず、保存時に Path が丸ごと消えた（2026-08-08、
/// 開発機の登録済みお気に入りを実際に失った）。ソースジェネレーター同士は互いの出力を見られないので、
/// 保存対象のプロパティを生成に頼ると同じことが起きる。ここはその番人。
/// </remarks>
[Collection(AppStorageCollection.Name)]
public class BookmarkNodeSerializationTests
{
    [Fact]
    public void 保存して読み直してもリンク先パスが残る()
    {
        using var scope = new AppStorageScope();
        var settings = new AppSettings
        {
            Bookmarks =
            [
                new BookmarkNode { Path = @"C:\dev\Kiriha" },
                new BookmarkNode { Name = "PC", Path = "" },
                new BookmarkNode { Name = "仕事", Children = [new BookmarkNode { Path = @"C:\dev\Komorebi" }] },
            ],
        };

        SettingsService.Save(settings);
        var loaded = SettingsService.Load();

        Assert.Equal(@"C:\dev\Kiriha", loaded.Bookmarks[0].Path);
        Assert.Equal("Kiriha", loaded.Bookmarks[0].DisplayName);
        Assert.Equal("", loaded.Bookmarks[1].Path);
        Assert.Equal("PC", loaded.Bookmarks[1].DisplayName);
        Assert.Equal(@"C:\dev\Komorebi", loaded.Bookmarks[2].Children![0].Path);
    }
}

/// <summary>リネームでお気に入りの登録パスが追従する。</summary>
public class BookmarkPathFollowHappyTests
{
    [Fact]
    public void 同じパスの登録が新しいパスへ付け替わる()
    {
        var node = new BookmarkNode { Path = @"C:\dev\old" };
        List<BookmarkNode> list = [node];

        Assert.True(MainWindowViewModel.UpdateBookmarkPaths(list, @"C:\dev\old", @"C:\dev\new"));
        Assert.Equal(@"C:\dev\new", node.Path);
    }

    [Fact]
    public void 親フォルダーのリネームで配下の登録も付け替わる()
    {
        var child = new BookmarkNode { Path = @"C:\dev\old\sub\deep" };
        List<BookmarkNode> list = [new BookmarkNode { Name = "g", Children = [child] }];

        Assert.True(MainWindowViewModel.UpdateBookmarkPaths(list, @"C:\dev\old", @"C:\dev\new"));
        Assert.Equal(@"C:\dev\new\sub\deep", child.Path);
    }

    [Fact]
    public void 大文字小文字は同じパスとして扱う()
    {
        var node = new BookmarkNode { Path = @"c:\DEV\Old" };
        List<BookmarkNode> list = [node];

        Assert.True(MainWindowViewModel.UpdateBookmarkPaths(list, @"C:\dev\old", @"C:\dev\new"));
        Assert.Equal(@"C:\dev\new", node.Path);
    }

    [Fact]
    public void 名前が前方一致しただけの別フォルダーは巻き込まない()
    {
        // C:\dev\old2 は C:\dev\old の配下ではない
        var sibling = new BookmarkNode { Path = @"C:\dev\old2" };
        List<BookmarkNode> list = [sibling];

        Assert.False(MainWindowViewModel.UpdateBookmarkPaths(list, @"C:\dev\old", @"C:\dev\new"));
        Assert.Equal(@"C:\dev\old2", sibling.Path);
    }

    [Fact]
    public void 該当が無ければ変更なしを返す()
    {
        List<BookmarkNode> list = [new BookmarkNode { Path = @"C:\other" }, new BookmarkNode { Name = "PC", Path = "" }];

        Assert.False(MainWindowViewModel.UpdateBookmarkPaths(list, @"C:\dev\old", @"C:\dev\new"));
    }
}
