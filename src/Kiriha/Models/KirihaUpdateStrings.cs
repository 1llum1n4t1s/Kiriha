using System.ComponentModel;
using Kiriha.Services;
using VelopackUpdateDialog;

namespace Kiriha.Models;

/// <summary>VelopackUpdateDialog.Avalonia が要求する文字列セット。
/// 各プロパティは読むたびに現在の言語で解決し、言語切り替え時は
/// <see cref="LocalizationService.Changed"/> を受けて全プロパティの再取得を通知する。</summary>
public sealed class KirihaUpdateStrings : IUpdateDialogStrings, INotifyPropertyChanged
{
    public static KirihaUpdateStrings Instance { get; } = new();

    private KirihaUpdateStrings()
    {
        LocalizationService.Changed += (_, _) => NotifyLocaleChanged();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>プロパティ名 null で「全プロパティが変わった」と通知する（言語切り替え時）。</summary>
    public void NotifyLocaleChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public string Title => LocalizationService.Text("Text.Update.Title");

    public string AvailableHeader => LocalizationService.Text("Text.Update.AvailableHeader");

    public string DownloadAndInstall => LocalizationService.Text("Text.Update.DownloadAndInstall");

    public string IgnoreThisVersion => LocalizationService.Text("Text.Update.IgnoreThisVersion");

    public string UpToDateMessage => LocalizationService.Text("Text.Update.UpToDate");

    public string ErrorHeader => LocalizationService.Text("Text.Update.ErrorHeader");

    public string Close => LocalizationService.Text("Text.Common.Close");

    public string CheckingMessage => LocalizationService.Text("Text.Update.Checking");
}
