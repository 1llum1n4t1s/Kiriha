using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Kiriha.Services;

/// <summary>
/// Windows のアクセントカラーを Fluent テーマの SystemAccentColor 系リソースに注入する。
/// OS 側でアクセントカラー / テーマを変更したときは ColorValuesChanged で即追従する
/// （ダーク / ライト自体は RequestedThemeVariant 未指定で Avalonia が自動追従）。
/// アクリル（半透明ぼかし）有効時は、同じ仕組みで背景ブラシを半透明版に差し替える。
/// </summary>
public static class ThemeService
{
    /// <summary>One Dark はダーク系だが Fluent の既定 Dark とは別配色のため、専用の ThemeVariant を用意する。
    /// InheritVariant を Dark にしておくと、App.axaml に "OneDark" キーが無い FluentTheme 側の内部リソースは
    /// Dark 配色にフォールバックする。全ページで同一インスタンスを参照するため static で共有する。</summary>
    public static readonly ThemeVariant OneDark = new("OneDark", ThemeVariant.Dark);

    private static bool _acrylicEnabled;

    public static void Initialize(Application app, bool acrylicEnabled)
    {
        _acrylicEnabled = acrylicEnabled;
        Apply(app);

        var platformSettings = app.PlatformSettings;
        if (platformSettings is not null)
        {
            platformSettings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => Apply(app));
        }
    }

    /// <summary>設定画面のトグルからアクリル効果の有効/無効を切り替える。</summary>
    public static void SetAcrylicEnabled(Application app, bool enabled)
    {
        _acrylicEnabled = enabled;
        Apply(app);
    }

    private static void Apply(Application app)
    {
        Color accent;
        try
        {
            if (app.PlatformSettings?.GetColorValues() is not { } colors)
            {
                return;
            }

            accent = colors.AccentColor1;
        }
        catch
        {
            return; // 取得できなければ Fluent 既定色のまま
        }

        // Fluent テーマの選択色・チェック色・フォーカスリングなどがアクセントカラーになる
        app.Resources["SystemAccentColor"] = accent;
        app.Resources["SystemAccentColorDark1"] = Shade(accent, 0.82);
        app.Resources["SystemAccentColorDark2"] = Shade(accent, 0.66);
        app.Resources["SystemAccentColorDark3"] = Shade(accent, 0.50);
        app.Resources["SystemAccentColorLight1"] = Tint(accent, 0.18);
        app.Resources["SystemAccentColorLight2"] = Tint(accent, 0.36);
        app.Resources["SystemAccentColorLight3"] = Tint(accent, 0.54);

        // 自前スタイル用
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentSelectionBrush"] = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x20, accent.R, accent.G, accent.B));
        // Lhamiel のダイアログと同じ、ごく薄いアクセント tint（約9%）。
        app.Resources["DialogAccentTintBrush"] = new SolidColorBrush(Color.FromArgb(0x18, accent.R, accent.G, accent.B));

        ApplyAcrylicBackgrounds(app);
    }

    /// <summary>黒方向へ暗くする（factor = 残す明るさの割合）。</summary>
    private static Color Shade(Color c, double factor)
        => Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    /// <summary>白方向へ明るくする。</summary>
    private static Color Tint(Color c, double amount)
        => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));

    // App.axaml の Light/Dark ThemeDictionaries と同じ基準色（アクリル有効時にこの値へアルファを付けて上書きする）。
    private static readonly Color LightTabStripBg = Color.Parse("#DEE1E6");
    private static readonly Color LightContentBg = Color.Parse("#FFFFFF");
    private static readonly Color LightSidebarBg = Color.Parse("#F8F9FA");
    private static readonly Color LightOmniboxBg = Color.Parse("#F1F3F4");
    private static readonly Color LightStatusBg = Color.Parse("#F8F9FA");

    private static readonly Color DarkTabStripBg = Color.Parse("#202124");
    private static readonly Color DarkContentBg = Color.Parse("#2B2C2F");
    private static readonly Color DarkSidebarBg = Color.Parse("#2B2C2F");
    private static readonly Color DarkOmniboxBg = Color.Parse("#2B2C2F");
    private static readonly Color DarkStatusBg = Color.Parse("#2B2C2F");

    private static readonly Color OneDarkTabStripBg = Color.Parse("#21252B");
    private static readonly Color OneDarkContentBg = Color.Parse("#282C34");
    private static readonly Color OneDarkSidebarBg = Color.Parse("#282C34");
    private static readonly Color OneDarkOmniboxBg = Color.Parse("#282C34");
    private static readonly Color OneDarkStatusBg = Color.Parse("#282C34");

    /// <summary>コンテンツ部の半透明度（読みやすさ優先でやや濃いめ）。</summary>
    private const byte ContentAlpha = 0xE6;

    /// <summary>クロム部（タブストリップ/サイドバー/アドレスバー/ステータスバー）の半透明度。</summary>
    private const byte ChromeAlpha = 0xD8;

    private static void ApplyAcrylicBackgrounds(Application app)
    {
        var variant = app.ActualThemeVariant;
        var (tabStrip, content, sidebar, omnibox, status) = variant == OneDark
            ? (OneDarkTabStripBg, OneDarkContentBg, OneDarkSidebarBg, OneDarkOmniboxBg, OneDarkStatusBg)
            : variant == ThemeVariant.Dark
                ? (DarkTabStripBg, DarkContentBg, DarkSidebarBg, DarkOmniboxBg, DarkStatusBg)
                : (LightTabStripBg, LightContentBg, LightSidebarBg, LightOmniboxBg, LightStatusBg);

        // ExperimentalAcrylicMaterial.TintColor は Color 型（Brush ではない）なので専用キーで持つ。
        // TabStripBg 自体は下で透明にすることがあるため、素の基準色はこちらに常時反映する。
        app.Resources["AcrylicTintColor"] = tabStrip;

        // ウィンドウ自体の背景（Window.Background）はアクリル有効時のみ透明にし、
        // ExperimentalAcrylicBorder のぼかしをそのまま見せる。無効時は従来どおり不透明。
        app.Resources["TabStripBg"] = new SolidColorBrush(_acrylicEnabled ? Colors.Transparent : tabStrip);
        // 垂直タブはタイトルバーと同じウィンドウ背景を共有する。OFF時も Window 自体が
        // 不透明な TabStripBg を持つため、ここへ別の面を重ねず質感を連続させる。
        app.Resources["VerticalTabsSurfaceBg"] = new SolidColorBrush(Colors.Transparent);
        app.Resources["ContentBg"] = new SolidColorBrush(WithAlpha(content, _acrylicEnabled ? ContentAlpha : (byte)0xFF));
        app.Resources["SidebarBg"] = new SolidColorBrush(WithAlpha(sidebar, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
        app.Resources["OmniboxBg"] = new SolidColorBrush(WithAlpha(omnibox, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
        app.Resources["StatusBg"] = new SolidColorBrush(WithAlpha(status, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
