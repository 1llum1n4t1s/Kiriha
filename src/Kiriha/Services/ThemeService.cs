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

    /// <summary>X（旧 Twitter）の「Dim」と同じ、紺寄りのダーク。OneDark と同じ理由で専用の
    /// ThemeVariant を用意する（カスタムバリアントは x:Static でしか XAML から参照できない）。</summary>
    public static readonly ThemeVariant Dim = new("Dim", ThemeVariant.Dark);

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

        // Win32 クラシックメニュー（シェルコンテキストメニュー等）もアプリの実効テーマへ追従させる。
        // システム設定時は ActualThemeVariant が OS 設定に解決されるため、その結果に合わせる。
        ApplyClassicMenuTheme(app);
        app.ActualThemeVariantChanged += (_, _) => ApplyClassicMenuTheme(app);
    }

    private static void ApplyClassicMenuTheme(Application app)
    {
        var variant = app.ActualThemeVariant;
        ClassicMenuThemeService.SetDark(variant == ThemeVariant.Dark || variant == OneDark || variant == Dim);
    }

    /// <summary>設定画面のトグルからアクリル効果の有効/無効を切り替える。</summary>
    public static void SetAcrylicEnabled(Application app, bool enabled)
    {
        _acrylicEnabled = enabled;
        Apply(app);
    }

    /// <summary>アクリル効果が有効か。ポップアップ側（独自コンテキストメニュー）が
    /// 自分のウィンドウにも同じ透明度設定を掛けるかの判断に使う。</summary>
    public static bool IsAcrylicEnabled => _acrylicEnabled;

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
    // ライトだけは帯（TabActiveBg）が白なので、明るくすると逆に埋もれる。
    // 暗い系と同じ分離感を出すため、白から離す向き（少し沈める向き）へ同じくらい動かす。
    private static readonly Color LightOmniboxBg = Color.Parse("#E6E9EC");
    private static readonly Color LightStatusBg = Color.Parse("#F8F9FA");
    // アドレスバー帯 / お気に入りバー / タブのドラッグゴーストが共有する面。
    // ライトだけ TabStripBg（#DEE1E6）と別色なので、流用せず個別に持つ。
    private static readonly Color LightTabActiveBg = Color.Parse("#FFFFFF");

    private static readonly Color DarkTabStripBg = Color.Parse("#202124");
    private static readonly Color DarkContentBg = Color.Parse("#2B2C2F");
    private static readonly Color DarkSidebarBg = Color.Parse("#2B2C2F");
    // アドレスバーの入力欄と検索ボックス。帯（TabActiveBg）の上に乗る面なので、
    // 本文と同色だと沈んで見える。Dim と同じ「帯からの差（+36/+36/+39）」だけ明るくして、
    // どのテーマでも同じ分離感にそろえる（絶対値をそろえると各テーマの色味が壊れる）。
    private static readonly Color DarkOmniboxBg = Color.Parse("#44454B");
    private static readonly Color DarkStatusBg = Color.Parse("#2B2C2F");
    private static readonly Color DarkTabActiveBg = Color.Parse("#202124");

    private static readonly Color OneDarkTabStripBg = Color.Parse("#21252B");
    private static readonly Color OneDarkContentBg = Color.Parse("#282C34");
    private static readonly Color OneDarkSidebarBg = Color.Parse("#282C34");
    private static readonly Color OneDarkOmniboxBg = Color.Parse("#454952");
    private static readonly Color OneDarkStatusBg = Color.Parse("#282C34");
    private static readonly Color OneDarkTabActiveBg = Color.Parse("#21252B");

    /// <summary>ライトのアクリル時だけ使う地色。通常の #DEE1E6 は無彩色なので、ぼかしで暗い壁紙を
    /// 拾うとただのくすんだ灰色に見えてしまう。青をわずかに足して、沈んでも淡い青白に見えるようにする。</summary>
    private static readonly Color LightAcrylicBase = Color.Parse("#E4EEFC");

    // 設定タブの地色。App.axaml の SettingsPageBg と同じ基準色（暗い系は NeutralBase）。
    // ContentBg と同じアルファを掛けないと、アクリル有効時に設定タブだけ不透明で浮く。
    private static readonly Color LightSettingsPageBg = Color.Parse("#F3F6FA");
    private static readonly Color DarkSettingsPageBg = Color.Parse("#202124");
    private static readonly Color OneDarkSettingsPageBg = Color.Parse("#21252B");
    private static readonly Color DimSettingsPageBg = Color.Parse("#15202B");

    private static readonly Color DimTabStripBg = Color.Parse("#15202B");
    private static readonly Color DimContentBg = Color.Parse("#1E2732");
    private static readonly Color DimSidebarBg = Color.Parse("#1E2732");
    private static readonly Color DimOmniboxBg = Color.Parse("#394452");
    private static readonly Color DimStatusBg = Color.Parse("#1E2732");
    private static readonly Color DimTabActiveBg = Color.Parse("#15202B");

    // アクリル時の不透明度。ぼかしの質感は残しつつ、透ける部分が壁紙の色に染まって
    // 不透明な面から浮かないよう、全体に濃いめへ寄せてある。
    // （ExperimentalAcrylicMaterial の MaterialOpacity / TintOpacity は Win32 の
    //  ACCENT_ENABLE_ACRYLICBLURBEHIND 経路ではほとんど効かない。0.85→0.97 に上げても
    //  実測で 1 段しか変わらなかったため、濃さの調整はこちらのアルファで行う。）

    /// <summary>コンテンツ部の不透明度（文字を読む面なので最も濃い）。</summary>
    private const byte ContentAlpha = 0x99;

    /// <summary>クロム部（サイドバー/アドレスバー/ステータスバー）の不透明度。</summary>
    private const byte ChromeAlpha = 0x99;

    /// <summary>ぼかしの上に敷くテーマ色の膜の不透明度。タブストリップや垂直タブの背後など、
    /// 面が乗らない場所の濃さはこれで決まる。0 にすると壁紙のぼかしがそのまま出て、
    /// 不透明な面と明らかに色が違って見えるので、テーマの色味は残る程度に留める。</summary>
    private const byte ScrimAlpha = 0x99;

    /// <summary>独自コンテキストメニューの地色の不透明度。ぼかしの質感は残しつつ、
    /// 背後のファイル一覧の文字が透けて読めてしまわない濃さにする（本文の面より少し濃い）。</summary>
    private const byte MenuAlpha = 0x99;

    private static void ApplyAcrylicBackgrounds(Application app)
    {
        var variant = app.ActualThemeVariant;
        var (tabStrip, content, sidebar, omnibox, status, tabActive) = variant == OneDark
            ? (OneDarkTabStripBg, OneDarkContentBg, OneDarkSidebarBg, OneDarkOmniboxBg, OneDarkStatusBg, OneDarkTabActiveBg)
            : variant == Dim
                ? (DimTabStripBg, DimContentBg, DimSidebarBg, DimOmniboxBg, DimStatusBg, DimTabActiveBg)
                : variant == ThemeVariant.Dark
                    ? (DarkTabStripBg, DarkContentBg, DarkSidebarBg, DarkOmniboxBg, DarkStatusBg, DarkTabActiveBg)
                    : (LightTabStripBg, LightContentBg, LightSidebarBg, LightOmniboxBg, LightStatusBg, LightTabActiveBg);

        // ライトのアクリルだけは地色を淡い青白へ寄せる（LightAcrylicBase の説明を参照）。
        // 暗い系テーマはぼかしを拾っても色味が保たれるので、そのままテーマの地色を使う。
        var isDark = variant == ThemeVariant.Dark || variant == OneDark || variant == Dim;
        var acrylicBase = _acrylicEnabled && !isDark ? LightAcrylicBase : tabStrip;

        // ExperimentalAcrylicMaterial.TintColor は Color 型（Brush ではない）なので専用キーで持つ。
        // TabStripBg 自体は下で透明にすることがあるため、素の基準色はこちらに常時反映する。
        app.Resources["AcrylicTintColor"] = acrylicBase;

        // ウィンドウ自体の背景（Window.Background）はアクリル有効時のみ透明にし、
        // ExperimentalAcrylicBorder のぼかしをそのまま見せる。無効時は従来どおり不透明。
        app.Resources["TabStripBg"] = new SolidColorBrush(_acrylicEnabled ? Colors.Transparent : tabStrip);

        // ぼかしの上に敷くテーマ色の膜（MainWindow.axaml の Border が使う）。
        // アクリル部分が壁紙の色に染まって不透明な面から浮くのを防ぐ。
        app.Resources["AcrylicScrimBrush"] = new SolidColorBrush(
            _acrylicEnabled ? WithAlpha(acrylicBase, ScrimAlpha) : Colors.Transparent);
        // 垂直タブはタイトルバーと同じウィンドウ背景を共有する。OFF時も Window 自体が
        // 不透明な TabStripBg を持つため、ここへ別の面を重ねず質感を連続させる。
        app.Resources["VerticalTabsSurfaceBg"] = new SolidColorBrush(Colors.Transparent);
        app.Resources["ContentBg"] = new SolidColorBrush(WithAlpha(content, _acrylicEnabled ? ContentAlpha : (byte)0xFF));
        // 設定タブもファイル一覧と同じ「本文の面」。App.axaml の静的な不透明ブラシのままだと
        // アクリル有効時にここだけ濃さが揃わず、コンテンツ島から浮いて見える。
        var settingsPage = variant == OneDark
            ? OneDarkSettingsPageBg
            : variant == Dim
                ? DimSettingsPageBg
                : variant == ThemeVariant.Dark
                    ? DarkSettingsPageBg
                    : LightSettingsPageBg;
        app.Resources["SettingsPageBg"] = new SolidColorBrush(
            WithAlpha(settingsPage, _acrylicEnabled ? ContentAlpha : (byte)0xFF));
        // 独自コンテキストメニューの地色。ContentBg をそのまま使うと、メニューは別ウィンドウ
        // （PopupRoot）なので背後にぼかし面が無く、半透明がただの素通しになって文字が読めない。
        // メニュー自身がアクリルを敷いたうえで、本文より濃いこの膜を重ねる。
        app.Resources["MenuBg"] = new SolidColorBrush(WithAlpha(content, _acrylicEnabled ? MenuAlpha : (byte)0xFF));
        // メニューのぼかし面の表示切替。ポップアップは View から手が届かないので、
        // DynamicResource の bool を IsVisible に直接バインドして切り替える。
        app.Resources["MenuAcrylicVisible"] = _acrylicEnabled;
        // アドレスバー帯・お気に入りバー・タブのドラッグゴーストが共有する面。役割としては
        // サイドバーやステータスバーと同じクロムなので、同じ ChromeAlpha でそろえる。
        // ここを静的な不透明色のままにすると、他を薄くしたときにこの帯だけ 100% で残って浮く。
        app.Resources["TabActiveBg"] = new SolidColorBrush(WithAlpha(tabActive, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
        app.Resources["SidebarBg"] = new SolidColorBrush(WithAlpha(sidebar, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
        app.Resources["OmniboxBg"] = new SolidColorBrush(WithAlpha(omnibox, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
        app.Resources["StatusBg"] = new SolidColorBrush(WithAlpha(status, _acrylicEnabled ? ChromeAlpha : (byte)0xFF));
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
