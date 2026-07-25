using System.Collections.Concurrent;
using System.Text.Json;
using Kiriha.Models;
using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

// 😈 嫌がらせテスト（Adversarial）: 境界値・並行性・環境異常でアプリの契約を殴る。
// 実ユーザーのデータ・レジストリ・OS 設定は一切変更しない（一時ディレクトリと退避レジストリキーのみ）。
// @adversarial

// ============================================================
// 🗡️ 境界値・極端入力（Boundary Assault）
// ============================================================

/// <summary>サイズ表記の単位境界・丸め・カルチャ依存。@category=boundary</summary>
public class FormatSizeBoundaryTests
{
    /// @severity=high
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    public void サイズ書式は単位の境界値で切り替わること(long size, string expected)
        => CultureScope.With("", () => Assert.Equal(expected, FileSystemEntry.FormatSize(size)));

    /// @severity=high
    /// @description 端数は切り捨て。四捨五入すると 1048575 B（1023.999… KB）が「1,024 KB」になり
    /// 次の単位の 1 MB と区別できなくなるため、上位単位の値に見えてはいけない
    [Theory]
    [InlineData(1048575L, "1,023.9 KB")]
    [InlineData(1073741823L, "1,023.9 MB")]
    public void 単位境界の直前は切り捨てられ上位単位の値に見えないこと(long size, string expected)
        => CultureScope.With("", () => Assert.Equal(expected, FileSystemEntry.FormatSize(size)));

    /// @severity=med
    [Theory]
    [InlineData(1099511627776L, "1,024 GB")]
    [InlineData(1125899906842624L, "1,048,576 GB")]
    [InlineData(long.MaxValue, "8,589,934,592 GB")]
    public void テラバイト級とlong最大値でも桁区切り付きGB表記になること(long size, string expected)
        => CultureScope.With("", () => Assert.Equal(expected, FileSystemEntry.FormatSize(size)));

    /// @severity=low
    /// @description 数値部分は OS（カルチャ）の書式に従う仕様。de-DE では小数点と桁区切りが入れ替わる
    [Fact]
    public void サイズ書式は現在のカルチャの数値書式に従うこと()
        => CultureScope.With("de-DE", () =>
        {
            Assert.Equal("1.023 B", FileSystemEntry.FormatSize(1023));
            Assert.Equal("1,5 KB", FileSystemEntry.FormatSize(1536));
        });
}

/// <summary>パス正規化の極端入力。ここが変わるとタブ重複判定と監視キーが同時に壊れる。@category=boundary</summary>
public class WindowsPathIdentityBoundaryTests
{
    /// @severity=high
    [Fact]
    public void 空文字とnullは空文字へ正規化されること()
    {
        Assert.Equal("", WindowsPathIdentity.Normalize(null));
        Assert.Equal("", WindowsPathIdentity.Normalize(""));
        Assert.Equal("", WindowsPathIdentity.Normalize(FileSystemService.ComputerPath));
    }

