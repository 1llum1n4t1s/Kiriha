using System.Text.Json.Serialization;

namespace Kiriha.Models;

/// <summary>
/// Material Icon Theme（<c>Assets/MaterialIcons/manifest.json</c>）から読み込む
/// 拡張子 / ファイル名 / フォルダー名 → アイコンキーのマッピング。
/// </summary>
public sealed class MaterialIconManifest
{
    public string DefaultFile { get; set; } = "file";

    public string DefaultFolder { get; set; } = "folder";

    public Dictionary<string, string> FileExtensions { get; set; } = new();

    /// <summary>キーはファイル名そのまま（大文字小文字を区別する既知のファイル名を含むため）。</summary>
    public Dictionary<string, string> FileNames { get; set; } = new();

    public Dictionary<string, string> FolderNames { get; set; } = new();

    public Dictionary<string, string> LightFileExtensions { get; set; } = new();

    public Dictionary<string, string> LightFileNames { get; set; } = new();

    public Dictionary<string, string> LightFolderNames { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MaterialIconManifest))]
internal sealed partial class MaterialIconManifestJsonContext : JsonSerializerContext;
