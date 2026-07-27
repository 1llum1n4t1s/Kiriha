using System.Runtime.InteropServices;
using Kiriha.Services;
using SkiaSharp;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// 動画に掛ける GPU シェーダー（<see cref="GalleryShaderService"/>）が、
/// CPU 実装（<see cref="ContrastAdaptiveSharpenService"/> / <see cref="GammaAdjustService"/>）と
/// 同じ絵を出すことを固定する。
///
/// 実機の画面はここでは見られないので、代わりに Skia のラスタ面へ同じシェーダーを流して
/// 画素を突き合わせる。これが通っていれば、少なくとも「SkSL がコンパイルできて、
/// アルゴリズムが CPU 版とずれていない」ことは保証できる。
///
/// 端の画素は比較しない。CPU 版は端をそのまま写すのに対し、シェーダーは
/// タイルモード Clamp で端の画素を複製して参照するため、仕様として結果が違う。
/// </summary>
[Collection(SharpenSettingsCollection.Name)]
public sealed class GalleryShaderTests
{
    /// <summary>8bit の丸めと half 精度のぶれを見込んだ許容差。</summary>
    private const int Tolerance = 2;

    public GalleryShaderTests()
    {
        ContrastAdaptiveSharpenService.Enabled = true;
        ContrastAdaptiveSharpenService.Strength = SharpenStrength.Normal;
        GammaAdjustService.Gamma = GammaAdjustService.Neutral;
    }

    [Fact]
    public void SkSLがコンパイルできる()
        => Assert.True(GalleryShaderService.Effect is not null, GalleryShaderService.LastError);

    [Theory]
    [InlineData(SharpenStrength.Low)]
    [InlineData(SharpenStrength.Normal)]
    [InlineData(SharpenStrength.High)]
    [InlineData(SharpenStrength.Max)]
    internal void 鮮鋭化の結果がCPU実装と一致する(SharpenStrength strength)
    {
        ContrastAdaptiveSharpenService.Strength = strength;
        const int size = 24;
        var source = BuildPattern(size);

        var expected = RunCpu(source, size);
        var actual = RunShader(source, size);

        AssertInteriorMatches(expected, actual, size);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.8)]
    public void ガンマ補正の結果がCPU実装と一致する(double gamma)
    {
        // 鮮鋭化を切って、ガンマだけを突き合わせる。
        ContrastAdaptiveSharpenService.Enabled = false;
        GammaAdjustService.Gamma = gamma;

        const int size = 16;
        var source = BuildPattern(size);

        var expected = (uint[])source.Clone();
        ApplyGammaCpu(expected, size);
        var actual = RunShader(source, size);

        AssertInteriorMatches(expected, actual, size);
    }

    [Fact]
    public void 鮮鋭化とガンマを同時に掛けてもCPU実装と一致する()
    {
        GammaAdjustService.Gamma = 1.4;
        const int size = 24;
        var source = BuildPattern(size);

        var expected = RunCpu(source, size);
        ApplyGammaCpu(expected, size);
        var actual = RunShader(source, size);

        AssertInteriorMatches(expected, actual, size);
    }

    /// <summary>平坦部・段差・斜めの境目・グラデーションを 1 枚に詰めた不透明な絵。</summary>
    private static uint[] BuildPattern(int size)
    {
        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                byte r = (byte)(x * 255 / (size - 1));
                byte g = (byte)(y * 255 / (size - 1));
                byte b = (byte)(x + y < size ? 40 : 210);
                if (x is > 6 and < 12 && y is > 6 and < 12)
                {
                    r = g = b = 128; // 平坦な面
                }

                pixels[(y * size) + x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }

        return pixels;
    }

    private static unsafe uint[] RunCpu(uint[] source, int size)
    {
        var destination = new uint[size * size];
        fixed (uint* buffer = destination)
        {
            ContrastAdaptiveSharpenService.Apply(source, size, size, (nint)buffer, size);
        }

        return destination;
    }

    private static unsafe void ApplyGammaCpu(uint[] pixels, int size)
    {
        fixed (uint* buffer = pixels)
        {
            GammaAdjustService.Apply((nint)buffer, size, size, size);
        }
    }

    /// <summary>シェーダーを等倍（1 画素 = 1 画素）でラスタ面へ流し、BGRA を取り出す。</summary>
    private static uint[] RunShader(uint[] source, int size)
    {
        var effect = GalleryShaderService.Effect;
        Assert.NotNull(effect);

        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        SKImage image;
        try
        {
            image = SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), size * 4);
        }
        finally
        {
            handle.Free();
        }

        using (image)
        using (var imageShader = image.ToShader(
                   SKShaderTileMode.Clamp,
                   SKShaderTileMode.Clamp,
                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                   SKMatrix.Identity))
        {
            var parameters = GalleryShaderService.CurrentParameters(GammaAdjustService.Gamma);
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["tapStep"] = new[] { 1f, 1f },
                ["con"] = parameters.Sharpness,
                ["denoise"] = parameters.Denoise,
                ["mode"] = (float)parameters.Mode,
                ["casPeak"] = parameters.CasPeak,
                ["gammaExp"] = parameters.GammaExponent,
            };
            var children = new SKRuntimeEffectChildren(effect) { ["src"] = imageShader };

            using var shader = effect.ToShader(uniforms, children);
            Assert.NotNull(shader);

            using var surface = SKSurface.Create(info);
            Assert.NotNull(surface);
            using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
            {
                surface.Canvas.DrawRect(SKRect.Create(size, size), paint);
            }

            var result = new uint[size * size];
            var resultHandle = GCHandle.Alloc(result, GCHandleType.Pinned);
            try
            {
                Assert.True(surface.ReadPixels(info, resultHandle.AddrOfPinnedObject(), size * 4, 0, 0));
            }
            finally
            {
                resultHandle.Free();
            }

            return result;
        }
    }

    private static void AssertInteriorMatches(uint[] expected, uint[] actual, int size)
    {
        for (var y = 1; y < size - 1; y++)
        {
            for (var x = 1; x < size - 1; x++)
            {
                var index = (y * size) + x;
                for (var shift = 0; shift <= 16; shift += 8)
                {
                    var want = (int)((expected[index] >> shift) & 0xFF);
                    var got = (int)((actual[index] >> shift) & 0xFF);
                    Assert.True(
                        Math.Abs(want - got) <= Tolerance,
                        $"({x},{y}) shift={shift}: CPU={want} シェーダー={got}");
                }
            }
        }
    }
}
