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
    /// サムネイル（シェル経由なので向きが効いている）と表示を揃える。</summary>
    public static Bitmap? TryDecodeToWidth(string path, int width, CancellationToken token = default)
    {
        if (TryReadAllBytes(path, token) is not { } bytes)
        {
            return null;
        }

        Bitmap bitmap;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            bitmap = Bitmap.DecodeToWidth(stream, width);
        }

        var orientation = ExifOrientationService.ReadOrientation(bytes);
        return orientation <= 1 ? bitmap : ApplyOrientation(bitmap, orientation);
    }

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
