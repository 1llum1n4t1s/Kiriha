using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// 画像ヘッダーからの横幅読み取りのテスト。
///
/// この値は「元より大きい幅でデコードしない」判断に使う。誤って大きく読むと拡大デコードが
/// 復活してぼけ、誤って小さく読むと本来の解像度を捨てて表示することになるため、
/// 形式ごとのバイト位置を固定しておく。
/// </summary>
public sealed class ImageHeaderWidthTests
{
    [Fact]
    public void PNGの幅を読む()
    {
        var bytes = new byte[24];
        // 署名
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        // IHDR の幅（ビッグエンディアン）= 1920
        bytes[16] = 0x00; bytes[17] = 0x00; bytes[18] = 0x07; bytes[19] = 0x80;

        Assert.Equal(1920, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Fact]
    public void GIFの幅を読む()
    {
        var bytes = new byte[10];
        bytes[0] = (byte)'G'; bytes[1] = (byte)'I'; bytes[2] = (byte)'F';
        bytes[3] = (byte)'8'; bytes[4] = (byte)'9'; bytes[5] = (byte)'a';
        // 論理画面幅（リトルエンディアン）= 640
        bytes[6] = 0x80; bytes[7] = 0x02;

        Assert.Equal(640, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Fact]
    public void BMPの幅を読む()
    {
        var bytes = new byte[26];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        // biWidth（リトルエンディアン）= 300
        bytes[18] = 0x2C; bytes[19] = 0x01;

        Assert.Equal(300, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Fact]
    public void JPEGはセグメントを辿ってSOFの幅を読む()
    {
        // SOI + APP0(長さ 16) + SOF0(長さ 17, 高さ 1080, 幅 1920)
        var bytes = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x04, 0x38, 0x07, 0x80, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        };

        Assert.Equal(1920, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Fact]
    public void JPEGのDHTはSOFと間違えない()
    {
        // SOI + DHT(0xC4, 長さ 6) + SOF0(幅 800)。DHT を SOF と誤認すると 800 にならない。
        var bytes = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xC4, 0x00, 0x06, 0x00, 0x01, 0x02, 0x03,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x58, 0x03, 0x20, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        };

        Assert.Equal(800, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Fact]
    public void 拡張WebPの幅を読む()
    {
        var bytes = new byte[30];
        WriteAscii(bytes, 0, "RIFF");
        WriteAscii(bytes, 8, "WEBP");
        WriteAscii(bytes, 12, "VP8X");
        // キャンバス幅 - 1 = 1279（3 バイトのリトルエンディアン）
        bytes[24] = 0xFF; bytes[25] = 0x04; bytes[26] = 0x00;

        Assert.Equal(1280, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Fact]
    public void 可逆WebPの幅を読む()
    {
        var bytes = new byte[30];
        WriteAscii(bytes, 0, "RIFF");
        WriteAscii(bytes, 8, "WEBP");
        WriteAscii(bytes, 12, "VP8L");
        bytes[20] = 0x2F;
        // 幅 - 1 = 639（14bit）
        bytes[21] = 0x7F; bytes[22] = 0x02;

        Assert.Equal(640, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]                      // SOF まで届かない JPEG
    public void 判別できないヘッダーは0を返す(byte[] bytes)
    {
        // 0 は「分からないので指定幅のままデコードする」の合図。従来の挙動へ倒れる。
        Assert.Equal(0, ImageDecodeService.TryReadPixelWidth(bytes));
    }

    private static void WriteAscii(byte[] bytes, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            bytes[offset + i] = (byte)text[i];
        }
    }
}
