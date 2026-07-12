using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Kiriha.Services;

/// <summary>
/// Windows へのスタートアップ登録 / エクスプローラー右クリックメニュー登録を扱う。
/// すべて HKCU（現在のユーザー）配下への書き込みで完結し、管理者権限は不要。
/// 設定の真実の源はレジストリ側にあり、設定画面のチェック状態は現在のレジストリ状態を都度読んで反映する。
/// </summary>
public static partial class WindowsIntegrationService
{
    private const string AppName = "Kiriha";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MenuLabel = "Kiriha で開く";
    private const string AssociationBackupPath = @"Software\Kiriha\ExplorerAssociationBackup";
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    // フォルダー本体の右クリックと、フォルダー内の空白（背景）の右クリックの両方に登録する。
    private static readonly string[] ShellRoots =
    {
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\Background\shell",
    };

    // 実ファイルシステムのフォルダーとドライブだけを対象にする。
    // Folder クラスはコントロールパネル等の仮想シェル項目にも使われるため変更しない。
    private static readonly string[] DefaultOpenClasses = ["Directory", "Drive"];

    /// <summary>現在の実行ファイルのフルパス（Velopack 配置でも current 配下で安定）。</summary>
    private static string? ExePath => Environment.ProcessPath;

    // ===== スタートアップ登録 =====

    /// <summary>Windows のスタートアップ（HKCU Run キー）に登録済みかどうか。</summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>スタートアップ登録の ON/OFF を切り替える。</summary>
    public static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                if (ExePath is { } exe)
                {
                    key.SetValue(AppName, $"\"{exe}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 失敗しても致命的ではない（次回操作で再試行）
        }
    }

    // ===== エクスプローラー右クリックメニュー登録 =====

    /// <summary>エクスプローラーの右クリックメニューに「Kiriha で開く」を登録済みかどうか。</summary>
    public static bool IsExplorerMenuEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ShellRoots[0]}\{AppName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>エクスプローラー右クリックメニュー登録の ON/OFF を切り替える。</summary>
    public static void SetExplorerMenuEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (ExePath is not { } exe)
                {
                    return;
                }

                foreach (var root in ShellRoots)
                {
                    using var verbKey = Registry.CurrentUser.CreateSubKey($@"{root}\{AppName}");
                    if (verbKey is null)
                    {
                        continue;
                    }

                    verbKey.SetValue(null, MenuLabel);       // メニューの表示名
                    verbKey.SetValue("Icon", $"\"{exe}\"");  // メニュー左のアイコン
                    using var cmd = verbKey.CreateSubKey("command");
                    // "%V" は対象フォルダーのパスに展開される（Directory / Background 双方で有効）
                    cmd?.SetValue(null, $"\"{exe}\" \"%V\"");
                }
            }
            else
            {
                foreach (var root in ShellRoots)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{AppName}", throwOnMissingSubKey: false);
                }
            }
        }
        catch
        {
            // 失敗しても致命的ではない
        }
    }

    // ===== エクスプローラーの既定フォルダーアプリ =====

    /// <summary>フォルダーとドライブの既定の open 動作が Kiriha になっているか。</summary>
    public static bool IsDefaultFolderAppEnabled()
    {
        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            return DefaultOpenClasses.All(className =>
            {
                using var shell = currentUser.OpenSubKey($@"Software\Classes\{className}\shell");
                return string.Equals(shell?.GetValue(null) as string, AppName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// フォルダーとドライブの既定 open verb をユーザー単位で Kiriha に切り替える。
    /// 解除時は、有効化前に HKCU に存在した既定値を復元する。
    /// </summary>
    public static bool SetDefaultFolderAppEnabled(bool enabled)
    {
        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            var exe = ExePath;
            if (enabled && exe is null)
            {
                return false;
            }

            foreach (var className in DefaultOpenClasses)
            {
                var shellPath = $@"Software\Classes\{className}\shell";
                using var shell = currentUser.CreateSubKey(shellPath);
                if (shell is null)
                {
                    return false;
                }

                if (enabled)
                {
                    SaveOriginalDefaultOnce(currentUser, className, shell);
                    using var verb = shell.CreateSubKey(AppName);
                    verb?.SetValue(null, "Kiriha で開く");
                    verb?.SetValue("Icon", $"\"{exe}\"");
                    using var command = verb?.CreateSubKey("command");
                    command?.SetValue(null, $"\"{exe}\" \"%1\"");
                    shell.SetValue(null, AppName);
                }
                else
                {
                    // ユーザーが別のアプリへ変更した後は、その選択を上書きしない。
                    if (string.Equals(shell.GetValue(null) as string, AppName, StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreOriginalDefault(currentUser, className, shell);
                    }

                    shell.DeleteSubKeyTree(AppName, throwOnMissingSubKey: false);
                }
            }

            if (!enabled)
            {
                currentUser.DeleteSubKeyTree(AssociationBackupPath, throwOnMissingSubKey: false);
            }

            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, 0, 0);
            return IsDefaultFolderAppEnabled() == enabled;
        }
        catch (Exception ex)
        {
            Logger.LogException("既定のフォルダーアプリ設定を変更できませんでした", ex);
            return false;
        }
    }

    private static void SaveOriginalDefaultOnce(RegistryKey currentUser, string className, RegistryKey shell)
    {
        using var backup = currentUser.CreateSubKey($@"{AssociationBackupPath}\{className}");
        if (backup is null || backup.GetValue("Captured") is not null)
        {
            return;
        }

        var original = shell.GetValue(null) as string;
        backup.SetValue("Captured", 1, RegistryValueKind.DWord);
        backup.SetValue("HadUserDefault", original is null ? 0 : 1, RegistryValueKind.DWord);
        if (original is not null)
        {
            backup.SetValue("OriginalDefault", original, RegistryValueKind.String);
        }
    }

    private static void RestoreOriginalDefault(RegistryKey currentUser, string className, RegistryKey shell)
    {
        using var backup = currentUser.OpenSubKey($@"{AssociationBackupPath}\{className}");
        if (backup?.GetValue("HadUserDefault") is int hadUserDefault && hadUserDefault == 1
            && backup.GetValue("OriginalDefault") is string original)
        {
            shell.SetValue(null, original);
        }
        else
        {
            shell.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);
}
