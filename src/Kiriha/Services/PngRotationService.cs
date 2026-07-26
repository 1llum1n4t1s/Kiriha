using System.Buffers.Binary;
using System.IO.Compression;

namespace Kiriha.Services;

/// <summary>
/// PNG を 90 度単位で回転して書き戻す（無劣化）。
///
/// JPEG は Exif の Orientation を書き換えるだけで回せる（<see cref="ExifOrientationService"/>）が、
/// PNG に Orientation の概念は無い。ただし PNG の圧縮は可逆なので、画素を並べ替えて再エンコード
/// しても画質は 1 ビットも落ちない。ここでは zlib ストリームを展開 → 行フィルターを外す →
/// 画素を並べ替える → 再びフィルターして deflate、という手順でファイルを作り直す。
/// ビット深度・カラータイプ・パレット・補助チャンク（tEXt / tRNS / gAMA 等）は素通しで保つ。
///
/// 対象外（false を返す）:
/// - インターレース PNG（Adam7）: 展開後の並びが 7 パスに分かれ、扱いが別物になる。
/// - ビット深度 8 未満: 1 バイトに複数画素が詰まっており、バイト単位の入れ替えでは動かせない。
/// スクリーンショットや一般的な画像編集ソフトの出力はどちらにも当たらない。
/// </summary>
internal static class PngRotationService
{
    /// <summary>この方式で回転できる拡張子。</summary>
    private static readonly string[] RotatableExtensions = [".png"];

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool CanRotate(string extension)
        => Array.IndexOf(RotatableExtensions, extension) >= 0;

    /// <summary>PNG を 90 度回転して同じパスへ書き戻す。成功したら true。</summary>
    /// <param name="path">対象ファイル。</param>
    /// <param name="clockwise">true で右回り、false で左回り。</param>
    public static bool TryRotate(string path, bool clockwise)
    {
        try
        {
            if (ImageDecodeService.TryReadAllBytes(path) is not { } bytes)
            {
                return false;
            }

            if (!TryRotateBytes(bytes, clockwise, out var rotated))
            {
                return false;
            }

            File.WriteAllBytes(path, rotated);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogException($"PNG の回転に失敗しました: {path}", ex);
            return false;
        }
    }

