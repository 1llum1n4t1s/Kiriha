using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// Exif の Orientation を書き換える無劣化回転の回帰テスト。
/// 画素に触れないこと（＝圧縮データ部分が 1 バイトも変わらないこと）まで含めて確認する。
/// </summary>
public class ExifOrientationTests
{
    /// <summary>Orientation だけを持つ最小の JPEG を組み立てる。</summary>
    /// <param name="orientation">IFD0 に書き込む Orientation 値。</param>
    /// <param name="bigEndian">true で TIFF ヘッダーをビッグエンディアン（MM）にする。</param>
    private static byte[] BuildJpeg(int orientation, bool bigEndian = false)
    {
        var tiff = bigEndian
            ? new byte[]
            {
                0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // TIFF ヘッダー（IFD0 は +8）
                0x00, 0x01,                                     // エントリー数 = 1
                0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, // タグ 0x0112 / SHORT / 個数 1
                (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,                         // 次 IFD なし
            }
            : new byte[]
            {
                0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
                0x01, 0x00,
                0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
                (byte)orientation, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };

        var exif = new byte[] { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00 }; // "Exif\0\0"
        var payload = exif.Concat(tiff).ToArray();
        var length = payload.Length + 2;

        return new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length }
            .Concat(payload)
            .Concat(BuildScan())
            .ToArray();
    }

    /// <summary>SOS 以降の「圧縮データのつもり」の部分。回転で書き換わってはいけない。</summary>
    private static byte[] BuildScan()
        => [0xFF, 0xDA, 0x00, 0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0xFF, 0xD9];

    [Theory]
    [InlineData(1, true, 6)]
    [InlineData(6, true, 3)]
    [InlineData(3, true, 8)]
    [InlineData(8, true, 1)]
    [InlineData(1, false, 8)]
    [InlineData(8, false, 3)]
    [InlineData(3, false, 6)]
    [InlineData(6, false, 1)]
    public void TryRotate_AdvancesOrientation(int start, bool clockwise, int expected)
    {
        using var temp = new TempDirectory("exif");
        var path = temp.Combine("photo.jpg");
        File.WriteAllBytes(path, BuildJpeg(start));

        Assert.True(ExifOrientationService.TryRotate(path, clockwise));
        Assert.Equal(expected, ExifOrientationService.ReadOrientation(File.ReadAllBytes(path)));
    }

    [Fact]
    public void TryRotate_KeepsCompressedDataByteForByte()
    {
        using var temp = new TempDirectory("exif");
        var path = temp.Combine("photo.jpg");
        var original = BuildJpeg(1);
        File.WriteAllBytes(path, original);

        Assert.True(ExifOrientationService.TryRotate(path, clockwise: true));

        var rotated = File.ReadAllBytes(path);
        // 無劣化回転なので、長さも SOS 以降も一切変わらない（差分は Orientation の 1 バイトだけ）。
        Assert.Equal(original.Length, rotated.Length);
        Assert.Equal(1, original.Zip(rotated).Count(pair => pair.First != pair.Second));
        Assert.Equal(BuildScan(), rotated[^BuildScan().Length..]);
    }

    [Fact]
    public void TryRotate_HandlesBigEndianExif()
    {
        using var temp = new TempDirectory("exif");
        var path = temp.Combine("photo.jpg");
        File.WriteAllBytes(path, BuildJpeg(1, bigEndian: true));

        Assert.True(ExifOrientationService.TryRotate(path, clockwise: true));
        Assert.Equal(6, ExifOrientationService.ReadOrientation(File.ReadAllBytes(path)));
    }

    [Fact]
    public void TryRotate_InsertsExifWhenMissing()
    {
        using var temp = new TempDirectory("exif");
        var path = temp.Combine("plain.jpg");
        var original = new byte[] { 0xFF, 0xD8 }.Concat(BuildScan()).ToArray();
        File.WriteAllBytes(path, original);

        Assert.Equal(1, ExifOrientationService.ReadOrientation(original));
        Assert.True(ExifOrientationService.TryRotate(path, clockwise: true));

        var rotated = File.ReadAllBytes(path);
        Assert.Equal(6, ExifOrientationService.ReadOrientation(rotated));
        // 挿入しただけなので、元の圧縮データは末尾にそのまま残る。
        Assert.Equal(BuildScan(), rotated[^BuildScan().Length..]);
    }

    [Fact]
    public void TryRotate_FailsForFormatWithoutOrientationSupport()
    {
        using var temp = new TempDirectory("exif");
        var path = temp.Combine("image.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Assert.False(ExifOrientationService.CanRotate(".png"));
        Assert.False(ExifOrientationService.TryRotate(path, clockwise: true));
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".tif", true)]
    [InlineData(".tiff", true)]
    [InlineData(".png", false)]
    [InlineData(".webp", false)]
    [InlineData(".mp4", false)]
    public void CanRotate_MatchesOrientationCapableFormats(string extension, bool expected)
        => Assert.Equal(expected, ExifOrientationService.CanRotate(extension));
}
