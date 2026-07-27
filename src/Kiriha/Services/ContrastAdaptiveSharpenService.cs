using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kiriha.Services;

/// <summary>ギャラリー鮮鋭化の強さ（設定 &gt; 表示 で選ぶ。settings.json には名前で保存する）。</summary>
internal enum SharpenStrength
{
    /// <summary>弱め。輪郭が立ちすぎるのが苦手なとき向け。</summary>
    Low,

    /// <summary>標準。FSR の推奨値あたり。</summary>
    Normal,

    /// <summary>強め。sharpness を最大にし、ノイズ検出による手加減も切る。</summary>
    High,

    /// <summary>最強。RCAS ではなく元祖 CAS（3x3 参照）を使う。上限が緩いぶんはっきり効くが、
    /// 素材によっては輪郭に白フチ（ハロー）が出る。</summary>
    Max,
}

/// <summary>
/// RCAS（Robust Contrast Adaptive Sharpening / AMD FidelityFX FSR1 の後段）による鮮鋭化。
///
/// 画素を増やす超解像ではなく「持っている解像度を見た目どおりに出す」ための処理。
/// 拡大で鈍った輪郭を、平坦部やすでに尖っている部分には手を出さずに締める。
/// 単純なアンシャープマスクと違い、局所的な最小・最大からリンギングの余地を計算して
/// 掛ける量を決めるため、白フチ（ハロー）と黒潰れが出ない。
///
/// 実装は FSR1 の ffx_fsr1.h の 32bit 経路をそのまま素直に写したもので、
/// 隣接 4 画素（上下左右）だけを見るので前後フレームも動きベクトルも要らない。
/// だから静止画にも動画フレームにも同じように掛けられる（FSR2 以降は動きベクトルが要るので使えない）。
/// </summary>
internal static class ContrastAdaptiveSharpenService
{
    /// <summary>鮮鋭化を掛けるか（設定 &gt; 表示 で切り替え。全タブ・動画と共通の状態）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// 鮮鋭化の強さ。RCAS で効きを変えられるのは次の 2 つで、段階ごとに組み合わせを変える。
    ///
    /// ・sharpness（0 = 最強、2 = ほぼ無効）… 掛ける量そのもの。
    /// ・ノイズ検出（RCAS_DENOISE）… 細かい凹凸が多い場所で効きを最大半分まで落とす仕組み。
    ///   写真のノイズやディザを持ち上げないための安全弁だが、切ると細部までしっかり立つ。
    ///
    /// 上限そのもの（FSR_RCAS_LIMIT）は動かさない。あれを超えるとリンギング（白フチ）が出て、
    /// 「くっきり」ではなく「汚い」方向へ行くため。
    /// </summary>
    public static SharpenStrength Strength
    {
        get => _strength;
        set
        {
            _strength = value;
            _con = value switch
            {
                SharpenStrength.Low => MathF.Pow(2f, -0.7f),
                SharpenStrength.High => 1f,      // sharpness 0 = FSR の最強
                _ => MathF.Pow(2f, -0.25f),      // 標準
            };
            // 強のときだけノイズ検出を切る（効きは体感でおよそ倍になる）
            _denoise = value != SharpenStrength.High;
            // 最強は RCAS ではなく CAS（元祖）へ切り替える。RCAS の上限を超えて掛けたい場合の受け皿。
            _useCas = value == SharpenStrength.Max;
        }
    }

    private static SharpenStrength _strength = SharpenStrength.Normal;
    private static float _con = MathF.Pow(2f, -0.25f);
    private static bool _denoise = true;
    private static bool _useCas;

    /// <summary>CAS の sharpness（0〜1）。FSR のサンプルの既定は 0.8 前後。</summary>
    private const float CasSharpness = 0.85f;

    /// <summary>CAS の掛かり方を決める係数。FSR と同じく -1 / lerp(8, 5, sharpness)。</summary>
    private static readonly float _casPeak = -1f / (8f + (5f - 8f) * CasSharpness);

    /// <summary>FSR_RCAS_LIMIT。掛ける量の上限（0.25 - 1/16）。これ以上はリンギングになる。</summary>
    private const float Limit = 0.25f - 1f / 16f;

    /// <summary>ゼロ除算よけ。FSR は GPU の rcp 近似に任せている箇所。</summary>
    private const float Epsilon = 1e-5f;