    /// <summary>PNG のバイト列を回転した新しいバイト列へ変換する。</summary>
    internal static bool TryRotateBytes(byte[] png, bool clockwise, out byte[] rotated)
    {
        rotated = [];
        if (!TryReadChunks(png, out var chunks))
        {
            return false;
        }

        var header = chunks.FirstOrDefault(chunk => chunk.Type == "IHDR");
        if (header is null || header.Data.Length < 13)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(0));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(4));
        var bitDepth = header.Data[8];
        var colorType = header.Data[9];
        var interlace = header.Data[12];

        if (width <= 0 || height <= 0 || interlace != 0 || bitDepth is not (8 or 16))
        {
            return false;
        }

        var channels = colorType switch
        {
            0 => 1, // グレースケール
            2 => 3, // トゥルーカラー
            3 => 1, // パレット（インデックス 1 バイト）
            4 => 2, // グレースケール + アルファ
            6 => 4, // トゥルーカラー + アルファ
            _ => 0,
        };
        if (channels == 0)
        {
            return false;
        }

        var pixelBytes = channels * (bitDepth / 8);
        var compressed = chunks.Where(chunk => chunk.Type == "IDAT").SelectMany(chunk => chunk.Data).ToArray();
        if (compressed.Length == 0)
        {
            return false;
        }

        var raw = Inflate(compressed, height * (1 + (width * pixelBytes)));
        if (raw.Length < height * (1 + (long)width * pixelBytes))
        {
            return false;
        }

        var pixels = Unfilter(raw, width, height, pixelBytes);
        var turned = Turn(pixels, width, height, pixelBytes, clockwise);
        var body = Deflate(Filter(turned, height, width, pixelBytes));

        rotated = Rebuild(chunks, header, width: height, height: width, body);
        return true;
    }

    /// <summary>PNG のチャンク列。</summary>
    private sealed record PngChunk(string Type, byte[] Data);

    private static bool TryReadChunks(byte[] png, out List<PngChunk> chunks)
    {
        chunks = [];
        if (png.Length < Signature.Length + 12 || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            return false;
        }

        var position = Signature.Length;
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            if (length < 0 || position + 12 + length > png.Length)
            {
                return false;
            }

            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            chunks.Add(new PngChunk(type, png[(position + 8)..(position + 8 + length)]));
            position += 12 + length;

            if (type == "IEND")
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Inflate(byte[] compressed, int expectedLength)
    {
        using var source = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(expectedLength, 1024));
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream(raw.Length / 2);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    /// <summary>各行の先頭に付く 1 バイトのフィルターを外して、素の画素バイト列へ戻す。</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int pixelBytes)
    {
        var stride = width * pixelBytes;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = (y * (stride + 1)) + 1;
            var line = y * stride;
            var previous = line - stride;

            for (var x = 0; x < stride; x++)
            {
                int left = x >= pixelBytes ? pixels[line + x - pixelBytes] : 0;
                int up = y > 0 ? pixels[previous + x] : 0;
                int upLeft = y > 0 && x >= pixelBytes ? pixels[previous + x - pixelBytes] : 0;

                var value = raw[source + x] + filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };

                pixels[line + x] = (byte)value;
            }
        }

        return pixels;
    }

    /// <summary>行ごとに 5 種類のフィルターを試し、絶対値の和が最小のものを選ぶ（PNG 標準の常套手段）。</summary>
    private static byte[] Filter(byte[] pixels, int width, int height, int pixelBytes)
    {
        var stride = width * pixelBytes;
        var output = new byte[(stride + 1) * height];
        var candidate = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var line = y * stride;
            var previous = line - stride;
            var best = 0;
            var bestScore = long.MaxValue;
            var bestBytes = new byte[stride];

            for (var filter = 0; filter <= 4; filter++)
            {
                long score = 0;
                for (var x = 0; x < stride; x++)
                {
                    int left = x >= pixelBytes ? pixels[line + x - pixelBytes] : 0;
                    int up = y > 0 ? pixels[previous + x] : 0;
                    int upLeft = y > 0 && x >= pixelBytes ? pixels[previous + x - pixelBytes] : 0;

                    var value = (byte)(pixels[line + x] - filter switch
                    {
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        4 => Paeth(left, up, upLeft),
                        _ => 0,
                    });

                    candidate[x] = value;
                    score += value < 128 ? value : 256 - value;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = filter;
                    (bestBytes, candidate) = (candidate, bestBytes);
                }
            }

            output[y * (stride + 1)] = (byte)best;
            bestBytes.CopyTo(output.AsSpan((y * (stride + 1)) + 1, stride));
        }

        return output;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpLeft = Math.Abs(estimate - upLeft);

        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpLeft ? up : upLeft;
    }

    /// <summary>画素を 90 度ぶん並べ替える（幅と高さが入れ替わる）。</summary>
    private static byte[] Turn(byte[] pixels, int width, int height, int pixelBytes, bool clockwise)
    {
        var destination = new byte[pixels.Length];
        var sourceStride = width * pixelBytes;
        var destinationStride = height * pixelBytes;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // 右回りなら (x, y) は右上へ、左回りなら左下へ移る。
                var destinationX = clockwise ? height - 1 - y : y;
                var destinationY = clockwise ? x : width - 1 - x;

                var source = (y * sourceStride) + (x * pixelBytes);
                var target = (destinationY * destinationStride) + (destinationX * pixelBytes);
                pixels.AsSpan(source, pixelBytes).CopyTo(destination.AsSpan(target, pixelBytes));
            }
        }

        return destination;
    }

    /// <summary>IHDR の寸法と IDAT だけを差し替え、その他のチャンクは順序ごと保って組み直す。</summary>
    private static byte[] Rebuild(List<PngChunk> chunks, PngChunk header, int width, int height, byte[] body)
    {
        var newHeader = (byte[])header.Data.Clone();
        BinaryPrimitives.WriteInt32BigEndian(newHeader.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(newHeader.AsSpan(4), height);

        using var output = new MemoryStream();
        output.Write(Signature);

        var idatWritten = false;
        foreach (var chunk in chunks)
        {
            switch (chunk.Type)
            {
                case "IHDR":
                    WriteChunk(output, "IHDR", newHeader);
                    break;
                case "IDAT":
                    // 元が複数 IDAT でも、作り直したデータは 1 つにまとめて先頭の位置へ置く。
                    if (!idatWritten)
                    {
                        WriteChunk(output, "IDAT", body);
                        idatWritten = true;
                    }

                    break;
                case "pHYs":
                    // 物理解像度は縦横が入れ替わるので、x と y を交換して整合を保つ。
                    WriteChunk(output, "pHYs", SwapPhysicalSize(chunk.Data));
                    break;
                default:
                    WriteChunk(output, chunk.Type, chunk.Data);
                    break;
            }
        }

        return output.ToArray();
    }

    private static byte[] SwapPhysicalSize(byte[] data)
    {
        if (data.Length < 9)
        {
            return data;
        }

        var swapped = (byte[])data.Clone();
        data.AsSpan(0, 4).CopyTo(swapped.AsSpan(4));
        data.AsSpan(4, 4).CopyTo(swapped.AsSpan(0));
        return swapped;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        output.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var value = (uint)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
