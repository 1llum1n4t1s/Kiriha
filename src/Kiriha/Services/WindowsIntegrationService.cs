using Microsoft.Win32;

namespace Kiriha.Services;

/// <summary>
/// Windows へのスタートアップ登録 / エクスプローラー右クリックメニュー登録を扱う。
/// すべて HKCU（現在のユーザー）配下への書き込みで完結し、管理者権限は不要。
/// 設定の真実の源はレジストリ側にあり、設定画面のチェック状態は現在のレジストリ状態を都度読んで反映する。
/// </summary>
public static class WindowsIntegrationService
{
    private const string AppName = "Kiriha";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MenuLabel = "Kiriha で開く";

    // フォルダー本体の右クリックと、フォルダー内の空白（背景）の右クリックの両方に登録する。
    private static readonly string[] ShellRoots =
    {
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\Background\shell",
    };

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
}
