using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// 右ボタンドラッグの「ショートカットをここに作成」で作る .lnk のファイル名規則。
/// エクスプローラーと同じく「元の名前 - ショートカット.lnk」、重複時は「 (2)」を足す。
/// </summary>
public class ShellLinkTests
{
    private const string Dest = @"C:\dest";

    [Fact]
    public void ファイル名は拡張子を残したままショートカット接尾辞を付ける()
    {
        var name = ShellLinkService.BuildShortcutFileName(@"C:\src\report.txt", Dest, _ => false);

        Assert.Equal("report.txt - ショートカット.lnk", name);
    }

    [Fact]
    public void フォルダーは末尾の区切り文字を無視してフォルダー名を使う()
    {
        var name = ShellLinkService.BuildShortcutFileName(@"C:\src\photos\", Dest, _ => false);

        Assert.Equal("photos - ショートカット.lnk", name);
    }

    [Fact]
    public void ドライブ直下はドライブ文字を名前にする()
    {
        var name = ShellLinkService.BuildShortcutFileName(@"D:\", Dest, _ => false);

        Assert.Equal("D - ショートカット.lnk", name);
    }

    [Fact]
    public void 既存のショートカットがあれば連番を足す()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\dest\report.txt - ショートカット.lnk",
            @"C:\dest\report.txt - ショートカット (2).lnk",
        };

        var name = ShellLinkService.BuildShortcutFileName(@"C:\src\report.txt", Dest, existing.Contains);

        Assert.Equal("report.txt - ショートカット (3).lnk", name);
    }

    [Fact]
    public void 返すのはファイル名だけでディレクトリを含まない()
    {
        var name = ShellLinkService.BuildShortcutFileName(@"C:\src\report.txt", Dest, _ => false);

        Assert.DoesNotContain(@"\", name);
    }
}
