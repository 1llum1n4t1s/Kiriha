using System.ComponentModel;
using VelopackUpdateDialog;

namespace Kiriha.Models;

/// <summary>VelopackUpdateDialog.Avalonia が要求する文字列セット（Kiriha は日本語固定）。</summary>
public sealed class KirihaUpdateStrings : IUpdateDialogStrings, INotifyPropertyChanged
{
    public static KirihaUpdateStrings Instance { get; } = new();

    private KirihaUpdateStrings()
    {
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>言語固定のため通知は使わないが、インターフェース契約として保持する。</summary>
    public void NotifyLocaleChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public string Title => "Kiriha の更新";

    public string AvailableHeader => "新しいバージョンが利用可能です";

    public string DownloadAndInstall => "ダウンロードしてインストール";

    public string IgnoreThisVersion => "このバージョンをスキップ";

    public string UpToDateMessage => "お使いのバージョンは最新です";

    public string ErrorHeader => "更新の確認に失敗しました";

    public string Close => "閉じる";

    public string CheckingMessage => "更新を確認しています...";
}
