using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// 保存先を差し替える静的状態（<see cref="AppStoragePaths"/>）と
/// <see cref="DirectoryObservationService"/> の静的辞書を触るテストは、このコレクションに属させて
/// xUnit に直列実行させる（同一コレクション内は非並列）。並列に走ると差し替えが競合し、
/// 最悪の場合ゆろちの実データ（%LocalAppData%\Kiriha / HKCU\Software\Kiriha）を読み書きしてしまう。
/// </summary>
[CollectionDefinition(AppStorageCollection.Name)]
public sealed class AppStorageCollection
{
    public const string Name = "アプリ保存先を差し替える直列テスト";
}

/// <summary>
/// <see cref="AppStoragePaths"/> の保存先を一意な一時ディレクトリと一時レジストリ接頭辞へ差し替えるスコープ。
/// 実ユーザーのデータに触れないための土台なので、保存系サービスを触るテストは必ず using で囲む。
/// </summary>
public sealed class AppStorageScope : IDisposable
{
    private readonly string _registryPrefix;

    public AppStorageScope()
    {
        Directory = Path.Combine(Path.GetTempPath(), "Kiriha.Tests", $"storage-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        _registryPrefix = $@"Software\Kiriha.Tests\{Guid.NewGuid():N}";
        AppStoragePaths.OverrideForTests(Directory, _registryPrefix);
    }

    /// <summary>差し替え先の永続データディレクトリ。</summary>
    public string Directory { get; }

    public string SettingsPath => Path.Combine(Directory, "settings.json");

    public string FolderViewsPath => Path.Combine(Directory, "folder-views.json");

    public void Dispose()
    {
        // 戻し忘れると以降のテストが実ユーザーの設定を読み書きするため、必ず既定へ戻す。
        AppStoragePaths.OverrideForTests(null, null);

        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_registryPrefix, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 後片付けの失敗はテスト結果に影響させない。
        }

        TempFileCleanup.DeleteRecursive(Directory);
    }
}

/// <summary>テスト専用の一意な一時ディレクトリ。using で必ず後始末する。</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string label = "temp")
    {
        Root = Path.Combine(Path.GetTempPath(), "Kiriha.Tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(string name) => Path.Combine(Root, name);

    public string CreateFile(string name, int size = 0, bool hidden = false)
    {
        var full = Combine(name);
        File.WriteAllBytes(full, new byte[size]);
        if (hidden)
        {
            File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Hidden);
        }
        return full;
    }

    public string CreateSubDirectory(string name, bool hidden = false)
    {
        var full = Combine(name);
        Directory.CreateDirectory(full);
        if (hidden)
        {
            File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Hidden);
        }
        return full;
    }

    public void Dispose() => TempFileCleanup.DeleteRecursive(Root);
}

internal static class TempFileCleanup
{
    /// <summary>Hidden / System / ReadOnly を落としてから再帰削除する（属性付きテストデータを残さない）。</summary>
    public static void DeleteRecursive(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (Exception) { /* 消せる限りで良い */ }
            }

            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try { new DirectoryInfo(directory).Attributes = FileAttributes.Directory; }
                catch (Exception) { /* 消せる限りで良い */ }
            }
        }
        catch (Exception) { /* 列挙自体が失敗しても削除を試みる */ }

        try { Directory.Delete(path, recursive: true); }
        catch (Exception) { /* 一時ディレクトリの残骸はテスト失敗にしない */ }
    }
}

internal static class CultureScope
{
    /// <summary>
    /// 指定カルチャを固定した専用スレッド上で検証を実行する。
    /// CurrentCulture は非同期フローへ伝播するため、専用スレッドに閉じ込めて他テストへ漏らさない。
    /// 空文字を渡すと ja-JP（本アプリの配布ロケール）を使う。
    /// </summary>
    public static void With(string cultureName, Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(cultureName.Length == 0 ? "ja-JP" : cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
    }
}

/// <summary>
/// <see cref="DirectoryObservationService"/> の内部状態（正規化パス→Observation）を検査するプローブ。
/// 「同一パスなら watcher は1個」「参照カウント 0 で解放」という観測しづらい契約を確認するためだけに使う。
/// </summary>
internal static class ObservationProbe
{
    private static readonly FieldInfo ObservationsField =
        typeof(DirectoryObservationService).GetField("Observations", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DirectoryObservationService.Observations が見つかりません");

    private static IDictionary Map => (IDictionary)ObservationsField.GetValue(null)!;

    /// <summary>指定パスの共有 Observation（無ければ null）。参照等価性で watcher の同一性を判定できる。</summary>
    public static object? Find(string path) => Map[path];

    /// <summary>指定パスの共有 Observation が保持しているコールバック数（＝参照カウント）。</summary>
    public static int CallbackCount(string path)
    {
        var observation = Find(path) ?? throw new InvalidOperationException($"監視が見つかりません: {path}");
        var property = observation.GetType().GetProperty("Callbacks")
            ?? throw new InvalidOperationException("Observation.Callbacks が見つかりません");
        return ((ICollection)property.GetValue(observation)!).Count;
    }
}

/// <summary>AppSettings の書き込み世代を相関させ、途中書き・混線を検出できるようにするヘルパー。</summary>
internal static class TestSettings
{
    public static AppSettings Snapshot(double sidebarWidth) => new()
    {
        SidebarWidth = sidebarWidth,
        VerticalTabWidth = sidebarWidth + 1000,
        PreviewWidth = sidebarWidth + 2000,
    };

    /// <summary>3つの値が同一世代の書き込みに由来しているか（既定値フォールバックも弾く）。</summary>
    public static bool IsConsistent(AppSettings settings)
        => settings.SidebarWidth >= 300
           && settings.VerticalTabWidth.Equals(settings.SidebarWidth + 1000)
           && settings.PreviewWidth.Equals(settings.SidebarWidth + 2000);
}

/// <summary>FolderViewSettings の相関付きバリアントと、永続化ファイルの読み戻しヘルパー。</summary>
internal static class TestFolderView
{
    public static FolderViewSettings Variant(int seed)
    {
        var iconsView = seed % 2 == 0;
        return new FolderViewSettings
        {
            ViewMode = iconsView ? "Icons" : "List",
            IconSize = iconsView ? 96 : 24,
            SortKey = iconsView ? "Size" : "Name",
            SortAscending = iconsView,
        };
    }

    /// <summary>4つのプロパティが同一バリアント由来か（＝torn read が起きていないか）。</summary>
    public static bool IsConsistent(FolderViewSettings settings)
    {
        if (settings.ViewMode != "Icons" && settings.ViewMode != "List")
        {
            return false;
        }

        var iconsView = settings.ViewMode == "Icons";
        return settings.IconSize.Equals(iconsView ? 96d : 24d)
               && settings.SortKey == (iconsView ? "Size" : "Name")
               && settings.SortAscending == iconsView;
    }

    public static FolderViewSettingsStore ReadStore(string path)
    {
        Assert.True(File.Exists(path), $"フォルダー別表示設定が保存されていません: {path}");
        var store = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            FolderViewSettingsJsonContext.Default.FolderViewSettingsStore);
        Assert.NotNull(store);
        return store!;
    }
}
