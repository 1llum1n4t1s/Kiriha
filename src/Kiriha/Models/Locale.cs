namespace Kiriha.Models;

/// <summary>UI 表示言語（ロケール）。設定画面のドロップダウンにそのまま並べる。</summary>
public sealed class Locale
{
    /// <summary>ドロップダウンに出す表示名。各言語のネイティブ表記で書く。</summary>
    public string Name { get; }

    /// <summary>ロケールキー（例: "ja_JP"）。Assets/Locales/&lt;Key&gt;.axaml と App.axaml の
    /// ResourceInclude の x:Key に一致させる。settings.json にもこの文字列で保存する。</summary>
    public string Key { get; }

    public Locale(string name, string key)
    {
        Name = name;
        Key = key;
    }

    /// <summary>対応言語の一覧（設定ドロップダウンの表示順）。
    /// ここへ追加したら Assets/Locales に同名の axaml を置き、App.axaml にも ResourceInclude を足す。</summary>
    public static readonly IReadOnlyList<Locale> Supported =
    [
        new Locale("日本語", "ja_JP"),
        new Locale("English", "en_US"),
        new Locale("Deutsch", "de_DE"),
        new Locale("Español", "es_ES"),
        new Locale("Français", "fr_FR"),
        new Locale("Italiano", "it_IT"),
        new Locale("Português (Brasil)", "pt_BR"),
        new Locale("Bahasa Indonesia", "id_ID"),
        new Locale("Filipino (Tagalog)", "fil_PH"),
        new Locale("Українська", "uk_UA"),
        new Locale("Русский", "ru_RU"),
        new Locale("简体中文", "zh_CN"),
        new Locale("繁體中文", "zh_TW"),
        new Locale("한국어", "ko_KR"),
        new Locale("தமிழ் (Tamil)", "ta_IN"),
        new Locale("संस्कृतम् (Sanskrit)", "sa"),
        new Locale("Latina (Latin)", "la"),
    ];

    /// <summary>ロケールキーが対応一覧に含まれるか。</summary>
    public static bool IsSupported(string? key)
        => key is not null && Supported.Any(l => string.Equals(l.Key, key, StringComparison.Ordinal));
}