    /// @severity=high
    /// @description 区切りを \ へ統一 → 連続区切りを畳む → 末尾区切りを除去。ルートの区切りは維持する
    [Theory]
    [InlineData(@"C:\Users\", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\Users")]
    [InlineData("C:/Users/", @"C:\Users")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", "C:")]
    [InlineData("C:/", @"C:\")]
    [InlineData(@"C:\Users\\", @"C:\Users")]
    [InlineData(@"C:\a\\\b\", @"C:\a\b")]
    [InlineData("C:/a//b/", @"C:\a\b")]
    public void 区切りを統一して連続区切りを畳み末尾区切りを除去すること(string input, string expected)
        => Assert.Equal(expected, WindowsPathIdentity.Normalize(input));

    /// @severity=med
    [Fact]
    public void UNC共有ルートは末尾区切りが除去されること()
    {
        Assert.Equal(@"\\server\share", WindowsPathIdentity.Normalize(@"\\server\share\"));
        Assert.Equal(@"\\server\share", WindowsPathIdentity.Normalize(@"\\server\share"));
        Assert.True(WindowsPathIdentity.Instance.Equals(@"\\server\share\", @"\\SERVER\Share"));
    }

    /// @severity=med
    [Fact]
    public void nullと空文字とComputerPathは等しいこと()
    {
        var comparer = WindowsPathIdentity.Instance;
        Assert.True(comparer.Equals(null, ""));
        Assert.True(comparer.Equals(null, null));
        Assert.True(comparer.Equals("", FileSystemService.ComputerPath));
        Assert.False(comparer.Equals(null, @"C:\"));
    }

    /// @severity=high
    /// @description Path.TrimEndingDirectorySeparator はルートをトリムしないため別扱いになる（現挙動の固定）
    [Fact]
    public void ドライブルートは末尾区切りの有無で別扱いになること()
        => Assert.False(WindowsPathIdentity.Instance.Equals(@"C:\", "C:"));

    /// @severity=high
    [Theory]
    [InlineData(@"C:\Users\Public", @"c:\users\public")]
    [InlineData(@"C:\Users\", @"C:\USERS")]
    [InlineData(@"D:\Photo\", @"d:\photo\")]
    public void 大小文字と末尾区切りの差を無視して同一視されること(string left, string right)
    {
        var comparer = WindowsPathIdentity.Instance;
        Assert.True(comparer.Equals(left, right));
        Assert.Equal(comparer.GetHashCode(left), comparer.GetHashCode(right));
    }

    /// @severity=high
    /// @description 同じフォルダーを指す表記揺れ（区切り種類・連続区切り）は同一視され、ハッシュも一致すること。
    /// 同一視されないと同じフォルダーが別タブ・別監視キー・別フォルダー別設定として扱われてしまう
    [Theory]
    [InlineData("C:/Users", @"C:\Users")]
    [InlineData(@"C:\Users\\", @"C:\Users")]
    [InlineData("C:/Users/Public/", @"C:\Users\Public")]
    [InlineData(@"C:\a\\b", "c:/A/B/")]
    public void 区切り種類の差と連続区切りも同一視されること(string left, string right)
    {
        var comparer = WindowsPathIdentity.Instance;
        Assert.True(comparer.Equals(left, right));
        Assert.Equal(comparer.GetHashCode(left), comparer.GetHashCode(right));
    }

    /// @severity=med
    /// @description UNC の先頭 \\ は意味を持つので畳まない
    [Fact]
    public void UNCの先頭の二重区切りは畳まれないこと()
    {
        Assert.Equal(@"\\server\share", WindowsPathIdentity.Normalize(@"\\server\\share\"));
        Assert.Equal(@"\\server\share", WindowsPathIdentity.Normalize("//server/share/"));
    }
}

/// <summary>エラーコード変換の極端入力。@category=boundary</summary>
public class DescribeErrorBoundaryTests
{
    /// @severity=med
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(0x7D)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void 未知のエラーコードは空文字を返すこと(int code)
        => Assert.Equal(string.Empty, FileOperationService.DescribeError(code));

    /// @severity=med
    /// @description エラーコードはすべて Win32 エラー番号として解釈される（0x70 = 112 = ERROR_DISK_FULL）。
    /// 旧コピーエンジン固有コードを載せていた頃は 0x71 / 0x7C が Win32 の 113 / 124 と衝突していた
    [Fact]
    public void エラーコードはWin32エラー番号として解釈されること()
    {
        Assert.Equal("ディスクの空き領域が不足しています", FileOperationService.DescribeError(0x70));
        Assert.Equal(string.Empty, FileOperationService.DescribeError(0x71));
        Assert.Equal(string.Empty, FileOperationService.DescribeError(0x7C));
    }
}

/// <summary>種類表示の拡張子まわりの極端入力。@category=boundary</summary>
public class TypeTextBoundaryTests
{
    private static FileSystemEntry FileEntry(string name) => new()
    {
        Name = name,
        DisplayName = name,
        FullPath = @"C:\dummy\" + name,
        IsDirectory = false,
    };

    /// @severity=high
    [Theory]
    [InlineData("README", "ファイル")]
    [InlineData(".", "ファイル")]
    [InlineData("..", "ファイル")]
    [InlineData("file.", "ファイル")]
    [InlineData("archive.tar.gz", "GZ ファイル")]
    [InlineData(".gitignore", "GITIGNORE ファイル")]
    [InlineData("写真.テキスト", "テキスト ファイル")]
    [InlineData("memo.TxT", "TXT ファイル")]
    public void 種類表示は拡張子の境界入力を扱えること(string name, string expected)
        => Assert.Equal(expected, FileEntry(name).TypeText);

    /// @severity=med
    [Theory]
    [InlineData(null, "ローカル ディスク")]
    [InlineData("", "ローカル ディスク")]
    [InlineData("NTFS", "ローカル ディスク (NTFS)")]
    public void ドライブの種類表示はDriveFormatの有無で切り替わること(string? format, string expected)
    {
        var entry = new FileSystemEntry
        {
            Name = @"C:\", DisplayName = "ローカル ディスク (C:)", FullPath = @"C:\",
            IsDirectory = true, IsDrive = true, DriveFormat = format,
        };
        Assert.Equal(expected, entry.TypeText);
    }

    /// @severity=low
    [Fact]
    public void ドライブ判定はフォルダーや拡張子より優先されること()
    {
        var entry = new FileSystemEntry
        {
            Name = "weird.txt", DisplayName = "weird.txt", FullPath = @"Z:\",
            IsDirectory = true, IsDrive = true, DriveFormat = "exFAT",
        };
        Assert.Equal("ローカル ディスク (exFAT)", entry.TypeText);
    }
}

// ============================================================
// 🌪️ 環境異常（Environmental Chaos）
// ============================================================

/// <summary>
/// 画像読み込みの入口。ネイティブ境界を越える例外でプロセスが即死した実バグの対策コードなので、
/// 「どんな I/O 失敗でも例外を投げず null を返す」ことが最重要契約。@category=chaos
/// </summary>
public class ImageDecodeServiceChaosTests
{
    /// @severity=high
    [Fact]
    public void 存在しないファイルでも例外を投げずnullを返すこと()
    {
        using var temp = new TempDirectory("imagedecode-missing");

        Assert.Null(ImageDecodeService.TryReadAllBytes(temp.Combine("存在しない画像.jpg")));
    }

    /// @severity=med
    [Fact]
    public void ディレクトリを指定しても例外を投げずnullを返すこと()
    {
        using var temp = new TempDirectory("imagedecode-directory");

        Assert.Null(ImageDecodeService.TryReadAllBytes(temp.Root));
    }

    /// @severity=high
    /// @description IOException 経路だけは 150ms 待って1回だけ読み直す契約
    [Fact]
    public void 排他ロック中のファイルは再試行してからnullを返すこと()
    {
        using var temp = new TempDirectory("imagedecode-locked");
        var locked = temp.Combine("ロック中.png");
        File.WriteAllBytes(locked, [1, 2, 3, 4]);

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = ImageDecodeService.TryReadAllBytes(locked);
        stopwatch.Stop();

        Assert.Null(bytes);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 100,
            $"再試行の待ち時間が入っていません: {stopwatch.ElapsedMilliseconds}ms");
    }

    /// @severity=med
    [Fact]
    public void キャンセル済みトークンでは再試行を待たずにnullを返すこと()
    {
        using var temp = new TempDirectory("imagedecode-cancelled");
        var locked = temp.Combine("ロック中.png");
        File.WriteAllBytes(locked, [1, 2, 3, 4]);

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = ImageDecodeService.TryReadAllBytes(locked, cts.Token);
        stopwatch.Stop();

        Assert.Null(bytes);
        // しきい値は再試行待ち 150ms の直下に置く。マージンを詰めすぎると、負荷の高いマシンでは
        // スレッドのスケジューリング待ちだけで超過して偽陽性になる。
        Assert.True(
            stopwatch.ElapsedMilliseconds < 140,
            $"キャンセル済みなのに再試行の待ち時間を消費しています: {stopwatch.ElapsedMilliseconds}ms");
    }

    /// @severity=med
    [Fact]
    public void 空文字パスでも例外を投げずnullを返すこと()
        => Assert.Null(ImageDecodeService.TryReadAllBytes(""));

    /// @severity=low
    [Fact]
    public void ゼロバイトのファイルは空配列として読み込めること()
    {
        using var temp = new TempDirectory("imagedecode-empty");
        var empty = temp.Combine("空.jpg");
        File.WriteAllBytes(empty, []);

        var bytes = ImageDecodeService.TryReadAllBytes(empty);

        Assert.NotNull(bytes);
        Assert.Empty(bytes);
    }
}

/// <summary>設定ファイルの壊れ耐性。@category=chaos</summary>
[Collection(AppStorageCollection.Name)]
public class SettingsServiceChaosTests
{
    /// @severity=high
    [Fact]
    public void 壊れた設定ファイルでも例外を投げず既定値を返すこと()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.SettingsPath, "{ これは JSON では ");

        var settings = SettingsService.Load();

        Assert.True(settings.CheckUpdatesOnStartup);
        Assert.Equal("Details", settings.DefaultViewMode);
        Assert.Equal(230, settings.SidebarWidth);
    }

    /// @severity=high
    [Fact]
    public void 壊れた設定ファイルはcorruptJsonへ退避されること()
    {
        using var scope = new AppStorageScope();
        const string broken = "{ \"StartupPath\": ";
        File.WriteAllText(scope.SettingsPath, broken);

        _ = SettingsService.Load();

        var preserved = Directory.GetFiles(scope.Directory, "settings-*.corrupt.json");
        var path = Assert.Single(preserved);
        Assert.Equal(broken, File.ReadAllText(path));
        // 退避しても原本は消さない（次回起動でも同じ判断ができる）
        Assert.True(File.Exists(scope.SettingsPath));
    }

    /// @severity=med
    [Fact]
    public void 空の設定ファイルでも例外を投げず既定値を返すこと()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.SettingsPath, "");

        var settings = SettingsService.Load();

        Assert.Equal("Details", settings.DefaultViewMode);
        Assert.Empty(settings.PinnedPaths);
    }

    /// @severity=high
    [Fact]
    public void 壊れた設定ファイルはバックアップから復旧されること()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.SettingsPath, "{ 壊れた JSON");
        File.WriteAllText(
            scope.SettingsPath + ".bak",
            """{"StartupPath":"D:\\backup","SidebarWidth":321,"CheckUpdatesOnStartup":false}""");

        var settings = SettingsService.Load();

        Assert.Equal(@"D:\backup", settings.StartupPath);
        Assert.Equal(321, settings.SidebarWidth);
        Assert.False(settings.CheckUpdatesOnStartup);
    }

    /// @severity=med
    [Fact]
    public void 本体とバックアップの両方が壊れていても既定値を返すこと()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.SettingsPath, "{ 壊れた JSON");
        File.WriteAllText(scope.SettingsPath + ".bak", "これも JSON ではない");

        var settings = SettingsService.Load();

        Assert.Equal("Details", settings.DefaultViewMode);
    }

    /// @severity=med
    [Fact]
    public void 設定ファイルが無いときは退避せず既定値を返すこと()
    {
        using var scope = new AppStorageScope();

        var settings = SettingsService.Load();

        Assert.True(settings.RememberWindowBounds);
        Assert.Empty(Directory.GetFiles(scope.Directory, "settings-*.corrupt.json"));
    }

    /// @severity=low
    [Fact]
    public void 中身がnullの設定ファイルは退避せず既定値を返すこと()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.SettingsPath, "null");

        var settings = SettingsService.Load();

        Assert.Equal("Details", settings.DefaultViewMode);
        Assert.Empty(Directory.GetFiles(scope.Directory, "settings-*.corrupt.json"));
    }

    /// @severity=low
    [Fact]
    public void 型が合わない設定値でも例外を投げず既定値を返すこと()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.SettingsPath, """{"ShowHidden":"はい","SidebarWidth":"ひろい"}""");

        var settings = SettingsService.Load();

        Assert.False(settings.ShowHidden);
        Assert.Equal(230, settings.SidebarWidth);
    }
}

/// <summary>フォルダー別表示設定の壊れ耐性。@category=chaos</summary>
[Collection(AppStorageCollection.Name)]
public class FolderViewSettingsServiceChaosTests
{
    private const string SamplePath = @"C:\Kiriha.Tests\folderview-sample";

    /// @severity=high
    [Fact]
    public void 壊れたフォルダー別設定ファイルでも例外を投げず空として扱うこと()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.FolderViewsPath, """{"Folders": [ { """);

        using var service = new FolderViewSettingsService();

        Assert.False(service.TryGet(SamplePath, out _));
    }

    /// @severity=med
    [Fact]
    public void フォルダー別設定ファイルが無くても例外を投げないこと()
    {
        using var scope = new AppStorageScope();
        Assert.False(File.Exists(scope.FolderViewsPath));

        using var service = new FolderViewSettingsService();

        Assert.False(service.TryGet(SamplePath, out _));
    }

    /// @severity=high
    /// @description 区切り文字統一の強化以前に C:/work と C:\work の両方が記録されていた場合、
    /// 読み込み時に同じキーへ畳まれる。保存は新しい順に並ぶため素朴に代入すると古い方が勝ってしまう。
    /// 更新日時が新しいエントリが残ること。
    [Fact]
    public void 正規化で同じキーへ畳まれた古い設定が新しい設定を上書きしないこと()
    {
        using var scope = new AppStorageScope();
        // 保存時と同じ「更新日時の降順」で並べる。畳んだあとは新しい方（Icons/160）が残るべき。
        File.WriteAllText(
            scope.FolderViewsPath,
            """
            {"Folders":[
              {"Path":"C:/work","ViewMode":"Icons","IconSize":160,"SortKey":"Size","SortAscending":false,"UpdatedUtcTicks":200},
              {"Path":"C:\\work","ViewMode":"List","IconSize":24,"SortKey":"Name","SortAscending":true,"UpdatedUtcTicks":100}
            ]}
            """);

        using var service = new FolderViewSettingsService();

        Assert.True(service.TryGet(@"C:\work", out var stored));
        Assert.Equal("Icons", stored.ViewMode);
        Assert.Equal(160, stored.IconSize);
    }

    /// @severity=med
    /// @description 並び順が逆（古い方が先）でも新しい方が残ること
    [Fact]
    public void 正規化で畳まれるとき並び順に関わらず最新の設定が残ること()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(
            scope.FolderViewsPath,
            """
            {"Folders":[
              {"Path":"C:\\work","ViewMode":"List","IconSize":24,"SortKey":"Name","SortAscending":true,"UpdatedUtcTicks":100},
              {"Path":"C:/work","ViewMode":"Icons","IconSize":160,"SortKey":"Size","SortAscending":false,"UpdatedUtcTicks":200}
            ]}
            """);

        using var service = new FolderViewSettingsService();

        Assert.True(service.TryGet(@"C:\work", out var stored));
        Assert.Equal("Icons", stored.ViewMode);
        Assert.Equal(160, stored.IconSize);
    }

    /// @severity=med
    [Fact]
    public void 壊れたフォルダー別設定はバックアップから復旧されること()
    {
        using var scope = new AppStorageScope();
        File.WriteAllText(scope.FolderViewsPath, """{"Folders": [ 壊れた""");
        File.WriteAllText(
            scope.FolderViewsPath + ".bak",
            "{\"Folders\":[{\"Path\":" + JsonSerializer.Serialize(SamplePath)
                + ",\"ViewMode\":\"Icons\",\"IconSize\":160,\"SortKey\":\"Size\",\"SortAscending\":false,\"UpdatedUtcTicks\":123}]}");

        using var service = new FolderViewSettingsService();

        Assert.True(service.TryGet(SamplePath, out var stored));
        Assert.Equal("Icons", stored.ViewMode);
        Assert.Equal(160, stored.IconSize);
        Assert.False(stored.SortAscending);
    }
}

/// <summary>属性付きファイルの列挙（Explorer パリティ）。@category=chaos</summary>
public class FileSystemServiceChaosTests
{
    /// @severity=high
    [Fact]
    public void 隠し属性とシステム属性の両方が付いたファイルは表示設定でも列挙されないこと()
    {
        using var temp = new TempDirectory("hidden-system-file");
        temp.CreateFile("ふつう.txt");
        var protectedFile = temp.CreateFile("desktop.ini");
        File.SetAttributes(protectedFile, File.GetAttributes(protectedFile) | FileAttributes.Hidden | FileAttributes.System);

        var entries = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowHidden = true, ShowExtensions = true });

        Assert.Equal(new[] { "ふつう.txt" }, entries.Select(e => e.Name));
    }

    /// @severity=high
    [Fact]
    public void 隠し属性とシステム属性の両方が付いたフォルダーは表示設定でも列挙されないこと()
    {
        using var temp = new TempDirectory("hidden-system-dir");
        temp.CreateSubDirectory("ふつうフォルダー");
        var protectedDir = temp.CreateSubDirectory("My Music");
        new DirectoryInfo(protectedDir).Attributes |= FileAttributes.Hidden | FileAttributes.System;

        var entries = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowHidden = true });