    /// <summary>
    /// 32bpp（BGRA）のビットマップを鮮鋭化した新しいビットマップを返す。
    /// 32bpp 以外や小さすぎる画像はそのまま返す（呼び出し側は戻り値だけを使えばよい）。
    /// 元のビットマップは <paramref name="disposeSource"/> のときここで破棄する。
    /// </summary>
    /// <summary>
    /// 表示される画素サイズへリサイズしてから鮮鋭化する。
    ///
    /// 順序が肝で、鮮鋭化してから拡大縮小すると、締めた輪郭が再サンプリングでまた鈍る
    /// （縮小ではほぼ消え、拡大では締めた幅ごと太る）。FSR も EASU で拡大した後段に
    /// RCAS を置いている。
    ///
    /// できあがりの DPI は 96 のまま（＝論理サイズ＝画素数）にすること。画面倍率を掛けた
    /// DPI を付けると、レイアウトは論理サイズで計算されるのに描画は画素で切り出すため、
    /// 「左上だけが引き伸ばされて残りが切れる」表示になる（実測で確認済み）。
    /// 画面上の大きさは呼び出し側が Width / Height と Stretch=Fill で決める。
    /// </summary>
    public static Bitmap ApplyScaled(Bitmap source, PixelSize target)
    {
        if (!Enabled)
        {
            return source;
        }

        Bitmap scaled;
        try
        {
            scaled = source.PixelSize == target
                ? source
                : source.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            Logger.Log($"表示サイズへの縮小拡大に失敗したため元の解像度で表示します: {ex.Message}", LogLevel.Debug);
            return Apply(source);
        }

        if (!ReferenceEquals(scaled, source))
        {
            source.Dispose();
        }

        return Apply(scaled);
    }

    public static unsafe Bitmap Apply(Bitmap source, bool disposeSource = true, Vector? dpi = null)
    {
        if (!Enabled
            || source.Format is not { BitsPerPixel: 32 } format
            || source.PixelSize.Width < 3
            || source.PixelSize.Height < 3)
        {
            return source;
        }

        try
        {
            var size = source.PixelSize;
            var pixels = new uint[size.Width * size.Height];
            fixed (uint* buffer = pixels)
            {
                source.CopyPixels(new PixelRect(size), (nint)buffer, pixels.Length * 4, size.Width * 4);
            }

            var result = new WriteableBitmap(size, dpi ?? source.Dpi, format, source.AlphaFormat ?? AlphaFormat.Premul);
            using (var locked = result.Lock())
            {
                Apply(pixels, size.Width, size.Height, locked.Address, locked.RowBytes / 4);
            }

            if (disposeSource)
            {
                source.Dispose();
            }

            return result;
        }
        catch (Exception ex)
        {
            // 表示できることのほうが大事なので、失敗したら鮮鋭化を諦めて元をそのまま出す
            Logger.Log($"鮮鋭化に失敗したため元の画像を表示します: {ex.Message}", LogLevel.Debug);
            return source;
        }
    }

