using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiriha.Services;

/// <summary>アプリ設定（%LocalAppData%\Kiriha\settings.json に永続化）。</summary>
public sealed class AppSettings
{
    /// <summary>固定タブのパス（並び順どおり）。次回起動時に復元する。</summary>
    public List<string> PinnedPaths { get; set; } = new();

    public bool ShowHidden { get; set; }

    public bool ShowExtensions { get; set; }

    /// <summary>起動時に自動で更新をチェックするかどうか。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>「このバージョンをスキップ」で無視した更新タグ（自動チェック時のみ有効）。</summary>
    public string IgnoreUpdateTag { get; set; } = "";

    /// <summary>エクスプローラーの「項目チェックボックス」相当。</summary>
    public bool ShowCheckBoxes { get; set; }

    /// <summary>お気に入りバーの表示状態（Ctrl+Shift+B で切替）。</summary>
    public bool ShowBookmarksBar { get; set; }

    /// <summary>お気に入りバーの内容。</summary>
    public List<Kiriha.Models.BookmarkNode> Bookmarks { get; set; } = new();

    /// <summary>最後に使った表示モード（新規タブの既定）。</summary>
    public string DefaultViewMode { get; set; } = "Details";

    /// <summary>前回のウィンドウサイズ（0 以下なら既定値を使う）。</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    /// <summary>前回のウィンドウ位置（両方 0 なら OS 既定）。</summary>
    public int WindowX { get; set; } = int.MinValue;

    public int WindowY { get; set; } = int.MinValue;

    /// <summary>前回最大化で終了したか。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>ウィンドウのサイズと位置を保存して次回復元する（設定画面で切替、既定 ON）。</summary>
    public bool RememberWindowBounds { get; set; } = true;

    /// <summary>左ペインの表示状態。</summary>
    public bool ShowSidebar { get; set; } = true;

    /// <summary>左ペインの幅。</summary>
    public double SidebarWidth { get; set; } = 230;

    /// <summary>プレビューペインの表示状態（Alt+P）。</summary>
    public bool ShowPreviewPane { get; set; }

    /// <summary>テーマ（System / Light / Dark）。</summary>
    public string ThemePreference { get; set; } = "System";

    /// <summary>新しいタブで開く既定フォルダー（空ならユーザーフォルダー）。</summary>
    public string StartupPath { get; set; } = "";

    /// <summary>終了時に開いていたタブを次回復元する（Chrome の「前回開いていたページ」相当）。</summary>
    public bool RestoreAllTabs { get; set; }

    /// <summary>終了時に開いていたタブのパス（RestoreAllTabs 用）。</summary>
    public List<string> OpenTabPaths { get; set; } = new();

    /// <summary>絵文字の代わりに Material Icon Theme のアイコンを使う（設定画面で切替）。</summary>
    public bool UseMaterialIcons { get; set; }

    /// <summary>ウィンドウにアクリル（半透明ぼかし）効果を使う（Lhamiel / RealTimeTranslator と同等、設定画面で切替）。</summary>
    public bool UseAcrylicBackground { get; set; } = true;

    /// <summary>最小化時にタスクバーではなくタスクトレイに格納する（Discord と同等の挙動、既定 OFF）。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>起動時にウィンドウを表示せずタスクトレイに格納した状態で開始する（Discord と同等の挙動、既定 OFF）。</summary>
    public bool StartMinimizedToTray { get; set; }
}

/// <summary>Native AOT 用の JSON source generator コンテキスト。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kiriha", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), SettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
            }
        }
        catch
        {
            // 壊れた設定ファイルは既定値で継続する
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        }
        catch
        {
            // 保存失敗は致命的でないため無視する（次回保存で再試行）
        }
    }
}
