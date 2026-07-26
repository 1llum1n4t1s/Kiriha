using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kiriha.Models;
using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// テストプロセスには Avalonia の Application が無いため、そのままでは
/// <see cref="LocalizationService.Text(string)"/> がキー文字列を返してしまう。
/// リポジトリ内の ja_JP.axaml を読んで解決フックへ差し込み、実運用と同じ日本語表示を再現する
/// （利用者向け文言を検証する既存テストの前提を保つため）。
/// </summary>
internal static class LocaleTestBootstrap
{
    [ModuleInitializer]
    internal static void UseJapaneseResources()
    {
        var path = LocaleResourceFiles.Find("ja_JP");
        if (path is null)
        {
            return;
        }

        var table = LocaleResourceFiles.ReadEntries(path);
        LocalizationService.ResolverOverride = key => table.TryGetValue(key, out var value) ? value : null;
    }
}

/// <summary>Assets/Locales の axaml をテストから直接読むためのヘルパー。</summary>
internal static class LocaleResourceFiles
{
    private static readonly Regex EntryPattern =
        new("x:Key=\"([^\"]+)\"[^>]*>(.*?)</x:String>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>ビルド出力から遡ってリポジトリ内の Assets/Locales を探す。</summary>
    public static string? Directory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Kiriha", "Assets", "Locales");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static string? Find(string localeKey)
    {
        var directory = Directory();
        if (directory is null)
        {
            return null;
        }

        var path = Path.Combine(directory, localeKey + ".axaml");
        return File.Exists(path) ? path : null;
    }

    /// <summary>キー → 表示文字列。XML の実体参照は元の文字へ戻す。</summary>
    public static Dictionary<string, string> ReadEntries(string path)
        => EntryPattern.Matches(File.ReadAllText(path)).ToDictionary(
            m => m.Groups[1].Value,
            m => m.Groups[2].Value
                .Replace("&#10;", "\n", StringComparison.Ordinal)
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal)
                .Replace("&quot;", "\"", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.Ordinal),
            StringComparer.Ordinal);
}

/// <summary>
/// ロケール定義と自動判定のテスト。表示文字列の解決自体は Avalonia の Application が必要なので、
/// ここでは純ロジック（対応言語一覧・カルチャ判定）と、リソースファイルのキー網羅を確認する。
/// </summary>
public sealed class LocaleDefinitionTests
{
    [Fact]
    public void 対応言語はKomorebiと同じ17言語ある()
    {
        Assert.Equal(17, Locale.Supported.Count);
    }

