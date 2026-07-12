using System.Diagnostics;

namespace Kiriha.Services;

/// <summary>閲覧中フォルダーを検索対象にせず、信頼できる実行ファイルだけを起動する。</summary>
internal static class TrustedProcessLauncher
{
    public static void Start(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var executable = ResolveExecutable(fileName);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
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
