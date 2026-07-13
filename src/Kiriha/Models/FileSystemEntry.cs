using CommunityToolkit.Mvvm.ComponentModel;

namespace Kiriha.Models;

/// <summary>ファイル一覧の 1 行分（ファイル / フォルダー / ドライブ）を表すモデル。</summary>
public partial class FileSystemEntry : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>一覧に表示する名前（拡張子表示 OFF のときは拡張子なし）。</summary>
    public required string DisplayName { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>ドライブ一覧（PC 表示）での 1 行かどうか。</summary>
    public bool IsDrive { get; init; }

    /// <summary>ファイルサイズ（バイト）。フォルダー / ドライブは null。</summary>
    public long? Size { get; init; }

    public DateTime? Modified { get; init; }

    /// <summary>作成日時（オプション列）。</summary>
    public DateTime? Created { get; init; }

    /// <summary>ドライブの使用率（0-100、ドライブ以外は 0）。</summary>
    public double DriveUsedPercent { get; init; }

    /// <summary>隠し属性（エクスプローラーと同じく薄く表示する）。</summary>
    public bool IsHidden { get; init; }

    /// <summary>読み取り専用属性（列挙時に取得済み。「読み取り専用項目を選択」で同期 I/O を避けるため）。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>切り取り済み（エクスプローラーと同じく半透明表示する）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isCut;

    /// <summary>アイコンビュー用の画像サムネイル（非同期ロード）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail), nameof(HasNoThumbnail))]
    private Avalonia.Media.Imaging.Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    public bool HasNoThumbnail => Thumbnail is null;

    /// <summary>この項目が所有するサムネイルを解放する。</summary>
    public void DisposeThumbnail()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }

    /// <summary>Material Icon Theme のアイコンキー（拡張子/ファイル名/フォルダー名から解決済み）。</summary>
    public required string MaterialIconKey { get; init; }

    /// <summary>Material Icon Theme の SVG（キャッシュ済み）。ドライブは対象外で常に null。</summary>
    public Avalonia.Media.IImage? MaterialIcon => IsDrive ? null : Services.MaterialIconService.GetImage(MaterialIconKey);

    /// <summary>切り取り / 隠し属性を反映した行の不透明度。</summary>
    public double RowOpacity => IsCut ? 0.45 : IsHidden ? 0.6 : 1.0;

    public string Icon
    {
        get
        {
            if (IsDrive) return "💾";
            if (IsDirectory) return "📁";
            return Path.GetExtension(Name).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico" or ".svg" => "🖼",
                ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" or ".wma" => "🎵",
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => "🎬",
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".xz" or ".cab" => "🗜",
                ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" => "⚙",
                ".pdf" => "📕",
                ".doc" or ".docx" => "📘",
                ".xls" or ".xlsx" or ".csv" => "📗",
                ".ppt" or ".pptx" => "📙",
                ".txt" or ".md" or ".log" or ".ini" or ".json" or ".xml" or ".yaml" or ".yml" => "📄",
                ".cs" or ".js" or ".ts" or ".py" or ".rs" or ".cpp" or ".c" or ".h" or ".java" or ".html" or ".css" => "📜",
                ".lnk" => "🔗",
                ".ttf" or ".otf" or ".woff" or ".woff2" => "🔤",
                _ => "📄",
            };
        }
    }

    /// <summary>サイズ列の表示を上書きする（ドライブの「空き / 合計」表示用）。</summary>
    public string? SizeTextOverride { get; init; }

    public string SizeText => SizeTextOverride ?? (Size is not { } size ? "" : FormatSize(size));

    public string ModifiedText => Modified?.ToString("yyyy/MM/dd HH:mm") ?? "";

    public string CreatedText => Created?.ToString("yyyy/MM/dd HH:mm") ?? "";

    /// <summary>行のツールチップ（秒付き日時などの詳細）。</summary>
    public string RowTooltip
        => $"{Name}\n種類: {TypeText}"
           + (Size is { } s ? $"\nサイズ: {FormatSize(s)}" : "")
           + (Modified is { } m ? $"\n更新日時: {m:yyyy/MM/dd HH:mm:ss}" : "");

    /// <summary>ドライブのファイルシステム（NTFS 等）。</summary>
    public string? DriveFormat { get; init; }

    public string TypeText
    {
        get
        {
            if (IsDrive) return DriveFormat is { Length: > 0 } fmt ? $"ローカル ディスク ({fmt})" : "ローカル ディスク";
            if (IsDirectory) return "ファイル フォルダー";
            var ext = Path.GetExtension(Name);
            return string.IsNullOrEmpty(ext) ? "ファイル" : $"{ext.TrimStart('.').ToUpperInvariant()} ファイル";
        }
    }

    public static string FormatSize(long size)
    {
        return size switch
        {
            < 1024 => $"{size} B",
            < 1024 * 1024 => $"{size / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024):0.#} MB",
            _ => $"{size / (1024.0 * 1024 * 1024):0.##} GB",
        };
    }
}
