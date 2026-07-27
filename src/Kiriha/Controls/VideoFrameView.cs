using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Kiriha.Services;
using SkiaSharp;

namespace Kiriha.Controls;

/// <summary>
/// ギャラリーの動画フレームを描く。<c>Image</c> の代わりに使う。
///
/// 鮮鋭化（RCAS / CAS）とガンマ補正を、CPU で画素を書き換えるのではなく描画時の
/// SkSL シェーダー（<see cref="GalleryShaderService"/>）で掛けるためのコントロール。
/// フル HD の 1 フレームに CPU で 8〜10ms 掛かっていたぶんがまるごと GPU へ移る。
///
/// レイアウトは <c>Stretch="Uniform"</c> の <c>Image</c> と同じ（映像の縦横比を保って領域に収める）。
/// Skia のリースを取れない場合や SkSL のコンパイルに失敗した場合は、シェーダー無しで
/// そのまま描画する（絵が出ないよりは効果が乗らないほうがまし）。
/// </summary>
internal sealed class VideoFrameView : Control
{
    public static readonly StyledProperty<VideoFrame?> FrameProperty =
        AvaloniaProperty.Register<VideoFrameView, VideoFrame?>(nameof(Frame));

    /// <summary>ガンマ補正の値（1.0 で素通し）。</summary>
    public static readonly StyledProperty<double> GammaProperty =
        AvaloniaProperty.Register<VideoFrameView, double>(nameof(Gamma), 1.0);

    /// <summary>鮮鋭化設定など、フレーム以外の理由で描き直したいときに増やしてもらう値。</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<VideoFrameView, int>(nameof(Revision));

    static VideoFrameView()
    {
        AffectsRender<VideoFrameView>(FrameProperty, GammaProperty, RevisionProperty);
    }

    /// <summary>
    /// フレームが差し替わるたびにレイアウトをやり直すと、30 回/秒で測定と配置が走る。
    /// 映像の大きさは再生中は変わらないので、寸法が変わったときだけ測り直す。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty
            && (change.GetOldValue<VideoFrame?>()?.Size ?? default) != (change.GetNewValue<VideoFrame?>()?.Size ?? default))
        {
            InvalidateMeasure();
        }
    }

    public VideoFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public double Gamma
    {
        get => GetValue(GammaProperty);
        set => SetValue(GammaProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => Fit(availableSize, Frame?.Size);

    protected override Size ArrangeOverride(Size finalSize) => Fit(finalSize, Frame?.Size);

    /// <summary>映像の縦横比を保ったまま領域に収めた大きさ（Stretch=Uniform 相当）。</summary>
    private static Size Fit(Size available, PixelSize? source)
    {
        if (source is not { Width: > 0, Height: > 0 } size)
        {
            return default;
        }

        var scaleX = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / size.Width;
        var scaleY = double.IsInfinity(available.Height) ? double.PositiveInfinity : available.Height / size.Height;
        if (double.IsInfinity(scaleX) && double.IsInfinity(scaleY))
        {
            return new Size(size.Width, size.Height);
        }

        var scale = Math.Min(scaleX, scaleY);
        return new Size(size.Width * scale, size.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        if (Frame is not { } frame
            || frame.Size.Width <= 0
            || Bounds.Width <= 0
            || Bounds.Height <= 0)
        {
            return;
        }

        // 画素の複製は UI スレッド側で済ませる。SKImage は不変になるので、
        // 描画スレッドがこれを使っている間に次のフレームが上書きされても壊れない。
        var image = CreateImage(frame);
        if (image is null)
        {
            return;
        }

        context.Custom(new FrameDrawOperation(
            new Rect(Bounds.Size),
            image,
            frame.Size,
            GalleryShaderService.CurrentParameters(Gamma)));
    }

    private static SKImage? CreateImage(VideoFrame frame)
    {
        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(frame.Size.Width, frame.Size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            return SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), frame.Stride);
        }
        catch (Exception ex)
        {
            Logger.Log($"動画フレームの画像化に失敗しました: {ex.Message}", LogLevel.Debug);
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Skia のキャンバスへ直接描く 1 回ぶんの描画。SKImage の破棄はここが持つ。</summary>
    private sealed class FrameDrawOperation(
        Rect bounds,
        SKImage image,
        PixelSize source,
        GalleryShaderParameters parameters) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public void Dispose() => image.Dispose();

        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var destination = SKRect.Create((float)bounds.Width, (float)bounds.Height);

            // 縮小して出すときだけ Mitchell（従来の HighQuality 相当）にする。
            // 等倍〜拡大では線形補間で十分で、シェーダーは 1 画素につき最大 9 回サンプリングするため。
            var sampling = bounds.Width < source.Width
                ? new SKSamplingOptions(SKCubicResampler.Mitchell)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

            var effect = GalleryShaderService.Effect;
            if (effect is null)
            {
                canvas.DrawImage(image, destination, sampling);
                return;
            }

            var localMatrix = SKMatrix.CreateScale(
                (float)(bounds.Width / source.Width),
                (float)(bounds.Height / source.Height));
            using var imageShader = image.ToShader(
                SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, localMatrix);

            // 隣接画素までの距離。出力の 1 画素ぶんを基本にしつつ、元画素より細かくは見ない
            // （ズーム中に元素材に無い細かさで平均を取っても効かなくなるだけなので）。
            var matrix = canvas.TotalMatrix;
            var determinant = Math.Abs((matrix.ScaleX * matrix.ScaleY) - (matrix.SkewX * matrix.SkewY));
            var deviceScale = determinant > 0 ? Math.Sqrt(determinant) : 1.0;
            var stepX = (float)Math.Max(1.0 / deviceScale, bounds.Width / source.Width);
            var stepY = (float)Math.Max(1.0 / deviceScale, bounds.Height / source.Height);

            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["tapStep"] = new[] { stepX, stepY },
                ["con"] = parameters.Sharpness,
                ["denoise"] = parameters.Denoise,
                ["mode"] = (float)parameters.Mode,
                ["casPeak"] = parameters.CasPeak,
                ["gammaExp"] = parameters.GammaExponent,
            };
            var children = new SKRuntimeEffectChildren(effect)
            {
                ["src"] = imageShader,
            };

            using var shader = effect.ToShader(uniforms, children);
            if (shader is null)
            {
                canvas.DrawImage(image, destination, sampling);
                return;
            }

            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            canvas.DrawRect(destination, paint);
        }
    }
}
