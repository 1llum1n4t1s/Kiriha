using Kiriha.ViewModels;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// アドレスバーに打ち込まれた文字列の解決（<see cref="TabViewModel.ResolveTypedPath"/>）。
///
/// 「C: はドライブ直下」はエクスプローラーの挙動であり、.NET の既定とは違う。
/// <c>new DirectoryInfo("C:")</c> はドライブ直下ではなくカレントディレクトリを指すため、
/// ここを素通しすると実行ファイルの場所が開いてしまう（実測で確認済み）。
/// </summary>
public class TypedPathResolutionTests
{
    [Theory]
    [InlineData("C:")]
    [InlineData("c:")]
    [InlineData("D:")]
    public void ドライブ相対表記はドライブ直下になる(string input)
    {
        var resolved = TabViewModel.ResolveTypedPath(input, @"C:\Users\IMT\dev");

        Assert.Equal(input + @"\", resolved);
    }

    [Fact]
    public void 相対パスは表示中のフォルダーを基準に解決する()
    {
        var resolved = TabViewModel.ResolveTypedPath("docs", @"C:\Users\IMT\dev");

        Assert.Equal(@"C:\Users\IMT\dev\docs", resolved);
    }

    [Fact]
    public void 親への相対指定も表示中のフォルダーを基準にする()
    {
        var resolved = TabViewModel.ResolveTypedPath(@"..\Kiriha", @"C:\Users\IMT\dev\Rinkaku");

        Assert.Equal(@"C:\Users\IMT\dev\Kiriha", resolved);
    }

    [Fact]
    public void 絶対パスは正規化されるが指す先は変わらない()
    {
        var resolved = TabViewModel.ResolveTypedPath(@"C:\Users\IMT\dev\Rinkaku\..\Kiriha", @"C:\Users\IMT");

        Assert.Equal(@"C:\Users\IMT\dev\Kiriha", resolved);
    }

    [Fact]
    public void 末尾の区切りは落とすがドライブ直下は保つ()
    {
        Assert.Equal(@"C:\Users\IMT", TabViewModel.ResolveTypedPath(@"C:\Users\IMT\", @"C:\"));
        Assert.Equal(@"C:\", TabViewModel.ResolveTypedPath(@"C:\", @"C:\Users"));
    }

    [Fact]
    public void PC表示中は相対パスの基準が無くても落ちない()
    {
        var resolved = TabViewModel.ResolveTypedPath("docs", "");

        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void 解決できない文字列はそのまま返す()
    {
        var input = "a\0b";

        Assert.Equal(input, TabViewModel.ResolveTypedPath(input, @"C:\Users\IMT"));
    }
}