        Assert.Equal(new[] { "ふつうフォルダー" }, entries.Select(e => e.Name));
    }

    /// @severity=med
    [Fact]
    public void 拡張子非表示でもドットで始まるファイル名は空にならないこと()
    {
        using var temp = new TempDirectory("dotfile");
        temp.CreateFile(".gitignore");

        var entries = FileSystemService.GetEntries(temp.Root, new ShellOptions { ShowExtensions = false });

        Assert.Equal(".gitignore", Assert.Single(entries).DisplayName);
    }

    /// @severity=med
    /// @description 「例外を投げない」契約ではなく、TabViewModel が捕捉して案内を出す分担を固定する
    [Fact]
    public void 存在しないフォルダーではDirectoryNotFoundExceptionを投げること()
    {
        using var temp = new TempDirectory("missing");

        Assert.Throws<DirectoryNotFoundException>(
            () => FileSystemService.GetEntries(temp.Combine("消えたフォルダー"), new ShellOptions()));
    }
}

// ============================================================
// ⚡ 並行性（Concurrency Chaos）
// ============================================================

/// <summary>
/// フォルダー監視の共有と参照カウント。同一パスを複数タブで開いても watcher は1個、という契約。
/// @category=concurrency
/// </summary>
[Collection(AppStorageCollection.Name)]
public class DirectoryObservationServiceConcurrencyTests
{
    /// @severity=high
    [Fact]
    public void 同一パスへ複数購読しても監視インスタンスが共有され最後の解除で解放されること()
    {
        using var scope = new AppStorageScope();
        var watched = Path.Combine(scope.Directory, "watched");
        Directory.CreateDirectory(watched);

        var first = DirectoryObservationService.Subscribe(watched, _ => { });
        Assert.NotNull(first);
        var observation = ObservationProbe.Find(watched);
        Assert.NotNull(observation);

        // 末尾区切り違いでも WindowsPathIdentity で同一キーになるため watcher は増えない
        var second = DirectoryObservationService.Subscribe(watched + Path.DirectorySeparatorChar, _ => { });
        Assert.NotNull(second);
        Assert.Same(observation, ObservationProbe.Find(watched));
        Assert.Equal(2, ObservationProbe.CallbackCount(watched));

        first!.Dispose();
        Assert.Same(observation, ObservationProbe.Find(watched));
        Assert.Equal(1, ObservationProbe.CallbackCount(watched));

        second!.Dispose();
        Assert.Null(ObservationProbe.Find(watched));
    }

