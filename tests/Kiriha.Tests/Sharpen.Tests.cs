using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// RCAS 鮮鋭化の性質テスト。
///
/// 見た目の良し悪しは数値では測れないので、ここでは「壊れていないこと」を固定する:
/// 平坦な面は 1 ビットも動かさない（＝ノイズを持ち上げない）、値は 0〜255 に収まる
/// （＝白飛び・黒潰れを作らない）、アルファは触らない、端の画素は素通しする。
/// </summary>
public sealed class ContrastAdaptiveSharpenTests
{
    public ContrastAdaptiveSharpenTests()
    {
        ContrastAdaptiveSharpenService.Enabled = true;
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Normal;
    }

    [Fact]
    public void 強さの段階どおりに効きが変わる()
    {
        const int size = 8;
        var source = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                source[y * size + x] = x < size / 2 ? Gray(80) : Gray(180);
            }
        }

        var row = 4 * size;
        var edge = row + (size / 2) - 1; // 境目の暗い側。強いほど値が下がる。

        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Low;
        var low = Channel(Run(source, size, size)[edge]);
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Normal;
        var normal = Channel(Run(source, size, size)[edge]);
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.High;
        var high = Channel(Run(source, size, size)[edge]);
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Max;
        var max = Channel(Run(source, size, size)[edge]);

        Assert.True(low > normal, $"弱が標準より効いている: low={low} normal={normal}");
        Assert.True(normal > high, $"標準が強より効いている: normal={normal} high={high}");
        Assert.True(high > max, $"強が最強(CAS)より効いている: high={high} max={max}");
        Assert.True(low < 80, $"弱でも締まっていない: {low}");
    }

    [Fact]
    public void 最強でも平坦な面は変化しない()
    {
        // CAS も RCAS と同じく、凹凸の無い場所には何もしない（ノイズを持ち上げない）。
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Max;
        const int size = 8;
        var source = new uint[size * size];
        Array.Fill(source, 0xFF808080u);

        var result = Run(source, size, size);

        Assert.All(result, pixel => Assert.Equal(0xFF808080u, pixel));
    }

    [Fact]
    public void 最強でも値は0から255に収まる()
    {
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Max;
        const int size = 5;
        var source = new uint[size * size];
        Array.Fill(source, Gray(0));
        source[(2 * size) + 2] = Gray(255);

        var result = Run(source, size, size);

        Assert.All(result, pixel =>
        {
            Assert.InRange(Channel(pixel), 0, 255);
            Assert.Equal(0xFFu, pixel >> 24);
        });
    }

    [Fact]
    public void 平坦な面は変化しない()
    {
        const int size = 8;
        var source = new uint[size * size];
        Array.Fill(source, 0xFF808080u);

        var result = Run(source, size, size);

        Assert.All(result, pixel => Assert.Equal(0xFF808080u, pixel));
    }

    [Fact]
    public void 明暗の境目は差が広がる()
    {
        // 左半分が暗く右半分が明るい縦のエッジ。境目の画素はより暗く / より明るくなる。
        const int size = 8;
        var source = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                source[y * size + x] = x < size / 2 ? Gray(80) : Gray(180);
            }
        }

        var result = Run(source, size, size);

        // 中央行（上下端でない行）の境目を見る
        var row = 4 * size;
        var darkSide = Channel(result[row + (size / 2) - 1]);
        var brightSide = Channel(result[row + (size / 2)]);
        Assert.True(darkSide < 80, $"暗い側が締まっていない: {darkSide}");
        Assert.True(brightSide > 180, $"明るい側が締まっていない: {brightSide}");
    }

    [Fact]
    public void 値は0から255に収まる()
    {
        // 1 画素だけ真っ白な点。過剰に掛かると負値や 255 超えが出て巻き戻る。
        const int size = 5;
        var source = new uint[size * size];
        Array.Fill(source, Gray(0));
        source[(2 * size) + 2] = Gray(255);

        var result = Run(source, size, size);

        Assert.All(result, pixel =>
        {
            Assert.InRange(Channel(pixel), 0, 255);
            Assert.Equal(0xFFu, pixel >> 24);
        });
    }

    [Fact]
    public void 端の画素はそのまま写す()
    {
        const int size = 5;
        var source = new uint[size * size];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = Gray((byte)(i * 7));
        }

        var result = Run(source, size, size);

        for (var x = 0; x < size; x++)
        {
            Assert.Equal(source[x], result[x]);                                   // 上端
            Assert.Equal(source[(size - 1) * size + x], result[(size - 1) * size + x]); // 下端
        }

        for (var y = 0; y < size; y++)
        {
            Assert.Equal(source[y * size], result[y * size]);                     // 左端
            Assert.Equal(source[y * size + size - 1], result[y * size + size - 1]); // 右端
        }
    }

    [Fact]
    public void アルファは中心画素の値を保つ()
    {
        const int size = 5;
        var source = new uint[size * size];
        Array.Fill(source, 0x80404040u);

        var result = Run(source, size, size);

        Assert.All(result, pixel => Assert.Equal(0x80u, pixel >> 24));
    }

    /// <summary>不透明なグレー 1 画素。</summary>
    private static uint Gray(byte value)
        => 0xFF000000u | ((uint)value << 16) | ((uint)value << 8) | value;

    private static int Channel(uint pixel) => (int)(pixel & 0xFF);

    private static unsafe uint[] Run(uint[] source, int width, int height)
    {
        var destination = new uint[width * height];
        fixed (uint* buffer = destination)
        {
            ContrastAdaptiveSharpenService.Apply(source, width, height, (nint)buffer, width);
        }

        return destination;
    }
}
