using Microsoft.Win32;

namespace Kiriha.Services;

public enum NewItemKind
{
    /// <summary>空ファイルを作成（レジストリ NullFile）。</summary>
    NullFile,

    /// <summary>レジストリ埋め込みデータで作成（レジストリ Data）。</summary>
    Data,

    /// <summary>テンプレートファイルをコピーして作成（レジストリ FileName）。</summary>
    TemplateFile,
}

/// <summary>エクスプローラーの「新規作成」メニュー 1 項目。</summary>
public sealed class NewItemTemplate
{
    public required string DisplayName { get; init; }

    public required string Extension { get; init; }

    public required NewItemKind Kind { get; init; }

    public byte[]? Data { get; init; }

    public string? TemplatePath { get; init; }
}

/// <summary>
/// レジストリの ShellNew 定義（HKCR\.ext\ShellNew / HKCR\.ext\progid\ShellNew）を列挙し、
/// エクスプローラーの「新規作成」メニューと同じ項目リストを返す。
/// </summary>
public static class ShellNewService
{
    private static IReadOnlyList<NewItemTemplate>? _cache;

    public static IReadOnlyList<NewItemTemplate> GetTemplates() => _cache ??= Enumerate();

    private static List<NewItemTemplate> Enumerate()
    {
        var result = new List<NewItemTemplate>();

        foreach (var extName in Registry.ClassesRoot.GetSubKeyNames())
        {
            if (!extName.StartsWith('.'))
            {
                continue;
            }

            try
            {
                using var extKey = Registry.ClassesRoot.OpenSubKey(extName);
                if (extKey is null)
                {
                    continue;
                }

                var progId = extKey.GetValue(null) as string;
                var template = ReadShellNew(extKey.OpenSubKey("ShellNew"), extName, progId)
                               ?? (progId is { Length: > 0 }
                                   ? ReadShellNew(extKey.OpenSubKey($"{progId}\\ShellNew"), extName, progId)
                                   : null);

                if (template is not null)
                {
                    result.Add(template);
                }
            }
            catch
            {
                // 読めない拡張子はスキップ（アクセス権など）
            }
        }

        return result
            .DistinctBy(t => t.Extension, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static NewItemTemplate? ReadShellNew(RegistryKey? shellNew, string extension, string? progId)
    {
        if (shellNew is null)
        {
            return null;
        }

        using (shellNew)
        {
            var displayName = GetFriendlyName(progId) ?? extension;

            if (shellNew.GetValue("NullFile") is not null)
            {
                return new NewItemTemplate { DisplayName = displayName, Extension = extension, Kind = NewItemKind.NullFile };
            }

            if (shellNew.GetValue("Data") is byte[] data)
            {
                return new NewItemTemplate { DisplayName = displayName, Extension = extension, Kind = NewItemKind.Data, Data = data };
            }

            if (shellNew.GetValue("FileName") is string fileName)
            {
                var path = Path.IsPathRooted(fileName)
                    ? fileName
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew", fileName);
                if (File.Exists(path))
                {
                    return new NewItemTemplate { DisplayName = displayName, Extension = extension, Kind = NewItemKind.TemplateFile, TemplatePath = path };
                }
            }

            // Command 型（ショートカットウィザード等）は対象外
            return null;
        }
    }

    private static string? GetFriendlyName(string? progId)
    {
        if (progId is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(progId);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
}
