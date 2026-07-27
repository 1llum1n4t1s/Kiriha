using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kiriha.Services;

/// <summary>
/// ガンマ補正。暗部を持ち上げたり締めたりする表示だけの調整で、ファイルは書き換えない。
///
/// 変換は 1 チャンネルにつき 256 通りしかないので、値が変わったときだけ表を作り直し、
/// 画素ごとには表引きだけを行う（動画は 1 秒あたり 4600 万画素を通すため、
/// ここで pow を呼ぶと鮮鋭化より重くなる）。
/// </summary>
internal static class GammaAdjustService
{
    /// <summary>調整しないときの値。</summary>
    public const double Neutral = 1.0;

    /// <summary>スライダーの下限（暗部を持ち上げる側）。</summary>
    public const double Minimum = 0.4;

    /// <summary>スライダーの上限（暗部を締める側）。</summary>
    public const double Maximum = 2.5;

    private static double _gamma = Neutral;
    private static byte[]? _table;

    /// <summary>
    /// ガンマ値。1.0 で無変換、1 より小さいと明るく、大きいと暗くなる。
    /// 全タブ・静止画・動画で共通の状態（動画の音量などと同じ扱い）。
    /// </summary>
    public static double Gamma
    {
        get => _gamma;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(clamped - _gamma) < 0.0005)
            {
                return;
            }

            _gamma = clamped;
            _table = IsNeutral ? null : BuildTable(clamped);
        }
    }

    /// <summary>無変換か（この場合は 1 画素も触らない）。</summary>
    public static bool IsNeutral => Math.Abs(_gamma - Neutral) < 0.0005;

    private static byte[] BuildTable(double gamma)
    {
        var table = new byte[256];
        // 画面へ出す値 = (入力 / 255) ^ (1 / gamma)。gamma > 1 で暗く、< 1 で明るくなる。
        var inverse = 1.0 / gamma;
        for (var i = 0; i < table.Length; i++)
        {
            var value = Math.Pow(i / 255.0, inverse) * 255.0 + 0.5;
            table[i] = (byte)Math.Clamp((int)value, 0, 255);
        }

        return table;
    }

    /// <summary>BGRA 画素をその場で補正する（動画フレーム用）。アルファは触らない。</summary>
    public static unsafe void Apply(nint pixels, int width, int height, int stride)
    {
        if (_table is not { } table || width <= 0 || height <= 0)
        {
            return;
        }

        var buffer = (uint*)pixels;
        fixed (byte* map = table)
        {
            // 行数ぶんの並列化。鮮鋭化と同じく、動画では 1 フレームあたり数百行を 30 回/秒処理する。
            var chunk = Math.Max(32, height / (Environment.ProcessorCount * 4));
            var address = (nint)map;
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, height, chunk), range =>
            {
                var lut = (byte*)address;
                for (var y = range.Item1; y < range.Item2; y++)
                {
                    var row = buffer + (nint)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = row[x];
                        row[x] = (pixel & 0xFF000000)
                                 | ((uint)lut[(pixel >> 16) & 0xFF] << 16)
                                 | ((uint)lut[(pixel >> 8) & 0xFF] << 8)
                                 | lut[pixel & 0xFF];
                    }
                }
            });
        }
    }

    /// <summary>補正した新しいビットマップを返す（静止画用）。無変換のときは元をそのまま返す。</summary>
    public static unsafe Bitmap Apply(Bitmap source)
    {
        if (IsNeutral || source.Format is not { BitsPerPixel: 32 } format)
        {
            return source;
        }

        try
        {
            var size = source.PixelSize;
            var result = new WriteableBitmap(size, source.Dpi, format, source.AlphaFormat ?? AlphaFormat.Premul);
            using (var locked = result.Lock())
            {
                source.CopyPixels(new PixelRect(size), locked.Address, locked.RowBytes * size.Height, locked.RowBytes);
                Apply(locked.Address, size.Width, size.Height, locked.RowBytes / 4);
            }

            source.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            // 表示できることのほうが大事なので、失敗したら補正を諦めて元をそのまま出す
            Logger.Log($"ガンマ補正に失敗したため元の画像を表示します: {ex.Message}", LogLevel.Debug);
            return source;
        }
    }
}
