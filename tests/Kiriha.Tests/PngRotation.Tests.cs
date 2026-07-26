using System.Buffers.Binary;
using System.IO.Compression;
using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// PNG の無劣化回転（画素の並べ替え＋再エンコード）の回帰テスト。
/// 「4 回回すと元の画素へ完全に戻る」ことを軸に、寸法・補助チャンク・非対応形式の扱いを確認する。
/// </summary>
public class PngRotationTests
{
    /// <summary>指定した画素（RGBA 8bit）から PNG を組み立てる。フィルターは全行 None。</summary>
    private static byte[] BuildPng(int width, int height, Func<int, int, uint> pixel, byte[]? physical = null)
    {
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = pixel(x, y);
                var offset = (y * (stride + 1)) + 1 + (x * 4);
                raw[offset] = (byte)(value >> 24);
                raw[offset + 1] = (byte)(value >> 16);
                raw[offset + 2] = (byte)(value >> 8);
                raw[offset + 3] = (byte)value;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // ビット深度
        header[9] = 6;  // カラータイプ = RGBA
        header[12] = 0; // インターレースなし

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", header);
        if (physical is not null)
        {
            WriteChunk(output, "pHYs", physical);
        }

        WriteChunk(output, "tEXt", "Kiriha\0test"u8.ToArray());
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xFFFFFFFFu);
        output.Write(checksum);
    }

    /// <summary>PNG を読み戻して (幅, 高さ, 画素) を取り出す。テスト側の独立した検証用デコーダー。</summary>
    private static (int Width, int Height, uint[] Pixels) Decode(byte[] png)
    {
        var position = 8;
        var header = Array.Empty<byte>();
        using var compressed = new MemoryStream();
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.AsSpan(position + 8, length);
            if (type == "IHDR")
            {
                header = data.ToArray();
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }

            position += 12 + length;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var rawStream = new MemoryStream();
        zlib.CopyTo(rawStream);
        var raw = rawStream.ToArray();

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int left = x >= 4 ? pixels[(y * stride) + x - 4] : 0;
                int up = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
                int upLeft = y > 0 && x >= 4 ? pixels[((y - 1) * stride) + x - 4] : 0;
                var value = raw[(y * (stride + 1)) + 1 + x] + filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };

                pixels[(y * stride) + x] = (byte)value;
            }
        }

        var result = new uint[width * height];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ((uint)pixels[i * 4] << 24) | ((uint)pixels[(i * 4) + 1] << 16)
                | ((uint)pixels[(i * 4) + 2] << 8) | pixels[(i * 4) + 3];
        }

        return (width, height, result);
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var a = Math.Abs(estimate - left);
        var b = Math.Abs(estimate - up);
        var c = Math.Abs(estimate - upLeft);
        return a <= b && a <= c ? left : b <= c ? up : upLeft;
    }

    /// <summary>座標がそのまま値になる画素（並べ替えの誤りを見逃さないため）。</summary>
    private static uint Coordinate(int x, int y) => (uint)(((x + 1) << 24) | ((y + 1) << 16) | 0x00A0FF);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryRotate_SwapsDimensionsAndMovesPixels(bool clockwise)
    {
        using var temp = new TempDirectory("png");
        var path = temp.Combine("image.png");
        File.WriteAllBytes(path, BuildPng(5, 3, Coordinate));

        Assert.True(PngRotationService.TryRotate(path, clockwise));

        var (width, height, pixels) = Decode(File.ReadAllBytes(path));
        Assert.Equal(3, width);
        Assert.Equal(5, height);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 5; x++)
            {
                // 右回りなら (x, y) は (H-1-y, x) へ、左回りなら (y, W-1-x) へ移る。
                var destinationX = clockwise ? 3 - 1 - y : y;
                var destinationY = clockwise ? x : 5 - 1 - x;
                Assert.Equal(Coordinate(x, y), pixels[(destinationY * width) + destinationX]);
            }
        }
    }

    [Fact]
    public void TryRotate_FourTimesRestoresPixelsExactly()
    {
        using var temp = new TempDirectory("png");
        var path = temp.Combine("image.png");
        var original = BuildPng(7, 4, (x, y) => Coordinate(x, y) ^ (uint)(x * y * 2654435761));
        File.WriteAllBytes(path, original);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(PngRotationService.TryRotate(path, clockwise: true));
        }

        var before = Decode(original);
        var after = Decode(File.ReadAllBytes(path));
        // 可逆圧縮なので、4 回回せば画素は 1 ビットも変わらずに戻る。
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.Pixels, after.Pixels);
    }

    [Fact]
    public void TryRotate_KeepsAncillaryChunksAndSwapsPhysicalSize()
    {
        using var temp = new TempDirectory("png");
        var path = temp.Combine("image.png");
        // pHYs: x = 2000, y = 1000, 単位 = メートル
        byte[] physical = [0x00, 0x00, 0x07, 0xD0, 0x00, 0x00, 0x03, 0xE8, 0x01];
        File.WriteAllBytes(path, BuildPng(4, 2, Coordinate, physical));

        Assert.True(PngRotationService.TryRotate(path, clockwise: true));

        var rotated = File.ReadAllBytes(path);
        Assert.Contains("Kiriha", System.Text.Encoding.ASCII.GetString(rotated));

        var index = IndexOfChunk(rotated, "pHYs");
        Assert.True(index > 0);
        Assert.Equal(1000, BinaryPrimitives.ReadInt32BigEndian(rotated.AsSpan(index)));
        Assert.Equal(2000, BinaryPrimitives.ReadInt32BigEndian(rotated.AsSpan(index + 4)));
        Assert.Equal(1, rotated[index + 8]);
    }

    /// <summary>チャンク種別を探して、そのデータ部の開始位置を返す。</summary>
    private static int IndexOfChunk(byte[] png, string type)
    {
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            if (System.Text.Encoding.ASCII.GetString(png, position + 4, 4) == type)
            {
                return position + 8;
            }

            position += 12 + length;
        }

        return -1;
    }

    [Fact]
    public void TryRotate_RejectsInterlacedPng()
    {
        using var temp = new TempDirectory("png");
        var path = temp.Combine("interlaced.png");
        var png = BuildPng(4, 4, Coordinate);
        // IHDR のインターレース欄（署名 8 + 長さ 4 + 型 4 + 12 バイト目）を Adam7 にする。
        png[8 + 8 + 12] = 1;
        File.WriteAllBytes(path, png);

        // CRC も合わなくなるが、いずれにせよ触らずに false を返せば良い（呼び出し側がエラー表示へ回す）。
        Assert.False(PngRotationService.TryRotate(path, clockwise: true));
    }

    [Fact]
    public void TryRotate_RejectsBrokenFile()
    {
        using var temp = new TempDirectory("png");
        var path = temp.Combine("broken.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Assert.False(PngRotationService.TryRotate(path, clockwise: true));
    }

    [Theory]
    [InlineData(".png", true)]
    [InlineData(".jpg", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".tiff", true)]
    [InlineData(".gif", false)]
    [InlineData(".webp", false)]
    [InlineData(".bmp", false)]
    [InlineData(".mp4", false)]
    public void CanRotate_CoversLosslesslyRotatableFormats(string extension, bool expected)
        => Assert.Equal(expected, ImageRotationService.CanRotate(extension));

    [Fact]
    public void ImageRotationService_RoutesPngToPixelRotation()
    {
        using var temp = new TempDirectory("png");
        var path = temp.Combine("image.png");
        File.WriteAllBytes(path, BuildPng(3, 2, Coordinate));

        Assert.True(ImageRotationService.TryRotate(path, clockwise: false));

        var (width, height, _) = Decode(File.ReadAllBytes(path));
        Assert.Equal(2, width);
        Assert.Equal(3, height);
    }
}