    /// <summary>
    /// BGRA 画素の配列を鮮鋭化して転送先へ書き出す。元画素を参照しながら書くので、
    /// 転送元と転送先は必ず別のバッファにすること（同じにすると隣接画素が処理済みの値になる）。
    /// </summary>
    /// <param name="source">元画素（幅 × 高さ、行間の余白なし）。</param>
    /// <param name="destinationStride">転送先の 1 行あたりの画素数（バイト数ではない）。</param>
    public static void Apply(uint[] source, int width, int height, nint destination, int destinationStride)
    {
        if (width < 3 || height < 3 || source.Length < width * height)
        {
            return;
        }

        // ポインタはラムダで捕捉できないので、固定して nint で渡す。
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            var address = handle.AddrOfPinnedObject();
            // 行ごとに Parallel.For を回すとデリゲート呼び出しと同期が行数ぶん増える。
            // 動画では 1 フレームあたり数百〜千行を 30 回/秒処理するので、範囲で区切って渡す。
            var chunk = Math.Max(16, height / (Environment.ProcessorCount * 4));
            Parallel.ForEach(Partitioner.Create(0, height, chunk), range =>
            {
                for (var y = range.Item1; y < range.Item2; y++)
                {
                    ProcessRow(address, destination, width, height, destinationStride, y);
                }
            });
        }
        finally
        {
            handle.Free();
        }
    }

    private static unsafe void ProcessRow(nint source, nint destination, int width, int height, int destinationStride, int y)
    {
        var src = (uint*)source;
        var dst = (uint*)destination + (nint)y * destinationStride;
        var row = src + (nint)y * width;

        // 上下端の行と左右端の画素は参照できる隣接が足りないので、そのまま写す。
        if (y == 0 || y == height - 1)
        {
            for (var x = 0; x < width; x++)
            {
                dst[x] = row[x];
            }

            return;
        }

        var above = row - width;
        var below = row + width;
        dst[0] = row[0];
        dst[width - 1] = row[width - 1];

        if (_useCas)
        {
            ProcessRowCas(above, row, below, dst, width);
            return;
        }

        // 横に 1 画素進むと、右の画素が中心に、中心が左になる。3 組を持ち回して
        // 展開し直す回数を 5 → 3 に減らす（動画では 1 秒あたり 4600 万画素を通す）。
        Unpack(row[0], out var d);
        Unpack(row[1], out var e);

        for (var x = 1; x < width - 1; x++)
        {
            Unpack(row[x + 1], out var f);
            Unpack(above[x], out var b);
            Unpack(below[x], out var h);
            dst[x] = Sharpen(b, d, e, f, h, row[x]);
            d = e;
            e = f;
        }
    }

    /// <summary>CAS（3x3 参照）で 1 行ぶんを鮮鋭化する。RCAS より強く掛けられる代わりに、
    /// 上限が緩いぶん強くしすぎると輪郭に白フチ（ハロー）が出る。</summary>
    private static unsafe void ProcessRowCas(uint* above, uint* row, uint* below, uint* dst, int width)
    {
        // 縦 3 画素ぶんを 1 列として持ち回す（列を 1 つ進めるたびに右端の 1 列だけ展開する）。
        Unpack3(above[0], row[0], below[0], out var left);
        Unpack3(above[1], row[1], below[1], out var mid);

        for (var x = 1; x < width - 1; x++)
        {
            Unpack3(above[x + 1], row[x + 1], below[x + 1], out var right);
            dst[x] = SharpenCas(left, mid, right, row[x]);
            left = mid;
            mid = right;
        }
    }

    /// <summary>1 画素ぶんの RGB（0〜1）。</summary>
    private struct Rgb
    {
        public float R;
        public float G;
        public float B;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Unpack(uint pixel, out Rgb rgb)
    {
        const float Scale = 1f / 255f;
        rgb.B = (pixel & 0xFF) * Scale;
        rgb.G = ((pixel >> 8) & 0xFF) * Scale;
        rgb.R = ((pixel >> 16) & 0xFF) * Scale;
    }

    /// <summary>上下左右 4 画素と中心 1 画素から、中心の鮮鋭化後の値を求める。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sharpen(in Rgb b, in Rgb d, in Rgb e, in Rgb f, in Rgb h, uint center)
    {
        // ノイズ検出。輝度の凹凸が周囲のレンジに対して大きいほど、掛ける量を減らす
        // （ノイズやディザまで持ち上げて汚くしないため）。輝度は FSR と同じ 2 倍値。
        var bL = Luma(b);
        var dL = Luma(d);
        var eL = Luma(e);
        var fL = Luma(f);
        var hL = Luma(h);
        var noise = 1f;
        if (_denoise)
        {
            var range = Max(Max(bL, dL), Max(Max(fL, hL), eL))
                        - Min(Min(bL, dL), Min(Min(fL, hL), eL));
            var bump = 0.25f * (bL + dL + fL + hL) - eL;
            bump = range > Epsilon ? Min(MathF.Abs(bump) / range, 1f) : 0f;
            noise = 1f - 0.5f * bump;
        }

        // 各色で「リンギングを起こさずに引ける量」を求め、最も厳しい色に合わせる。
        var lobe = Max(
            Lobe(b.R, d.R, f.R, h.R, e.R),
            Max(Lobe(b.G, d.G, f.G, h.G, e.G), Lobe(b.B, d.B, f.B, h.B, e.B)));
        lobe = Max(-Limit, Min(lobe, 0f)) * _con * noise;

        var rcp = 1f / (4f * lobe + 1f);
        return (center & 0xFF000000)
               | ((uint)Resolve(b.R, d.R, f.R, h.R, e.R, lobe, rcp) << 16)
               | ((uint)Resolve(b.G, d.G, f.G, h.G, e.G, lobe, rcp) << 8)
               | Resolve(b.B, d.B, f.B, h.B, e.B, lobe, rcp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Luma(in Rgb c) => c.B * 0.5f + (c.R * 0.5f + c.G);

    /// <summary>縦に並んだ 3 画素（上・中・下）。CAS は 3x3 を見るので列単位で持ち回す。</summary>
    private struct Column
    {
        public Rgb Top;
        public Rgb Middle;
        public Rgb Bottom;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Unpack3(uint top, uint middle, uint bottom, out Column column)
    {
        Unpack(top, out column.Top);
        Unpack(middle, out column.Middle);
        Unpack(bottom, out column.Bottom);
    }

    /// <summary>
    /// CAS（FidelityFX Contrast Adaptive Sharpening）の 1 画素。
    ///
    /// 3x3 の最小・最大から「その場所で白飛び・黒潰れさせずに掛けられる量」を出し、
    /// 上下左右の 4 画素で中心を引く。RCAS と違って掛ける量の上限が低くないので、
    /// <see cref="_casSharpness"/> を上げるほど素直に強くなる（上げすぎるとハロー）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SharpenCas(in Column left, in Column mid, in Column right, uint center)
    {
        // 十字（上下左右）＋中心の最小・最大
        var minR = Min(Min(Min(left.Middle.R, mid.Top.R), Min(mid.Middle.R, mid.Bottom.R)), right.Middle.R);
        var minG = Min(Min(Min(left.Middle.G, mid.Top.G), Min(mid.Middle.G, mid.Bottom.G)), right.Middle.G);
        var minB = Min(Min(Min(left.Middle.B, mid.Top.B), Min(mid.Middle.B, mid.Bottom.B)), right.Middle.B);
        var maxR = Max(Max(Max(left.Middle.R, mid.Top.R), Max(mid.Middle.R, mid.Bottom.R)), right.Middle.R);
        var maxG = Max(Max(Max(left.Middle.G, mid.Top.G), Max(mid.Middle.G, mid.Bottom.G)), right.Middle.G);
        var maxB = Max(Max(Max(left.Middle.B, mid.Top.B), Max(mid.Middle.B, mid.Bottom.B)), right.Middle.B);

        // 斜め 4 画素も含めた最小・最大を足し込む（FSR の実装どおり。値域は 0〜2 になる）
        minR += Min(Min(left.Top.R, left.Bottom.R), Min(right.Top.R, right.Bottom.R));
        minG += Min(Min(left.Top.G, left.Bottom.G), Min(right.Top.G, right.Bottom.G));
        minB += Min(Min(left.Top.B, left.Bottom.B), Min(right.Top.B, right.Bottom.B));
        maxR += Max(Max(left.Top.R, left.Bottom.R), Max(right.Top.R, right.Bottom.R));
        maxG += Max(Max(left.Top.G, left.Bottom.G), Max(right.Top.G, right.Bottom.G));
        maxB += Max(Max(left.Top.B, left.Bottom.B), Max(right.Top.B, right.Bottom.B));

        var weightR = CasWeight(minR, maxR);
        var weightG = CasWeight(minG, maxG);
        var weightB = CasWeight(minB, maxB);

        return (center & 0xFF000000)
               | ((uint)CasResolve(left.Middle.R, mid.Top.R, mid.Middle.R, mid.Bottom.R, right.Middle.R, weightR) << 16)
               | ((uint)CasResolve(left.Middle.G, mid.Top.G, mid.Middle.G, mid.Bottom.G, right.Middle.G, weightG) << 8)
               | CasResolve(left.Middle.B, mid.Top.B, mid.Middle.B, mid.Bottom.B, right.Middle.B, weightB);
    }

    /// <summary>この色に掛けられる量（負の値）。周囲が信号の上下限へ近いほど 0 に寄る。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CasWeight(float min, float max)
    {
        var amp = Math.Clamp(Min(min, 2f - max) / Max(max, Epsilon), 0f, 1f);
        return MathF.Sqrt(amp) * _casPeak;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CasResolve(float d, float b, float e, float h, float f, float weight)
    {
        var value = (b + d + f + h) * weight + e;
        value = value / (1f + 4f * weight) * 255f + 0.5f;
        return (uint)Math.Clamp((int)value, 0, 255);
    }

    /// <summary>この色で許される鮮鋭化量（負の値。0 に近いほど掛けられない）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lobe(float b, float d, float f, float h, float e)
    {
        var min4 = Min(Min(b, d), Min(f, h));
        var max4 = Max(Max(b, d), Max(f, h));
        // 下は黒潰れ、上は白飛びまでの余裕。分母は符号を保ったままゼロから離す。
        var hitMin = Min(min4, e) / Max(4f * max4, Epsilon);
        var hitMax = (1f - Max(max4, e)) / Min(4f * min4 - 4f, -Epsilon);
        return Max(-hitMin, hitMax);
    }

    // MathF.Max / MathF.Min は JIT が maxss / minss へ落とす組み込み。
    // 「a > b ? a : b」に書き換えると分岐になり、絵柄によっては分岐予測が外れて
    // かえって遅くなる（ランダム画素の実測で 1 フレーム 7ms → 19ms）。ここは組み込みのまま使う。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Max(float a, float b) => MathF.Max(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Min(float a, float b) => MathF.Min(a, b);

    /// <summary>鮮鋭化後の値を 0〜255 の 1 チャンネルへ戻す。丸めは +0.5 の切り捨てで済ませる
    /// （MathF.Round は最近接偶数の判定が入り、画素ごとに呼ぶには重い）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Resolve(float b, float d, float f, float h, float e, float lobe, float rcp)
    {
        var value = (lobe * (b + d + f + h) + e) * rcp * 255f + 0.5f;
        return (uint)Math.Clamp((int)value, 0, 255);
    }
}
