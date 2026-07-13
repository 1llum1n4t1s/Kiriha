using System.Diagnostics;

namespace Kiriha.Services;

/// <summary>閲覧中フォルダーを検索対象にせず、信頼できる実行ファイルだけを起動する。</summary>
internal static class TrustedProcessLauncher
{
    // cmd.exe の第2段解釈でコマンド連結・リダイレクトを許す文字。バッチファイル(.cmd/.bat)を起動すると
    // Windows は内部で cmd.exe を介するため、ArgumentList はこれらをエスケープできない（BatBadBut クラス）。
    private static readonly char[] CmdShellMetaChars = ['&', '|', '<', '>', '\n', '\r'];

    public static void Start(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var executable = ResolveExecutable(fileName);
        var args = arguments.ToArray();

        // 解決先がバッチファイルの場合、外部由来のファイル名/パスに cmd メタ文字が含まれていると
        // 任意コマンド実行に化けうるため、そうした引数を含む起動は拒否する（多重防御）。
        var isBatch = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                      || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (isBatch && Array.Exists(args, a => a.IndexOfAny(CmdShellMetaChars) >= 0))
        {
            throw new InvalidOperationException(
                $"安全でない文字を含む引数のため、バッチファイルの起動を中止しました: {executable}");
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info);
    }

    private static string ResolveExecutable(string fileName)
    {
        var systemDirectory = Environment.SystemDirectory;
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var trustedSystemPath = fileName.ToLowerInvariant() switch
        {
            "cmd.exe" => Path.Combine(systemDirectory, "cmd.exe"),
            "notepad.exe" => Path.Combine(systemDirectory, "notepad.exe"),
            "powershell.exe" => Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "explorer.exe" => Path.Combine(windowsDirectory, "explorer.exe"),
            _ => null,
        };
        if (trustedSystemPath is not null && File.Exists(trustedSystemPath))
        {
            return trustedSystemPath;
        }

        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
        {
            return Path.GetFullPath(fileName);
        }

        var extensions = Path.HasExtension(fileName)
            ? [""]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), fileName + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new FileNotFoundException($"実行ファイルが見つかりません: {fileName}");
    }
}
