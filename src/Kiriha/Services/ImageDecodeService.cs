using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kiriha.Services;

/// <summary>
/// 画像・PDF をデコードライブラリへ渡す前に、必ずメモリへ読み切るための窓口。
///
/// Bitmap.DecodeToWidth(FileStream) のようにファイルストリームを直接渡すと、Skia の
/// ネイティブコードから managed ストリームの Read がコールバックされる（SKManagedStream）。
/// そこで I/O 例外が投げられると、ネイティブフレームを越えて巻き戻すことができず、
/// 呼び出し側の try/catch には届かないまま未処理例外としてプロセスが即死する。
/// Google ドライブなどの仮想ドライブでは実体取得の瞬断で「ファンクションが間違っています」
/// （ERROR_INVALID_FUNCTION）「セマフォがタイムアウトしました」（ERROR_SEM_TIMEOUT）が実際に発生し、
/// 画像フォルダーの閲覧中にクラッシュする事例があった。
///
/// 先に読み切っておけば I/O 失敗は通常の managed 例外として捕捉でき、デコードは読み取りが
/// 失敗しない MemoryStream 上で行われるため、この経路でプロセスが落ちることはなくなる。
/// </summary>
internal static class ImageDecodeService
{
    /// <summary>クラウド同期ドライブの瞬断向けに 1 回だけ置く待ち時間。</summary>
    private const int RetryDelayMilliseconds = 150;

    /// <summary>ファイルをメモリへ読み切ってから指定幅でデコードする。読み取りに失敗したら null。
    /// Skia は Exif の Orientation を見ないため、ここで向きを適用してエクスプローラーの
    /// サムネイル（シェル経由なので向きが効いている）と表示を揃える。
    /// <paramref name="sharpen"/> はギャラリーの大画面表示だけで立てる（一覧のサムネイルに
    /// 掛けても効果が見えないうえ、枚数分の処理時間がそのままスクロールの重さになる）。</summary>
    public static Bitmap? TryDecodeToWidth(string path, int width, CancellationToken token = default, bool sharpen = false)
    {
        if (TryReadAllBytes(path, token) is not { } bytes)
        {
            return null;
        }

        Bitmap bitmap;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            // 元より大きい幅を要求すると、デコード時に一度引き伸ばした上で描画時にもう一度
            // 拡大されることになり、ぼけるだけでメモリも余計に食う（横 500px の画像を
            // 2560 幅で持つと 1 枚 26MB）。元の幅が分かる形式では、それを超えない幅で頼む。
            var source = TryReadPixelWidth(bytes);
            var target = source > 0 ? Math.Min(width, source) : width;
            bitmap = Bitmap.DecodeToWidth(stream, target);
        }