    /// @severity=high
    [Fact]
    public async Task 並行購読と解除を混在させても参照カウントが壊れないこと()
    {
        using var scope = new AppStorageScope();
        var watched = Path.Combine(scope.Directory, "watched");
        Directory.CreateDirectory(watched);

        const int threads = 8;
        using var start = new Barrier(threads);
        var kept = new IDisposable[threads];

        await Task.WhenAll(Enumerable.Range(0, threads).Select(index => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var round = 0; round < 20; round++)
            {
                var temporary = DirectoryObservationService.Subscribe(watched, _ => { });
                Assert.NotNull(temporary);
                temporary!.Dispose();
            }

            var live = DirectoryObservationService.Subscribe(watched, _ => { });
            Assert.NotNull(live);
            kept[index] = live!;
        })));

        try
        {
            Assert.NotNull(ObservationProbe.Find(watched));
            Assert.Equal(threads, ObservationProbe.CallbackCount(watched));
        }
        finally
        {
            foreach (var subscription in kept)
            {
                subscription.Dispose();
            }
        }

        Assert.Null(ObservationProbe.Find(watched));
    }

    /// @severity=high
    [Fact]
    public async Task 同一購読を並行して重複解除しても他の購読者の監視が維持されること()
    {
        using var scope = new AppStorageScope();
        var watched = Path.Combine(scope.Directory, "watched");
        Directory.CreateDirectory(watched);

        var kept = DirectoryObservationService.Subscribe(watched, _ => { })!;
        var doomed = DirectoryObservationService.Subscribe(watched, _ => { })!;
        var observation = ObservationProbe.Find(watched);
        Assert.Equal(2, ObservationProbe.CallbackCount(watched));

        const int threads = 4;
        using var start = new Barrier(threads);
        await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            doomed.Dispose();
        })));

        Assert.Same(observation, ObservationProbe.Find(watched));
        Assert.Equal(1, ObservationProbe.CallbackCount(watched));

        kept.Dispose();
        Assert.Null(ObservationProbe.Find(watched));
    }

    /// @severity=med
    /// @description 待ち時間ではなくイベント到達で判定する
    [Fact]
    public async Task 共有監視のファイル変更通知が全購読者へ届くこと()
    {
        using var scope = new AppStorageScope();
        var watched = Path.Combine(scope.Directory, "watched");
        Directory.CreateDirectory(watched);

        // 遅延通知後に Set されうるので Dispose せず GC に任せる（多重 Set は冪等）
        var firstNotified = new ManualResetEventSlim(false);
        var secondNotified = new ManualResetEventSlim(false);

        var first = DirectoryObservationService.Subscribe(watched, _ => firstNotified.Set())!;
        var second = DirectoryObservationService.Subscribe(watched, _ => secondNotified.Set())!;
        try
        {
            Assert.Equal(2, ObservationProbe.CallbackCount(watched));

            await File.WriteAllTextAsync(Path.Combine(watched, "created.txt"), "kiriha");

            var timeout = TimeSpan.FromSeconds(30);
            Assert.True(firstNotified.Wait(timeout), "1つ目の購読者へ変更通知が届きませんでした");
            Assert.True(secondNotified.Wait(timeout), "2つ目の購読者へ変更通知が届きませんでした");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }
}

