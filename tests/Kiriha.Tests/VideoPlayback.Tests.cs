using Kiriha.Services;
using Kiriha.ViewModels;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// ギャラリー動画再生のうち、Media Foundation を起こさずに確かめられる部分のテスト。
/// 実際の再生（COM の生成・フレーム転送）は Avalonia の Application と実ファイルが要るので、
/// ここでは対象拡張子の判定と、コントロールバーに出す時間表記だけを固定する。
/// </summary>
public sealed class VideoPlaybackExtensionTests
{
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".mkv")]
    [InlineData(".avi")]
    [InlineData(".mov")]
    [InlineData(".wmv")]
    [InlineData(".webm")]
    [InlineData(".mpg")]
    [InlineData(".mpeg")]
    [InlineData(".mts")]
    [InlineData(".m2ts")]
    public void 動画拡張子は再生対象になる(string extension)
    {
        Assert.True(VideoPlaybackSession.IsPlayable(extension));
    }

    [Theory]
    [InlineData(".MP4")]
    [InlineData(".MkV")]
    public void 拡張子の大小文字は区別しない(string extension)
    {
        Assert.True(VideoPlaybackSession.IsPlayable(extension));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".pdf")]
    [InlineData(".mp3")]   // 音声だけのファイルはギャラリーの再生対象にしていない
    [InlineData("mp4")]    // ドット無しは拡張子として扱わない
    [InlineData("")]
    public void 動画以外は再生対象にしない(string extension)
    {
        Assert.False(VideoPlaybackSession.IsPlayable(extension));
    }
}

public sealed class VideoDurationFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(95, "1:35")]
    [InlineData(599, "9:59")]
    [InlineData(600, "10:00")]
    public void 一時間未満は分と秒で表記する(double seconds, string expected)
    {
        Assert.Equal(expected, TabViewModel.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(7325, "2:02:05")]
    public void 一時間以上は時も出す(double seconds, string expected)
    {
        Assert.Equal(expected, TabViewModel.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 不正な秒数は0として扱う(double seconds)
    {
        // 尺が取れていない間（NaN / 無限大）でも表示が壊れないこと
        Assert.Equal("0:00", TabViewModel.FormatDuration(seconds));
    }

    [Fact]
    public void 端数は切り捨てて秒を表示する()
    {
        Assert.Equal("0:05", TabViewModel.FormatDuration(5.9));
    }
}