        var orientation = ExifOrientationService.ReadOrientation(bytes);
        var oriented = orientation <= 1 ? bitmap : ApplyOrientation(bitmap, orientation);
        return sharpen ? ContrastAdaptiveSharpenService.Apply(oriented) : oriented;
    }

    /// <summary>
    /// 画像ヘッダーから元の横幅（画素）を読む。デコードせずに済ませたいので、対応するのは
    /// プレビュー対象の主要な形式（PNG / JPEG / GIF / BMP / WebP）だけ。
    /// 判別できない形式・壊れたヘッダーでは 0 を返し、呼び出し側は従来どおり指定幅でデコードする。
    /// </summary>
    internal static int TryReadPixelWidth(ReadOnlySpan<byte> bytes)
    {
        // PNG: 8 バイトの署名 + IHDR。幅はビッグエンディアンで 16 バイト目から。
        if (bytes.Length >= 24
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ReadBigEndian32(bytes[16..20]);
        }

        // GIF: "GIF8" + 論理画面幅（リトルエンディアン 16bit）
        if (bytes.Length >= 10
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return bytes[6] | (bytes[7] << 8);
        }

        // BMP: "BM" + BITMAPINFOHEADER の biWidth（リトルエンディアン 32bit）
        if (bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24);
        }

        if (bytes.Length >= 30
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return ReadWebPWidth(bytes);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return ReadJpegWidth(bytes);
        }

        return 0;
    }

    /// <summary>JPEG のセグメントを辿って SOF（フレーム開始）の横幅を読む。</summary>
    private static int ReadJpegWidth(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset + 9 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                return 0;
            }

            var marker = bytes[offset + 1];
            // スタンドアロンマーカー（長さを持たない）は読み飛ばす
            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                offset += 2;
                continue;
            }

            var length = (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (length < 2)
            {
                return 0;
            }

            // SOF0〜SOF15（DHT=C4 / JPG=C8 / DAC=CC を除く）に画素数が入っている
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                return (bytes[offset + 7] << 8) | bytes[offset + 8];
            }

            offset += 2 + length;
        }

        return 0;
    }

    /// <summary>WebP の 3 形式（可逆 VP8L / 非可逆 VP8 / 拡張 VP8X）から横幅を読む。</summary>
    private static int ReadWebPWidth(ReadOnlySpan<byte> bytes)
    {
        var chunk = bytes[12..16];
        if (chunk is [(byte)'V', (byte)'P', (byte)'8', (byte)'X'])
        {
            // キャンバス幅 - 1 が 3 バイトのリトルエンディアンで入っている
            return (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
        }

        if (chunk is [(byte)'V', (byte)'P', (byte)'8', (byte)' '])
        {
            // フレームヘッダー（3+3 バイト）の後、14bit が横幅
            return bytes.Length >= 28 ? (bytes[26] | (bytes[27] << 8)) & 0x3FFF : 0;
        }

        if (chunk is [(byte)'V', (byte)'P', (byte)'8', (byte)'L'])
        {
            // 署名 0x2F の後、14bit が横幅 - 1
            return bytes.Length >= 25 && bytes[20] == 0x2F
                ? ((bytes[21] | (bytes[22] << 8)) & 0x3FFF) + 1
                : 0;
        }

        return 0;
    }

    private static int ReadBigEndian32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    /// <summary>Exif の Orientation（2〜8）に従って画素を並べ替えた新しいビットマップを返す。
    /// 元のビットマップはここで破棄する。</summary>
    private static unsafe Bitmap ApplyOrientation(Bitmap source, int orientation)
    {
        try
        {
            // 32bpp 以外（インデックスカラー等）は画素を 1 語として扱えないので、向きを諦めて元をそのまま出す。
            if (source.Format is not { BitsPerPixel: 32 } format)
            {
                return source;
            }

            var alphaFormat = source.AlphaFormat ?? AlphaFormat.Premul;
            var size = source.PixelSize;
            var width = size.Width;
            var height = size.Height;
            // 5〜8 は 90 度成分を含むので縦横が入れ替わる。
            var swap = orientation >= 5;
            var destSize = swap ? new PixelSize(height, width) : size;

            var pixels = new uint[width * height];
            fixed (uint* buffer = pixels)
            {
                source.CopyPixels(new PixelRect(size), (nint)buffer, pixels.Length * 4, width * 4);
            }

            var result = new WriteableBitmap(destSize, source.Dpi, format, alphaFormat);
            using (var buffer = result.Lock())
            {
                var stride = buffer.RowBytes / 4;
                var dst = (uint*)buffer.Address;
                fixed (uint* src = pixels)
                {
                    for (var dy = 0; dy < destSize.Height; dy++)
                    {
                        for (var dx = 0; dx < destSize.Width; dx++)
                        {
                            // 出力画素ごとに、元画像のどこから持ってくるかを Orientation の定義どおりに引く。
                            var (sx, sy) = orientation switch
                            {
                                2 => (width - 1 - dx, dy),
                                3 => (width - 1 - dx, height - 1 - dy),
                                4 => (dx, height - 1 - dy),
                                5 => (dy, dx),
                                6 => (dy, height - 1 - dx),
                                7 => (width - 1 - dy, height - 1 - dx),
                                8 => (width - 1 - dy, dx),
                                _ => (dx, dy),
                            };
                            dst[(dy * stride) + dx] = src[(sy * width) + sx];
                        }
                    }
                }
            }

            source.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            // 向きの適用に失敗しても、回転していない画像を出せる方がましなので元をそのまま返す。
            Logger.Log($"Exif の向きを適用できませんでした: {ex.Message}", LogLevel.Debug);
            return source;
        }
    }

    /// <summary>ファイル全体をメモリへ読み込む。失敗したら（1 回だけ再試行したうえで）null を返す。</summary>
    public static byte[]? TryReadAllBytes(string path, CancellationToken token = default)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            // 仮想ドライブの実体取得失敗は一時的なことがあるため、少し待って 1 回だけ読み直す
            Logger.Log($"画像ファイルを読み込めませんでした（再試行します）: {path} ({ex.Message})", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Logger.Log($"画像ファイルを読み込めませんでした: {path} ({ex.Message})", LogLevel.Debug);
            return null;
        }

        if (token.IsCancellationRequested)
        {
            return null;
        }

        Thread.Sleep(RetryDelayMilliseconds);
        if (token.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Logger.Log($"画像ファイルを読み込めませんでした（再試行後）: {path} ({ex.Message})", LogLevel.Debug);
            return null;
        }
    }
}