/// <summary>設定保存のアトミック性。@category=concurrency</summary>
[Collection(AppStorageCollection.Name)]
public class SettingsServiceConcurrencyTests
{
    /// @severity=high
    [Fact]
    public async Task 並行保存中でも設定ファイルが常に有効なJSONであること()
    {
        using var scope = new AppStorageScope();
        SettingsService.Save(TestSettings.Snapshot(300));

        const int writers = 4;
        var failures = new ConcurrentBag<string>();
        using var start = new Barrier(writers + 1);
        using var writersDone = new CountdownEvent(writers);

        var writerTasks = Enumerable.Range(0, writers).Select(index => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                for (var round = 0; round < 60; round++)
                {
                    SettingsService.Save(TestSettings.Snapshot(301 + index));
                }
            }
            finally
            {
                writersDone.Signal();
            }
        })).ToArray();

        var readerTask = Task.Run(() =>
        {
            start.SignalAndWait();
            while (!writersDone.IsSet)
            {
                // File.Replace は「元ファイルを .bak へ移動 → 一時ファイルを元名へ」の順で動くため、
                // 同時保存中は外部から一瞬ファイルが存在しない状態を観測できる（OS プリミティブの性質）。
                // ここで検証したい契約は「壊れた JSON や複数世代が混ざった内容を読まない」ことなので、
                // 一時的な不在は破損として扱わない。
                // ※ 「settings.json が無く .bak だけある」状態は Load がバックアップを参照して復旧する。
                if (!File.Exists(scope.SettingsPath))
                {
                    continue;
                }

                string? text = null;
                for (var attempt = 0; attempt < 200 && text is null; attempt++)
                {
                    try { text = File.ReadAllText(scope.SettingsPath); }
                    catch (IOException) { Thread.Yield(); }
                    catch (UnauthorizedAccessException) { Thread.Yield(); }
                }

                // 共有違反で開けないのは破損ではないので判定対象外
                if (text is null)
                {
                    continue;
                }

                AppSettings? loaded;
                try
                {
                    loaded = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings);
                }
                catch (JsonException ex)
                {
                    failures.Add($"壊れた JSON を読み取りました: {ex.Message}");
                    break;
                }

                if (loaded is null)
                {
                    failures.Add("設定ファイルから null を読み取りました");
                    break;
                }

                if (!TestSettings.IsConsistent(loaded))
                {
                    failures.Add($"複数世代が混ざった内容を読み取りました: {loaded.SidebarWidth}");
                    break;
                }
            }
        });

        await Task.WhenAll(writerTasks.Append(readerTask));
        Assert.Empty(failures);
    }

    /// @severity=high
    [Fact]
    public async Task 並行保存の完了後にLoadが書き込んだ値のいずれかを返すこと()
    {
        using var scope = new AppStorageScope();
        SettingsService.Save(TestSettings.Snapshot(300));

        const int writers = 4;
        using var start = new Barrier(writers);
        await Task.WhenAll(Enumerable.Range(0, writers).Select(index => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var round = 0; round < 60; round++)
            {
                SettingsService.Save(TestSettings.Snapshot(301 + index));
            }
        })));

        var loaded = SettingsService.Load();
        Assert.True(TestSettings.IsConsistent(loaded), $"整合しない設定が読み込まれました: {loaded.SidebarWidth}");
        // 下限はシード値 300 の「次」から。Save は例外を握りつぶすため、300 を許容すると
        // 並行保存が全滅していてもこのテストが通ってしまう（検証したいのは書き込みの成功）。
        Assert.InRange(loaded.SidebarWidth, 301, 300 + writers);
    }

    /// @severity=med
    [Fact]
    public async Task 並行保存で一時ファイルや破損退避ファイルが残らないこと()
    {
        using var scope = new AppStorageScope();

        const int writers = 6;
        using var start = new Barrier(writers);
        await Task.WhenAll(Enumerable.Range(0, writers).Select(index => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var round = 0; round < 40; round++)
            {
                SettingsService.Save(TestSettings.Snapshot(301 + index));
            }
        })));

        Assert.True(File.Exists(scope.SettingsPath));
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.corrupt.json"));
    }
}

