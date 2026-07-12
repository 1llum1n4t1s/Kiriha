using Avalonia;
using Kiriha.Services;
using Velopack;

namespace Kiriha;

internal static class Program
{
    /// <summary>起動引数（フォルダーパスを渡すとそのフォルダーをタブで開く）。</summary>
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;
        Logger.Initialize();

        // Velopack のブートストラップ（インストール / 更新 / アンインストール時のフック処理）。
        // 更新適用直後の再起動などはここで処理され、通常起動時はそのまま通過する。
        VelopackApp.Build().Run();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Logger.LogException("アプリケーション起動エラー", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
