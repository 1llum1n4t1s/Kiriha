using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Kiriha.Models;
using Kiriha.Views;
using Velopack;
using Velopack.Sources;

namespace Kiriha.Services;

/// <summary>
/// Velopack による自動更新（Lhamiel と同方式）。
/// Cloudflare R2 の静的ホスティングを配信元とし、SimpleWebSource が
/// {baseUrl}/releases.{channel}.json と nupkg を取得する。
/// UI は VelopackUpdateDialog.Avalonia がダウンロード / 適用 / 再起動まで誘導する。
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// 更新配信元のベース URL。改ざん防止のためハードコード固定（Lhamiel と同方針）。
    /// </summary>
    public const string CanonicalUpdateBaseUrl = "https://kiriha.nephilim.jp";

    /// <summary>自動チェックのタイムアウト（半開き TCP / DNS 異常で長時間ロックされるのを防ぐ）。</summary>
    private static readonly TimeSpan AutomaticCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>起動時自動チェックと手動チェックの並走を防ぐ先勝ちフラグ。</summary>
    private static int _isChecking;

    /// <summary>Settings から UpdateManager を組み立てる共通ファクトリ。</summary>
    private static UpdateManager BuildUpdateManager()
        => new(new SimpleWebSource(CanonicalUpdateBaseUrl));

    /// <summary>
    /// 更新を確認し、更新がある場合は VelopackUpdateDialog でダウンロード / 適用までを誘導する。
    /// </summary>
    /// <param name="owner">ダイアログのオーナーウィンドウ。</param>
    /// <param name="settings">共有 AppSettings（IgnoreUpdateTag の読み書きに使用）。</param>
    /// <param name="manually">
    /// true: 手動チェック（最新版でも結果ダイアログを表示、無視タグは無視、タイムアウト無し）。
    /// false: 自動チェック（更新がある場合のみ表示、IgnoreUpdateTag と一致したら何も表示しない）。
    /// </param>
    public static void Check4Update(Window? owner, AppSettings settings, bool manually = false)
    {
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var mgr = BuildUpdateManager();

                if (!mgr.IsInstalled)
                {
                    Logger.Log(
                        $"Velopack の IsInstalled=false のため更新チェックをスキップ (開発実行 or manifest 破損の可能性): ProcessPath={Environment.ProcessPath ?? "(unknown)"}",
                        LogLevel.Warning);
                    if (manually && owner is not null)
                    {
                        await ShowInfoAsync(
                            owner, settings.UseAcrylicBackground,
                            "開発環境での実行のため、更新チェックをスキップしました。");
                    }

                    return;
                }

                var options = new VelopackUpdateDialog.UpdateDialogOptions
                {
                    Strings = KirihaUpdateStrings.Instance,
                    IgnoredTagName = settings.IgnoreUpdateTag,
                    // 更新ダイアログ本体も Kiriha のアクリル設定に合わせる。
                    ChromeMode = settings.UseAcrylicBackground
                        ? VelopackUpdateDialog.WindowChromeMode.Custom
                        : VelopackUpdateDialog.WindowChromeMode.System,
                };
                options.VersionIgnored += tag =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        settings.IgnoreUpdateTag = tag;
                        SettingsService.Save(settings);
                        Logger.Log($"IgnoreUpdateTag を保存: {tag}", LogLevel.Warning);
                    });
                options.ErrorOccurred += ex =>
                    Logger.Log($"Velopack 更新失敗: {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);

                Logger.Log($"Velopack 更新チェック開始: manually={manually}, baseUrl={CanonicalUpdateBaseUrl}");

                // 自動チェックのみタイムアウトを設定（手動はユーザーが待てる前提・Close で中断可）
                CancellationTokenSource? autoCts = manually ? null : new CancellationTokenSource(AutomaticCheckTimeout);
                try
                {
                    await VelopackUpdateDialog.UpdateDialogWindow.ShowAsync(
                        owner, mgr, options, manualCheck: manually, autoCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    Logger.Log(
                        $"自動更新チェックがタイムアウトしました（{AutomaticCheckTimeout.TotalSeconds:F0} 秒）",
                        LogLevel.Warning);
                    return;
                }
                finally
                {
                    autoCts?.Dispose();
                    (mgr as IDisposable)?.Dispose();
                }

                Logger.Log($"Velopack 更新チェック完了: manually={manually}");
            }
            catch (Exception ex)
            {
                Logger.LogException("更新チェック失敗", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isChecking, 0);
            }
        });
    }

    private static async Task ShowInfoAsync(Window owner, bool useAcrylic, string message)
    {
        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new ThemedDialogWindow(useAcrylic)
        {
            Title = "Kiriha の更新",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            DialogContent = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                MinWidth = 280,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }
}