/// <summary>フォルダー別表示設定の一貫性と確定保存。@category=concurrency</summary>
[Collection(AppStorageCollection.Name)]
public class FolderViewSettingsServiceConcurrencyTests
{
    /// @severity=high
    [Fact]
    public async Task 同一パスへ並行更新してもTryGetが一貫したスナップショットを返すこと()
    {
        using var scope = new AppStorageScope();
        using var service = new FolderViewSettingsService();
        const string path = @"C:\Kiriha.Tests\concurrent";
        service.Set(path, TestFolderView.Variant(0));

        var failures = new ConcurrentBag<string>();
        const int writers = 2;
        using var start = new Barrier(writers + 1);
        using var writersDone = new CountdownEvent(writers);

        var writerTasks = Enumerable.Range(0, writers).Select(index => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                for (var round = 0; round < 500; round++)
                {
                    service.Set(path, TestFolderView.Variant(round + index));
                }
            }
            finally
            {
                writersDone.Signal();
            }
        })).ToArray();

        var readerTask = Task.Run(() =>
        {
            start.SignalAndWait();
            while (!writersDone.IsSet)
            {
                if (!service.TryGet(path, out var current))
                {
                    failures.Add("更新中に設定が取得できなくなりました");
                    break;
                }

                if (!TestFolderView.IsConsistent(current))
                {
                    failures.Add($"混ざった内容を取得しました: {current.ViewMode}/{current.IconSize}");
                    break;
                }
            }
        });

        await Task.WhenAll(writerTasks.Append(readerTask));
        Assert.Empty(failures);

        Assert.True(service.TryGet(path, out var last));
        Assert.True(TestFolderView.IsConsistent(last));
        service.Flush();
    }

    /// @severity=high
    [Fact]
    public async Task 並行更新後のFlushで保留中の全件がファイルへ書き出されること()
    {
        using var scope = new AppStorageScope();
        using var service = new FolderViewSettingsService();

        const int threads = 8;
        const int perThread = 50;
        using var start = new Barrier(threads);
        await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var index = 0; index < perThread; index++)
            {
                service.Set($@"C:\Kiriha.Tests\flush\{thread}\{index}", TestFolderView.Variant(index));
            }
        })));

        service.Flush();

        var store = TestFolderView.ReadStore(scope.FolderViewsPath);
        Assert.Equal(threads * perThread, store.Folders.Count);
        Assert.All(store.Folders, folder => Assert.True(TestFolderView.IsConsistent(folder)));
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.tmp"));
    }

    /// @severity=med
    [Fact]
    public async Task 並行更新と全消去を混在させてもFlush後のファイルが整合していること()
    {
        using var scope = new AppStorageScope();
        using var service = new FolderViewSettingsService();

        const int writers = 6;
        using var start = new Barrier(writers + 1);
        using var writersDone = new CountdownEvent(writers);

        var writerTasks = Enumerable.Range(0, writers).Select(thread => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                for (var index = 0; index < 200; index++)
                {
                    service.Set($@"C:\Kiriha.Tests\chaos\{thread}\{index}", TestFolderView.Variant(index));
                }
            }
            finally
            {
                writersDone.Signal();
            }
        })).ToArray();

        var clearTask = Task.Run(() =>
        {
            start.SignalAndWait();
            while (!writersDone.IsSet)
            {
                service.Clear();
            }
        });

        await Task.WhenAll(writerTasks.Append(clearTask));
        service.Flush();

        var store = TestFolderView.ReadStore(scope.FolderViewsPath);
        Assert.All(store.Folders, folder =>
        {
            Assert.NotEqual("", folder.Path);
            Assert.True(TestFolderView.IsConsistent(folder));
        });
        Assert.Equal(
            store.Folders.Select(folder => folder.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            store.Folders.Count);
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.tmp"));
    }

    /// @severity=med
    [Fact]
    public async Task 複数スレッドから同時にFlushしても一時ファイルを残さず有効なJSONになること()
    {
        using var scope = new AppStorageScope();
        using var service = new FolderViewSettingsService();

        const int entries = 100;
        for (var index = 0; index < entries; index++)
        {
            service.Set($@"C:\Kiriha.Tests\flush-race\{index}", TestFolderView.Variant(index));
        }

        const int flushers = 8;
        using var start = new Barrier(flushers);
        await Task.WhenAll(Enumerable.Range(0, flushers).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var round = 0; round < 20; round++)
            {
                service.Flush();
            }
        })));

        var store = TestFolderView.ReadStore(scope.FolderViewsPath);
        Assert.Equal(entries, store.Folders.Count);
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.tmp"));
    }

    /// @severity=high
    /// @description 終了処理は ShutdownRequested で Dispose → その後 MainWindow.OnClosing 経由で
    /// Flush が走る順序になり得る。破棄後の Flush / Set / Clear で ObjectDisposedException を投げると
    /// 終了処理が中断してウィンドウ位置などの保存が飛ぶため、投げずに済むこと。
    [Fact]
    public void 破棄後にFlushやSetを呼んでも例外を投げないこと()
    {
        using var scope = new AppStorageScope();
        var service = new FolderViewSettingsService();
        service.Set(@"C:\Kiriha.Tests\after-dispose", TestFolderView.Variant(0));

        service.Dispose();
        service.Dispose(); // 二重破棄も安全

        service.Flush();
        service.Set(@"C:\Kiriha.Tests\after-dispose2", TestFolderView.Variant(1));
        service.Clear();
        service.Flush();

        // 破棄時点の保留分は書き切られている
        var store = TestFolderView.ReadStore(scope.FolderViewsPath);
        Assert.NotNull(store);
    }

    /// @severity=low
    [Fact]
    public async Task 上限を超える並行更新でもエントリ数が上限以内に保たれること()
    {
        using var scope = new AppStorageScope();
        using var service = new FolderViewSettingsService();

        const int threads = 4;
        const int perThread = 1300; // 合計 5200 件 > MaxEntries(4096)
        using var start = new Barrier(threads);
        await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var index = 0; index < perThread; index++)
            {
                service.Set($@"C:\Kiriha.Tests\cap\{thread}\{index}", TestFolderView.Variant(index));
            }
        })));

        service.Flush();

        var store = TestFolderView.ReadStore(scope.FolderViewsPath);
        Assert.InRange(store.Folders.Count, 1, 4096);
        Assert.Equal(
            store.Folders.Select(folder => folder.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            store.Folders.Count);
    }
}
