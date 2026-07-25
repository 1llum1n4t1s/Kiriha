using Kiriha.Models;
using Kiriha.Services;
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
                Assert.StartsWith("空き ", x.SizeText);
                Assert.InRange(x.DriveUsedPercent, 0.0, 100.0);
            });
            Assert.Contains(
                entries,
                x => string.Equals(x.FullPath, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        });
}
