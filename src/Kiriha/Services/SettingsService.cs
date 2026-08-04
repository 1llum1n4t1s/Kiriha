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

    /// <summary>最後に使った並べ替え列（新規タブの既定）。</summary>
    public string DefaultSortKey { get; set; } = "Name";

    /// <summary>最後に使った並べ替え方向（新規タブの既定）。</summary>
    public bool DefaultSortAscending { get; set; } = true;

    /// <summary>前回のウィンドウサイズ（0 以下なら既定値を使う）。</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    /// <summary>前回のウィンドウ位置（両方 0 なら OS 既定）。</summary>
    public int WindowX { get; set; } = int.MinValue;

    public int WindowY { get; set; } = int.MinValue;

    /// <summary>前回最大化で終了したか。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>終了時にウィンドウが表示されていたモニターの作業領域。
    /// 最大化時も同じモニターへ復元するために使う。</summary>
    public int WindowMonitorX { get; set; } = int.MinValue;

    public int WindowMonitorY { get; set; } = int.MinValue;

    public int WindowMonitorWidth { get; set; }

    public int WindowMonitorHeight { get; set; }

    /// <summary>ウィンドウのサイズと位置を保存して次回復元する（設定画面で切替、既定 ON）。</summary>
    public bool RememberWindowBounds { get; set; } = true;

    /// <summary>左ペインの表示状態。</summary>
    public bool ShowSidebar { get; set; } = true;

    /// <summary>左ペインにクイックアクセスの代わりに XP 風フォルダーツリーを表示する。</summary>
    public bool SidebarShowTree { get; set; }

    /// <summary>ツリー表示を現在のフォルダーへ自動追従させる
    /// （VS の「アクティブ ドキュメントとの同期」トグル。既定はオン）。</summary>
    public bool SidebarTreeSyncActive { get; set; } = true;

    /// <summary>左ペインの幅。</summary>
    public double SidebarWidth { get; set; } = 230;

    /// <summary>垂直タブバーの幅。</summary>
    public double VerticalTabWidth { get; set; } = 240;

    /// <summary>プレビューペインの表示状態（Alt+P）。</summary>
    public bool ShowPreviewPane { get; set; }

    /// <summary>プレビューペインの幅（境界の Thumb ドラッグで変更）。</summary>
    public double PreviewWidth { get; set; } = 280;

    /// <summary>ギャラリー表示の下部サムネイルストリップの高さ（Thumb ドラッグで変更、全タブ共通）。</summary>
    public double GalleryStripHeight { get; set; } = 116;

    /// <summary>ギャラリー動画の音量（0.0〜1.0、全タブ共通）。</summary>
    public double VideoVolume { get; set; } = 0.7;

    /// <summary>ギャラリー動画のミュート状態（全タブ共通）。</summary>
    public bool VideoMuted { get; set; }

    /// <summary>ギャラリー動画の再生速度（1.0 が等速、全タブ共通）。</summary>
    public double VideoRate { get; set; } = 1.0;

    /// <summary>ステータスバーの表示状態（表示メニューで切替）。</summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>コンパクトビュー（行の高さを詰める。表示メニューで切替、新規タブの既定）。</summary>
    public bool CompactView { get; set; }

    /// <summary>タブのダブルクリック動作（None / Pin / Close）。</summary>
    public string TabDoubleClickAction { get; set; } = "None";

    /// <summary>タブのホイールクリック動作（None / Pin / Close）。既定はこれまで通り閉じる。</summary>
    public string TabMiddleClickAction { get; set; } = "Close";

    /// <summary>フォルダー背景のダブルクリック動作（None / Up / Refresh）。</summary>
    public string BackgroundDoubleClickAction { get; set; } = "None";

    /// <summary>フォルダー背景のホイールクリック動作（None / Up / Refresh）。</summary>
    public string BackgroundMiddleClickAction { get; set; } = "None";

    /// <summary>フォルダーツリーからのドラッグ開始を禁止する。</summary>
    public bool SidebarTreeDragDisabled { get; set; }

    /// <summary>フォルダーツリーへのドロップ受け入れを禁止する。</summary>
    public bool SidebarTreeDropDisabled { get; set; }

    /// <summary>テーマ（System / Light / Dark）。</summary>
    public string ThemePreference { get; set; } = "System";

    /// <summary>UI 表示言語のロケールキー（例: "ja_JP"）。空文字は「自動判定」で、
    /// 初回インストール直後はこれになる（OS の UI 言語から LocalizationService が決める）。</summary>
    public string Locale { get; set; } = "";

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

    public double ColSizeWidth { get; set; } = 100;

    /// <summary>詳細表示の列の表示 / 非表示（ヘッダー右クリックで切替、次回起動時に復元）。</summary>
    public bool ShowColModified { get; set; } = true;

    public bool ShowColCreated { get; set; }

    public bool ShowColType { get; set; } = true;

    public bool ShowColSize { get; set; } = true;

    /// <summary>検索ボックスの幅（境界の Thumb ドラッグで変更）。</summary>
    public double SearchBoxWidth { get; set; } = 200;

    /// <summary>ファイル一覧で使うアイコンセット。未設定なら旧 UseMaterialIcons から移行する。</summary>
    public string? IconSet { get; set; }

    /// <summary>v1.0.17 以前の設定から移行するために保持する旧フラグ。</summary>
    public bool UseMaterialIcons { get; set; }

    /// <summary>右クリックメニューの実装方式（<see cref="Kiriha.Models.ContextMenuStyle"/> の enum 名）。
    /// 未設定・不正値は既定の Modern として扱う。</summary>
    public string? ContextMenuStyle { get; set; }

    /// <summary>ウィンドウにアクリル（半透明ぼかし）効果を使う（Lhamiel / RealTimeTranslator と同等、設定画面で切替）。</summary>
    public bool UseAcrylicBackground { get; set; } = true;

    /// <summary>ギャラリー表示の画像・動画に RCAS の鮮鋭化を掛ける（拡大時のぼけ対策、既定 ON）。</summary>
    public bool SharpenGallery { get; set; } = true;

    /// <summary>鮮鋭化の強さ（SharpenStrength の名前。Low / Normal / High / Max）。</summary>
    public string SharpenStrength { get; set; } = "Normal";

    /// <summary>動画の早送り・巻き戻し 1 回あたりの秒数（既定 1 秒）。</summary>
    public double VideoSeekSeconds { get; set; } = 1.0;

    /// <summary>Kiriha 内で画像をダブルクリックしたとき、ギャラリーの全画面表示で開く（既定 ON）。
    /// Windows の関連付けには関与しない（エクスプローラーからの起動は従来どおり）。</summary>
    public bool OpenImagesInGallery { get; set; } = true;

    /// <summary>Kiriha 内で動画をダブルクリックしたとき、ギャラリーの全画面表示で開く（既定 ON）。</summary>
    public bool OpenVideosInGallery { get; set; } = true;

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
    // 置き場は AppStoragePaths が正本（テストが実ユーザーの設定を壊さないよう差し替え可能）。
    private static string SettingsPath => Path.Combine(AppStoragePaths.Directory, "settings.json");
    private static string BackupPath => SettingsPath + ".bak";

    /// <summary>ファイル共有違反で読めなかったときの再試行回数と間隔。
    /// Save は一時ファイル + File.Replace で置き換えるため、その瞬間に読むと一時的に開けないことがある。</summary>
    private const int ReadRetryCount = 3;
    private const int ReadRetryDelayMilliseconds = 30;

    public static AppSettings Load()
    {
        // 本体 → バックアップの順に試す。本体が「壊れている」場合だけでなく「存在しない」場合も
        // バックアップを見る: Save の File.Replace は本体を .bak へ移してから一時ファイルを本体名へ
        // 置き換えるため、その途中でプロセスが落ちると「本体なし + 正常な .bak」だけが残り、
        // 以前はバックアップを無視して設定が既定値へ戻っていた。
        if (TryLoadFrom(SettingsPath, isBackup: false) is { } primary)
        {
            return primary;
        }

        if (TryLoadFrom(BackupPath, isBackup: true) is { } backup)
        {
            return backup;
        }

        return new AppSettings();
    }

    /// <summary>指定ファイルから設定を読む。読めない・壊れている場合は null。</summary>
    private static AppSettings? TryLoadFrom(string path, bool isBackup)
    {
        var label = isBackup ? "設定バックアップ" : "設定ファイル";
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException ex) when (attempt < ReadRetryCount)
            {
                // 保存処理との競合による一時的な共有違反。壊れたと決めつけず読み直す。
                Logger.Log($"{label}を読み込めませんでした（再試行します）: {ex.Message}", LogLevel.Debug);
                Thread.Sleep(ReadRetryDelayMilliseconds);
                continue;
            }
            catch (Exception ex)
            {
                // I/O 系の失敗はファイルの内容が壊れた証拠ではないため退避しない。
                Logger.LogException($"{label}を読み込めませんでした", ex);
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                // 内容が本当に壊れているときだけ退避する（正常なファイルで *.corrupt.json を増やさない）。
                Logger.LogException($"{label}の内容が壊れています", ex);
                if (!isBackup)
                {
                    PreserveCorruptSettings();
                }

                return null;
            }
        }
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