    [Fact]
    public void ロケールキーは重複しない()
    {
        var keys = Locale.Supported.Select(l => l.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 表示名とキーはどちらも空ではない()
    {
        Assert.All(Locale.Supported, locale =>
        {
            Assert.False(string.IsNullOrWhiteSpace(locale.Name));
            Assert.False(string.IsNullOrWhiteSpace(locale.Key));
        });
    }

    [Theory]
    [InlineData("ja_JP")]
    [InlineData("en_US")]
    [InlineData("sa")]
    [InlineData("la")]
    public void 対応キーはIsSupportedが真を返す(string key)
    {
        Assert.True(Locale.IsSupported(key));
    }

    [Theory]
    [InlineData("ja-JP")]   // ハイフン区切りは内部表現ではない
    [InlineData("xx_YY")]
    [InlineData("")]
    [InlineData(null)]
    public void 未対応キーはIsSupportedが偽を返す(string? key)
    {
        Assert.False(Locale.IsSupported(key));
    }
}

public sealed class LocaleDetectionTests
{
    [Theory]
    [InlineData("ja-JP", "ja_JP")]
    [InlineData("en-US", "en_US")]
    [InlineData("de-DE", "de_DE")]
    [InlineData("pt-BR", "pt_BR")]
    [InlineData("ru-RU", "ru_RU")]
    [InlineData("ko-KR", "ko_KR")]
    public void 完全一致するカルチャはそのまま対応する(string culture, string expected)
    {
        Assert.Equal(expected, LocalizationService.MatchLocale(new CultureInfo(culture)));
    }

    [Theory]
    [InlineData("ja", "ja_JP")]          // 言語コードだけでも地域付きキーへ寄せる
    [InlineData("de-AT", "de_DE")]       // 同一言語の別地域
    [InlineData("en-GB", "en_US")]
    [InlineData("pt-PT", "pt_BR")]
    [InlineData("es-MX", "es_ES")]
    public void 地域違いは同一言語のキーへ寄せる(string culture, string expected)
    {
        Assert.Equal(expected, LocalizationService.MatchLocale(new CultureInfo(culture)));
    }

    [Theory]
    [InlineData("zh-CN", "zh_CN")]
    [InlineData("zh-SG", "zh_CN")]
    [InlineData("zh-Hans", "zh_CN")]
    [InlineData("zh-TW", "zh_TW")]
    [InlineData("zh-HK", "zh_TW")]
    [InlineData("zh-MO", "zh_TW")]
    [InlineData("zh-Hant", "zh_TW")]
    public void 中国語は簡体と繁体を判別する(string culture, string expected)
    {
        Assert.Equal(expected, LocalizationService.MatchLocale(new CultureInfo(culture)));
    }

    [Theory]
    [InlineData("fil-PH")]
    [InlineData("fil")]
    public void フィリピン語はfilPHへ対応する(string culture)
    {
        Assert.Equal("fil_PH", LocalizationService.MatchLocale(new CultureInfo(culture)));
    }

    [Theory]
    [InlineData("sa", "sa")]
    [InlineData("sa-IN", "sa")]
    [InlineData("la", "la")]
    public void 二文字キーの言語も判別できる(string culture, string expected)
    {
        Assert.Equal(expected, LocalizationService.MatchLocale(new CultureInfo(culture)));
    }

    [Theory]
    [InlineData("sv-SE")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void 未対応の言語は英語へフォールバックする(string culture)
    {
        Assert.Equal(LocalizationService.FallbackLocale, LocalizationService.MatchLocale(new CultureInfo(culture)));
    }

    [Fact]
    public void 自動判定結果は必ず対応言語のいずれかになる()
    {
        Assert.True(Locale.IsSupported(LocalizationService.DetectedLocale));
    }

    [Theory]
    [InlineData("")]      // 空文字 = 初回インストール直後の「自動判定」
    [InlineData(null)]
    [InlineData("xx_YY")] // 壊れた設定値
    public void 未設定や不正値は自動判定結果へ解決される(string? configured)
    {
        Assert.Equal(LocalizationService.DetectedLocale, LocalizationService.Resolve(configured));
    }

    [Fact]
    public void 明示指定されたロケールはそのまま解決される()
    {
        Assert.Equal("de_DE", LocalizationService.Resolve("de_DE"));
    }
}

/// <summary>
/// Assets/Locales の各言語ファイルが同じキー集合を持つことを確かめる。
/// 1 言語だけキーが欠けると、その言語でだけ画面にキー文字列が生で出てしまうため。
/// </summary>
public sealed class LocaleResourceTests
{
    private static readonly Regex KeyPattern = new("x:Key=\"([^\"]+)\"", RegexOptions.Compiled);

    private static string? FindLocalesDirectory() => LocaleResourceFiles.Directory();

    private static Dictionary<string, string[]> LoadAllKeys(string directory)
        => Directory.GetFiles(directory, "*.axaml")
            .ToDictionary(
                path => Path.GetFileNameWithoutExtension(path)!,
                path => KeyPattern.Matches(File.ReadAllText(path))
                    .Select(m => m.Groups[1].Value)
                    .ToArray(),
                StringComparer.Ordinal);

    [Fact]
    public void 対応言語ごとにリソースファイルが存在する()
    {
        var directory = FindLocalesDirectory();
        Assert.NotNull(directory);

        var files = LoadAllKeys(directory!);
        Assert.All(Locale.Supported, locale => Assert.True(
            files.ContainsKey(locale.Key), $"{locale.Key}.axaml がありません"));
        Assert.Equal(Locale.Supported.Count, files.Count);
    }

    [Fact]
    public void 全言語のキー集合が一致し重複もない()
    {
        var directory = FindLocalesDirectory();
        Assert.NotNull(directory);

        var files = LoadAllKeys(directory!);
        var baseline = files["ja_JP"];
        Assert.NotEmpty(baseline);

        foreach (var (name, keys) in files)
        {
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
            var missing = baseline.Except(keys, StringComparer.Ordinal).ToArray();
            var extra = keys.Except(baseline, StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0, $"{name}: 不足キー {string.Join(", ", missing)}");
            Assert.True(extra.Length == 0, $"{name}: 余分なキー {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void 書式プレースホルダーの個数が全言語で揃っている()
    {
        var directory = FindLocalesDirectory();
        Assert.NotNull(directory);

        var placeholder = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);
        var entryPattern = new Regex("x:Key=\"([^\"]+)\"[^>]*>(.*?)</x:String>", RegexOptions.Compiled | RegexOptions.Singleline);

        Dictionary<string, int> Load(string localeKey)
            => entryPattern.Matches(File.ReadAllText(Path.Combine(directory!, localeKey + ".axaml")))
                .ToDictionary(
                    m => m.Groups[1].Value,
                    m => placeholder.Matches(m.Groups[2].Value).Select(p => p.Value).Distinct().Count(),
                    StringComparer.Ordinal);

        var baseline = Load("ja_JP");
        foreach (var locale in Locale.Supported.Where(l => l.Key != "ja_JP"))
        {
            var actual = Load(locale.Key);
            foreach (var (key, count) in baseline)
            {
                Assert.True(actual[key] == count,
                    $"{locale.Key} の {key}: プレースホルダー数が {count} ではなく {actual[key]}");
            }
        }
    }
}
