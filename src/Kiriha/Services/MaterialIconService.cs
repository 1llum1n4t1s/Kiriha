using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>
/// Material Icon Theme（MIT ライセンス、Assets/MaterialIcons 配下に同梱）による
/// ファイル種別アイコンの解決とレンダリング用画像のキャッシュ。
/// <para>
/// アイコン本体は元 SVG を resvg でビルド時に 256x256 PNG へラスタライズ済み。
/// Avalonia 12 系では SVG ランタイム描画ライブラリ (Avalonia.Svg.Skia) が Avalonia 11 向けビルドしか
/// 存在せず、実機検証でウィンドウ全体が描画されなくなる致命的な非互換が確認されたため、
/// 既存のサムネイル機能と同じ実績のある Bitmap 経路に統一した。
/// </para>
/// </summary>
public static class MaterialIconService
{
    private const string BaseUri = "avares://Kiriha/Assets/MaterialIcons";

    private static MaterialIconManifest? _manifest;
    private static readonly ConcurrentDictionary<string, IImage?> ImageCache = new();
    private static readonly ConcurrentQueue<string> ImageCacheOrder = new();
    private const int ImageCacheLimit = 256;

    private static MaterialIconManifest Manifest => _manifest ??= Load();

    private static MaterialIconManifest Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"{BaseUri}/manifest.json"));
            return System.Text.Json.JsonSerializer.Deserialize(stream, MaterialIconManifestJsonContext.Default.MaterialIconManifest)
                   ?? new MaterialIconManifest();
        }
        catch
        {
            // マニフェストが読めない場合は既定アイコン (file/folder) のみのフォールバック
            return new MaterialIconManifest();
        }
    }

    /// <summary>現在の実効テーマがライトかどうか（ライト専用アイコンの優先に使う）。</summary>
    public static bool IsLightTheme()
        => (Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark) == ThemeVariant.Light;

    /// <summary>
    /// ファイル / フォルダー名からアイコンキー（拡張子なしの PNG ファイル名）を解決する。
    /// 一致順は「ファイル名完全一致 → ファイル名小文字一致 → 拡張子一致 → 既定」。
    /// </summary>
    public static string ResolveIconKey(string name, bool isDirectory, bool preferLight)
    {
        var m = Manifest;

        if (isDirectory)
        {
            var lower = name.ToLowerInvariant();
            if (preferLight && m.LightFolderNames.TryGetValue(lower, out var lightFolder))
            {
                return lightFolder;
            }

            return m.FolderNames.TryGetValue(lower, out var folder) ? folder : m.DefaultFolder;
        }

        if (preferLight && (m.LightFileNames.TryGetValue(name, out var lightExact)
                             || m.LightFileNames.TryGetValue(name.ToLowerInvariant(), out lightExact)))
        {
            return lightExact;
        }

        if (m.FileNames.TryGetValue(name, out var exact)
            || m.FileNames.TryGetValue(name.ToLowerInvariant(), out exact))
        {
            return exact;
        }

        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0)
        {
            if (preferLight && m.LightFileExtensions.TryGetValue(ext, out var lightExt))
            {
                return lightExt;
            }

            if (m.FileExtensions.TryGetValue(ext, out var byExt))
            {
                return byExt;
            }
        }

        return m.DefaultFile;
    }

    /// <summary>アイコンキーに対応する PNG を（キャッシュ済みなら再利用して）返す。</summary>
    public static IImage? GetImage(string iconKey)
    {
        if (ImageCache.TryGetValue(iconKey, out var cached))
        {
            return cached;
        }

        // GetOrAdd の valueFactory は競合時に複数回実行されうる（ConcurrentDictionary の仕様）。
        // 敗者側が生成した Bitmap が未解放のまま捨てられないよう、生成→追加→敗者破棄を明示する。
        var created = LoadImage(iconKey);
        var image = ImageCache.GetOrAdd(iconKey, created);
        if (!ReferenceEquals(image, created) && created is Bitmap loser)
        {
            loser.Dispose();
        }
        ImageCacheOrder.Enqueue(iconKey);
        while (ImageCache.Count > ImageCacheLimit && ImageCacheOrder.TryDequeue(out var oldest))
        {
            // 表示中のImageを破棄しないため、強参照だけを外してGCへ所有権を戻す。
            ImageCache.TryRemove(oldest, out _);
        }
        return image;
    }

    private static IImage? LoadImage(string key)
    {
            // 上流アイコンセット内で同名 SVG が重複していたものは、ラスタライズ時に
            // 「.clone」付きで保存されている。マニフェストは元のキーを指すため、
            // 通常名が無い場合だけ clone 版を試す。
            var image = TryLoadImage(key) ?? TryLoadImage($"{key}.clone");
            if (image is not null)
            {
                return image;
            }

            // 壊れた／不足した個別マッピングがあっても透明表示にはせず、
            // フォルダーとファイルの汎用アイコンへフォールバックする。
            var fallbackKey = key.StartsWith("folder-", StringComparison.Ordinal)
                ? Manifest.DefaultFolder
                : Manifest.DefaultFile;
            return TryLoadImage(fallbackKey);
    }

    private static IImage? TryLoadImage(string iconKey)
    {
        try
        {
            var uri = new Uri($"{BaseUri}/icons/{iconKey}.png");
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
