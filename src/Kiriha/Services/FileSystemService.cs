using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>ディレクトリ / ドライブの列挙を担当するサービス。</summary>
public static class FileSystemService
{
    /// <summary>PC（ドライブ一覧）を表す仮想パス。</summary>
    public const string ComputerPath = "";

    /// <summary>サイドバー / パンくずに表示するドライブラベル（例: "Windows (C:)"）。</summary>
    public static string GetDriveLabel(DriveInfo drive)
        => string.IsNullOrEmpty(drive.VolumeLabel)
            ? drive.Name.TrimEnd('\\')
            : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

    private static string? TryGetDriveFormat(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>指定パスのエントリ一覧を返す（フォルダー優先・名前順）。ComputerPath はドライブ一覧。</summary>
    public static List<FileSystemEntry> GetEntries(string path, ShellOptions options)
    {
        if (path == ComputerPath)
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new FileSystemEntry
                {
                    Name = GetDriveLabel(d),
                    DisplayName = GetDriveLabel(d),
                    FullPath = d.RootDirectory.FullName,
                    IsDirectory = true,
                    IsDrive = true,
                    MaterialIconKey = "", // MaterialIcon は IsDrive で常に null になるため未使用
                    SizeTextOverride = $"空き {FileSystemEntry.FormatSize(d.AvailableFreeSpace)} / {FileSystemEntry.FormatSize(d.TotalSize)}",
                    DriveFormat = TryGetDriveFormat(d),
                    DriveUsedPercent = d.TotalSize > 0
                        ? (d.TotalSize - d.AvailableFreeSpace) * 100.0 / d.TotalSize
                        : 0,
                })
                .ToList();
        }

        // テーマ判定は列挙全体で 1 回だけ（行ごとに問い合わせない）
        var useMaterialIcons = options.IconSet == FileIconSet.Material;
        var preferLight = useMaterialIcons && MaterialIconService.IsLightTheme();

        var info = new DirectoryInfo(path);
        var entries = new List<FileSystemEntry>();

        foreach (var dir in info.EnumerateDirectories())
        {
            var attributes = dir.Attributes;
            var hidden = (attributes & FileAttributes.Hidden) != 0;
            if (!options.ShowHidden && hidden)
            {
                continue;
            }

            // Explorer パリティ: Hidden+System 両方付き（保護された OS 項目。Documents 配下の
            // My Music 等の互換ジャンクションなど）は「隠しファイルを表示」ON でも表示しない。
            if (hidden && (attributes & FileAttributes.System) != 0)
            {
                continue;
            }

            entries.Add(new FileSystemEntry
            {
                Name = dir.Name,
                DisplayName = dir.Name,
                FullPath = dir.FullName,
                IsDirectory = true,
                Modified = dir.LastWriteTime,
                Created = dir.CreationTime,
                IsHidden = hidden,
                IsReadOnly = (attributes & FileAttributes.ReadOnly) != 0,
                IsCut = ClipboardFileService.IsCutPath(dir.FullName),
                MaterialIconKey = useMaterialIcons
                    ? MaterialIconService.ResolveIconKey(dir.Name, isDirectory: true, preferLight)
                    : "",
            });
        }

        foreach (var file in info.EnumerateFiles())
        {
            var attributes = file.Attributes;
            var hidden = (attributes & FileAttributes.Hidden) != 0;
            if (!options.ShowHidden && hidden)
            {
                continue;
            }

            // Explorer パリティ: Hidden+System 両方付き（desktop.ini 等）は常に非表示。
            if (hidden && (attributes & FileAttributes.System) != 0)
            {
                continue;
            }

            var display = !options.ShowExtensions && Path.GetFileNameWithoutExtension(file.Name) is { Length: > 0 } stem
                ? stem
                : file.Name;

            entries.Add(new FileSystemEntry
            {
                Name = file.Name,
                DisplayName = display,
                FullPath = file.FullName,
                IsDirectory = false,
                Size = file.Length,
                Modified = file.LastWriteTime,
                Created = file.CreationTime,
                IsHidden = hidden,
                IsReadOnly = (attributes & FileAttributes.ReadOnly) != 0,
                IsCut = ClipboardFileService.IsCutPath(file.FullName),
                MaterialIconKey = useMaterialIcons
                    ? MaterialIconService.ResolveIconKey(file.Name, isDirectory: false, preferLight)
                    : "",
            });
        }

        return entries;
    }
}
