using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>ディレクトリ / ドライブの列挙を担当するサービス。</summary>
public static class FileSystemService
{
    /// <summary>PC（ドライブ一覧）を表す仮想パス。</summary>
    public const string ComputerPath = "";

    /// <summary>
    /// サイドバー / パンくず / 一覧に表示するドライブラベル（例: "Windows (C:)"）。
    /// エクスプローラーと同じ文字列にするためシェルの表示名を正本にする。ラベル未設定のドライブを
    /// 自前で組み立てると "C:" にしかならないが、本家は種別名を補って "ローカル ディスク (C:)" と出す。
    /// シェルが答えないときだけ従来の組み立てへ落とす。
    /// </summary>
    public static string GetDriveLabel(DriveInfo drive)
        => ShellDisplayService.TryGetDisplayName(drive.RootDirectory.FullName)
           ?? (string.IsNullOrEmpty(drive.VolumeLabel)
               ? drive.Name.TrimEnd('\\')
               : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})");

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

    /// <summary>
    /// ドライブ 1 台ぶんの容量表示（"空き 180 GB / 全体 476 GB"）と使用率。
    /// <see cref="DriveInfo.AvailableFreeSpace"/> / <see cref="DriveInfo.TotalSize"/> は同期 P/Invoke で、
    /// 切断中のネットワークドライブなどでは例外になりうる（TabViewModel.UpdateFreeSpace も同じ理由で
    /// try/catch している）。ここで握らないと 1 台の異常で PC ビュー全体が開けなくなるため、
    /// 失敗したドライブは容量表示なしで一覧に残す。
    /// </summary>
    public static (string SizeText, double UsedPercent) GetDriveSpace(DriveInfo drive)
    {
        try
        {
            var free = drive.AvailableFreeSpace;
            var total = drive.TotalSize;
            return (
                // 単位付きの数値はエクスプローラーと同じ丸め（180 GB / 0.98 TB）にしたいので
                // Windows の StrFormatByteSize に任せる。FormatSize は GB 止まりで桁も違う。
                LocalizationService.Text(
                    "Text.Drive.FreeOfTotal",
                    ShellDisplayService.FormatByteSize(free),
                    ShellDisplayService.FormatByteSize(total)),
                total > 0 ? (total - free) * 100.0 / total : 0);
        }
        catch (Exception ex)
        {
            Logger.LogException($"ドライブの容量を取得できませんでした: {drive.Name}", ex);
            return ("", 0);
        }
    }

    /// <summary>準備済みのドライブを列挙する。1 台の異常で列挙全体を落とさない。</summary>
    public static List<DriveInfo> GetReadyDrives()
    {
        var drives = new List<DriveInfo>();
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            Logger.LogException("ドライブ一覧を取得できませんでした", ex);
            return drives;
        }

        foreach (var drive in all)
        {
            try
            {
                if (drive.IsReady)
                {
                    drives.Add(drive);
                }
            }
            catch (Exception ex)
            {
                // 切断されたネットワークドライブなど。その 1 台だけ諦めて残りを表示する
                Logger.LogException($"ドライブの状態を取得できませんでした: {drive.Name}", ex);
            }
        }

        return drives;
    }

    /// <summary>指定パスのエントリ一覧を返す（フォルダー優先・名前順）。ComputerPath はドライブ一覧。</summary>
    public static List<FileSystemEntry> GetEntries(string path, ShellOptions options)
    {
        if (path == ComputerPath)
        {
            var drives = new List<FileSystemEntry>();
            foreach (var drive in GetReadyDrives())
            {
                try
                {
                    var label = GetDriveLabel(drive);
                    var (sizeText, usedPercent) = GetDriveSpace(drive);
                    drives.Add(new FileSystemEntry
                    {
                        Name = label,
                        DisplayName = label,
                        FullPath = drive.RootDirectory.FullName,
                        IsDirectory = true,
                        IsDrive = true,
                        MaterialIconKey = "", // MaterialIcon は IsDrive で常に null になるため未使用
                        SizeTextOverride = sizeText,
                        DriveFormat = TryGetDriveFormat(drive),
                        DriveUsedPercent = usedPercent,
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogException($"ドライブを一覧に追加できませんでした: {drive.Name}", ex);
                }
            }

            return drives;
        }

        // テーマ判定は列挙全体で 1 回だけ（行ごとに問い合わせない）
        var useMaterialIcons = options.IconSet == FileIconSet.Material;
        var preferLight = useMaterialIcons && MaterialIconService.IsLightTheme();

        var info = new DirectoryInfo(path);
        var entries = new List<FileSystemEntry>();

        // ここは既定の列挙（EnumerationOptions.Compatible）のままにする。
        // EnumerationOptions を自前で渡すと、既定の AttributesToSkip が Hidden|System のため
        // 「隠しファイルを表示」が効かなくなる（実測: C:\ 直下が 15 件 → 9 件）。
        // IgnoreInaccessible = true も足さない。非再帰の列挙では読めない子があっても例外にならず
        // （実測: System Volume Information を含む C:\ は正常に列挙できる）、効くのは
        // 「対象フォルダー自体が拒否」のときで、そこは例外のまま NavigateToAsync に
        // 「開けませんでした」を出させるのが正しい（true にすると空フォルダーに見えてしまう）。
        foreach (var dir in info.EnumerateDirectories())
        {
            if (CreateEntry(dir, options, useMaterialIcons, preferLight) is { } entry)
            {
                entries.Add(entry);
            }
        }

        foreach (var file in info.EnumerateFiles())
        {
            if (CreateEntry(file, options, useMaterialIcons, preferLight) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// 変更通知で届いた 1 パスぶんの行を作る（フォルダー監視からのピンポイント更新用）。
    /// 実体が無い・アクセスできない・表示対象外（隠し / 保護された OS 項目）なら null を返す。
    /// 返す内容は <see cref="GetEntries"/> の 1 行と同じでなければならないため、生成は共通化してある。
    /// </summary>
    public static FileSystemEntry? TryCreateEntry(string fullPath, ShellOptions options)
    {
        var useMaterialIcons = options.IconSet == FileIconSet.Material;
        var preferLight = useMaterialIcons && MaterialIconService.IsLightTheme();
        try
        {
            var directory = new DirectoryInfo(fullPath);
            if (directory.Exists)
            {
                return CreateEntry(directory, options, useMaterialIcons, preferLight);
            }

            var file = new FileInfo(fullPath);
            return file.Exists ? CreateEntry(file, options, useMaterialIcons, preferLight) : null;
        }
        catch (Exception ex)
        {
            // 変更直後に消える一時ファイルなど、この経路では珍しくない。行を作らないだけで続行する。
            Logger.Log($"変更のあった項目を取得できませんでした: {fullPath}: {ex.GetType().Name}", LogLevel.Warning);
            return null;
        }
    }

    /// <summary>フォルダー 1 件ぶんの行。表示対象外なら null。</summary>
    private static FileSystemEntry? CreateEntry(DirectoryInfo dir, ShellOptions options, bool useMaterialIcons, bool preferLight)
    {
        var attributes = dir.Attributes;
        var hidden = (attributes & FileAttributes.Hidden) != 0;
        if (!options.ShowHidden && hidden)
        {
            return null;
        }

        // Explorer パリティ: Hidden+System 両方付き（保護された OS 項目。Documents 配下の
        // My Music 等の互換ジャンクションなど）は「隠しファイルを表示」ON でも表示しない。
        if (hidden && (attributes & FileAttributes.System) != 0)
        {
            return null;
        }

        return new FileSystemEntry
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
        };
    }

    /// <summary>ファイル 1 件ぶんの行。表示対象外なら null。</summary>
    private static FileSystemEntry? CreateEntry(FileInfo file, ShellOptions options, bool useMaterialIcons, bool preferLight)
    {
        var attributes = file.Attributes;
        var hidden = (attributes & FileAttributes.Hidden) != 0;
        if (!options.ShowHidden && hidden)
        {
            return null;
        }

        // Explorer パリティ: Hidden+System 両方付き（desktop.ini 等）は常に非表示。
        if (hidden && (attributes & FileAttributes.System) != 0)
        {
            return null;
        }

        var display = !options.ShowExtensions && Path.GetFileNameWithoutExtension(file.Name) is { Length: > 0 } stem
            ? stem
            : file.Name;

        return new FileSystemEntry
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
        };
    }
}
