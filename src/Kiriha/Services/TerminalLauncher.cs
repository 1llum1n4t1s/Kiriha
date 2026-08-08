using System.ComponentModel;

namespace Kiriha.Services;

/// <summary>
/// 指定フォルダーをターミナルで開く。Windows Terminal を優先し、無ければコマンドプロンプトへ落とす。
/// </summary>
/// <remarks>
/// コマンドバーのボタン・「…」メニュー・右クリックメニューの 3 か所から呼ばれる唯一の入口。
/// どこから開いても <see cref="RunAsAdmin"/> の扱いが同じになるよう、分岐はここだけに置く。
/// </remarks>
internal static class TerminalLauncher
{
    /// <summary>ユーザーが UAC の昇格ダイアログをキャンセルしたときの Win32 エラー（ERROR_CANCELLED）。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// ターミナルを管理者として起動するか（設定「ターミナルを管理者として開く」、既定 OFF）。
    /// <see cref="AppSettings.RunTerminalAsAdmin"/> を正本に、起動時と設定変更時に
    /// <c>MainWindowViewModel</c> が書き込む（鮮鋭化の <see cref="ContrastAdaptiveSharpenService.Enabled"/> と同じ持ち方）。
    /// </summary>
    /// <remarks>
    /// これを ON にしても Kiriha 自身は昇格しない。昇格が要るのは起動されるターミナルだけで、
    /// Kiriha を管理者で動かすとエクスプローラーからのドラッグ＆ドロップが UIPI で塞がれるなど実害が出る。
    /// </remarks>
    public static bool RunAsAdmin { get; set; }

    /// <summary>
    /// <paramref name="folderPath"/> をカレントディレクトリにしてターミナルを開く。
    /// 成功したら null、失敗したら画面に出すメッセージを返す。
    /// UAC のキャンセルは「ユーザーがやめた」だけなので失敗扱いにせず null を返す。
    /// </summary>
    public static string? TryOpen(string folderPath)
    {
        if (folderPath.Length == 0 || folderPath == FileSystemService.ComputerPath)
        {
            return null;
        }

        var admin = RunAsAdmin;

        // Windows Terminal。-d は開始ディレクトリの指定で、エクスプローラーの「ターミナルで開く」と同じ。
        try
        {
            TrustedProcessLauncher.Start("wt.exe", ["-d", folderPath], folderPath, admin);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // 昇格を断られた。ここで cmd.exe へ落ちると UAC をもう一度出すことになるので何もしない。
            return null;
        }
        catch (Exception ex)
        {
            // 未インストール（TrustedProcessLauncher が固定パスを見つけられず FileNotFoundException）が主。
            // 昇格に失敗した場合もここへ来るので、どちらでもコマンドプロンプトで代替する。
            Logger.Log($"Windows Terminal を起動できませんでした（コマンドプロンプトで開きます）: {ex.Message}", LogLevel.Debug);
        }

        try
        {
            TrustedProcessLauncher.Start("cmd.exe", [], folderPath, admin);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
