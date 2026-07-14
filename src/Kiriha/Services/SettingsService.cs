using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiriha.Services;

/// <summary>アプリ設定（%LocalAppData%\Kiriha\settings.json に永続化）。</summary>
public sealed class AppSettings
{
    /// <summary>固定タブのパス（並び順どおり）。次回起動時に復元する。</summary>
    public List<string> PinnedPaths { get; set; } = new();

    /// <summary>設定タブを固定タブとして次回起動時も復元する。</summary>
    public bool PinnedSettingsTab { get; set; }

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

    /// <summary>最後に使ったアイコンサイズ（マウスホイールで連続変更、新規タブの既定）。</summary>
    public double DefaultIconSize { get; set; } = 28;

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

    /// <summary>タブをウィンドウ左側へ縦に並べる。</summary>
    public bool UseVerticalTabs { get; set; }

    /// <summary>垂直タブバーの幅。</summary>
    public double VerticalTabWidth { get; set; } = 240;

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

    /// <summary>終了時に設定タブ（固定ではない）が開いていたか（RestoreAllTabs 用）。</summary>
    public bool OpenSettingsTab { get; set; }

    /// <summary>終了時に選択していたタブが設定タブだったか（次回起動時の選択状態復元用）。</summary>
    public bool LastSelectedTabIsSettings { get; set; }

    /// <summary>終了時に選択していたタブのパス（設定タブの場合は空。次回起動時の選択状態復元用）。</summary>
    public string LastSelectedTabPath { get; set; } = "";

    /// <summary>詳細表示の列幅（ヘッダーの Thumb ドラッグで変更、次回起動時に復元）。</summary>
    public double ColNameWidth { get; set; } = 300;

    public double ColModifiedWidth { get; set; } = 160;

    public double ColCreatedWidth { get; set; } = 170;

    public double ColTypeWidth { get; set; } = 140;

    public double ColSizeWidth { get; set; } = 180;

    /// <summary>詳細表示の列の表示 / 非表示（ヘッダー右クリックで切替、次回起動時に復元）。</summary>
    public bool ShowColModified { get; set; } = true;

    public bool ShowColCreated { get; set; }

    public bool ShowColType { get; set; } = true;

    public bool ShowColSize { get; set; } = true;

    /// <summary>検索ボックスの幅（境界の Thumb ドラッグで変更）。</summary>
    public double SearchBoxWidth { get; set; } = 200;

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
    private static readonly string BackupPath = SettingsPath + ".bak";

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
        catch (Exception ex)
        {
            Logger.LogException("設定ファイルを読み込めませんでした", ex);
            PreserveCorruptSettings();
            try
            {
                if (File.Exists(BackupPath))
                {
                    return JsonSerializer.Deserialize(
                               File.ReadAllText(BackupPath),
                               SettingsJsonContext.Default.AppSettings)
                           ?? new AppSettings();
                }
            }
            catch (Exception backupEx)
            {
                Logger.LogException("設定バックアップも読み込めませんでした", backupEx);
            }
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporary, SettingsPath, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, SettingsPath);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("設定ファイルを保存できませんでした", ex);
        }
    }

    private static void PreserveCorruptSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var corruptPath = Path.Combine(
                Path.GetDirectoryName(SettingsPath)!,
                $"settings-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.corrupt.json");
            File.Copy(SettingsPath, corruptPath, overwrite: false);
        }
        catch (Exception ex)
        {
            Logger.LogException("壊れた設定ファイルを退避できませんでした", ex);
        }
    }
}
