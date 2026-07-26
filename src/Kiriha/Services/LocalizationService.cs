using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>UI 文字列のローカライズ。
///
/// XAML 側は <c>{DynamicResource Text.Xxx}</c> で直接参照する。App.axaml が全言語の
/// ResourceDictionary を x:Key 付きで保持しており、<see cref="SetLocale"/> が「現在の言語だけ」を
/// Application.Resources.MergedDictionaries へ差し替えることで、DynamicResource が一斉に更新される。
///
/// C# 側から組み立てる文字列（ステータスバー、エラーメッセージ、動的コンテキストメニュー等）は
/// <see cref="Text(string)"/> / <see cref="Text(string, object?[])"/> で取り出す。こちらは呼び出し時に
/// 解決するため、言語切り替え直後の再表示は <see cref="Changed"/> を購読して再計算する。</summary>
public static class LocalizationService
{
    /// <summary>自動判定にも失敗したときの最終フォールバック。</summary>
    public const string FallbackLocale = "en_US";

    private static readonly string s_detectedLocale = DetectDefaultLocale();

    private static ResourceDictionary? s_activeLocale;

    /// <summary>現在適用中のロケールキー。</summary>
    public static string CurrentLocale { get; private set; } = s_detectedLocale;

    /// <summary>OS の UI 言語から判定した既定ロケール（初回起動時の既定値）。</summary>
    public static string DetectedLocale => s_detectedLocale;

    /// <summary>表示言語が切り替わったときに発火する。C# 側で組み立て済みの文字列を持つ
    /// ビューモデルは、これを購読して再計算する。</summary>
    public static event EventHandler? Changed;

    /// <summary>設定値（空文字なら自動判定）を実際のロケールキーへ解決する。</summary>
    public static string Resolve(string? configured)
        => Locale.IsSupported(configured) ? configured! : s_detectedLocale;

    /// <summary>表示言語を切り替える。App.axaml の ResourceInclude に無いキーは無視する。</summary>
    public static void SetLocale(string? configured)
    {
        var localeKey = Resolve(configured);
        if (Application.Current is not { } app)
        {
            CurrentLocale = localeKey;
            return;
        }

        if (!app.Resources.TryGetResource(localeKey, null, out var resource) ||
            resource is not ResourceDictionary target)
        {
            return;
        }

        if (ReferenceEquals(target, s_activeLocale))
        {
            CurrentLocale = localeKey;
            return;
        }

        if (s_activeLocale is not null)
        {
            app.Resources.MergedDictionaries.Remove(s_activeLocale);
        }

        app.Resources.MergedDictionaries.Add(target);
        s_activeLocale = target;
        CurrentLocale = localeKey;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Avalonia の Application が無い環境（単体テスト）で文字列解決を差し替えるフック。
    /// 通常運用では null のままで、Application.Resources から解決する。</summary>
    internal static Func<string, string?>? ResolverOverride { get; set; }

    /// <summary>キーに対応する現在言語の文字列を返す。見つからなければキー自体を返す
    /// （未翻訳を画面上で発見できるようにするため、例外にはしない）。</summary>
    public static string Text(string key)
    {
        if (ResolverOverride?.Invoke(key) is { } overridden)
        {
            return overridden;
        }

        if (Application.Current is { } app &&
            app.TryFindResource(key, out var value) &&
            value is string text)
        {
            return text;
        }

        return key;
    }

    /// <summary>書式付きの文字列を返す（リソース側は {0} {1} … のプレースホルダーを持つ）。</summary>
    public static string Text(string key, params object?[] args)
    {
        var format = Text(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // 翻訳側のプレースホルダー崩れでアプリを落とさない。書式化前の文字列で妥協する。
            return format;
        }
    }

    /// <summary>OS の UI 言語から、対応言語のうち最も近いものを選ぶ。</summary>
    internal static string DetectDefaultLocale()
    {
        try
        {
            return MatchLocale(CultureInfo.CurrentUICulture);
        }
        catch (CultureNotFoundException)
        {
            return FallbackLocale;
        }
    }

    /// <summary>カルチャ → 対応ロケールキーの対応付け（判定ロジック本体。テストから直接叩く）。</summary>
    internal static string MatchLocale(CultureInfo culture)
    {
        var exact = culture.Name.Replace('-', '_');
        if (Locale.IsSupported(exact))
        {
            return exact;
        }

        var lang = culture.TwoLetterISOLanguageName;

        // 中国語は簡体 / 繁体の判定が地域コードとスクリプトの両方に散るため個別に見る。
        if (lang == "zh")
        {
            var name = culture.Name;
            var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || name.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || name.Contains("MO", StringComparison.OrdinalIgnoreCase);
            return traditional ? "zh_TW" : "zh_CN";
        }

        // フィリピン語は fil / tl の両方の表記が使われる。
        if (lang is "fil" or "tl")
        {
            return "fil_PH";
        }

        // 2 文字キーのロケール（sa / la）は言語コードだけで一致する。
        if (Locale.IsSupported(lang))
        {
            return lang;
        }

        foreach (var locale in Locale.Supported)
        {
            if (locale.Key.StartsWith(lang + "_", StringComparison.Ordinal))
            {
                return locale.Key;
            }
        }

        return FallbackLocale;
    }
}
