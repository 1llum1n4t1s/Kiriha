using SkiaSharp;

namespace Kiriha.Services;

/// <summary>シェーダーへ渡す 1 回ぶんの設定値。UI スレッドで組み立て、描画スレッドで使う。</summary>
/// <param name="Mode">0 = 鮮鋭化なし / 1 = RCAS / 2 = CAS。</param>
/// <param name="Sharpness">RCAS の sharpness 係数（CPU 版の con と同じ）。</param>
/// <param name="Denoise">RCAS のノイズ検出を掛けるか（1 = 掛ける）。</param>
/// <param name="CasPeak">CAS の掛かり方を決める係数（負の値）。</param>
/// <param name="GammaExponent">ガンマ補正の指数（1 / gamma）。1 なら素通し。</param>
internal readonly record struct GalleryShaderParameters(
    int Mode,
    float Sharpness,
    float Denoise,
    float CasPeak,
    float GammaExponent);

/// <summary>
/// ギャラリーの映像に掛ける鮮鋭化（RCAS / CAS）とガンマ補正の SkSL シェーダー。
///
/// 中身のアルゴリズムは <see cref="ContrastAdaptiveSharpenService"/> の CPU 実装と同じで、
/// フル HD 1 フレームあたり 8〜10ms 掛かっていた処理を描画時の GPU へ移すためのもの。
/// CPU 版は Skia のリースを取れなかったときの逃げ道として残してある。
///
/// 隣接画素は「出力側の 1 画素」ぶん離してサンプリングする。拡大して表示しているときは
/// 元画素より細かく見ても意味がないので、元画素 1 つぶんを下限にする（FSR が EASU で
/// 拡大した後段に RCAS を置いているのと同じ考え方＝縮小拡大の後に締める）。
/// </summary>
internal static class GalleryShaderService
{
    private const string Source = """
        uniform shader src;
        uniform float2 tapStep;
        uniform float con;
        uniform float denoise;
        uniform float mode;
        uniform float casPeak;
        uniform float gammaExp;

        const float LIMIT = 0.1875;
        const float EPS = 0.00001;

        float luma(float3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }

        float lobeOf(float b, float d, float f, float h, float e) {
            float mn4 = min(min(b, d), min(f, h));
            float mx4 = max(max(b, d), max(f, h));
            float hitMin = min(mn4, e) / max(4.0 * mx4, EPS);
            float hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, -EPS);
            return max(-hitMin, hitMax);
        }

        half4 main(float2 coord) {
            float3 e = float3(src.eval(coord).rgb);
            float3 result = e;

            if (mode > 1.5) {
                // CAS（3x3 参照）。上限が緩いぶん強く掛かる。
                float3 a  = float3(src.eval(coord + float2(-tapStep.x, -tapStep.y)).rgb);
                float3 b  = float3(src.eval(coord + float2(     0.0, -tapStep.y)).rgb);
                float3 c  = float3(src.eval(coord + float2( tapStep.x, -tapStep.y)).rgb);
                float3 d  = float3(src.eval(coord + float2(-tapStep.x,     0.0)).rgb);
                float3 f  = float3(src.eval(coord + float2( tapStep.x,     0.0)).rgb);
                float3 g  = float3(src.eval(coord + float2(-tapStep.x,  tapStep.y)).rgb);
                float3 h  = float3(src.eval(coord + float2(     0.0,  tapStep.y)).rgb);
                float3 i  = float3(src.eval(coord + float2( tapStep.x,  tapStep.y)).rgb);

                float3 mn = min(min(min(d, b), min(e, h)), f);
                float3 mx = max(max(max(d, b), max(e, h)), f);
                mn += min(min(a, g), min(c, i));
                mx += max(max(a, g), max(c, i));

                float3 amp = clamp(min(mn, float3(2.0) - mx) / max(mx, float3(EPS)), 0.0, 1.0);
                float3 w = sqrt(amp) * casPeak;
                result = ((b + d + f + h) * w + e) / (float3(1.0) + 4.0 * w);
            } else if (mode > 0.5) {
                // RCAS（上下左右の 4 画素だけ参照）。
                float3 b = float3(src.eval(coord + float2(    0.0, -tapStep.y)).rgb);
                float3 d = float3(src.eval(coord + float2(-tapStep.x,     0.0)).rgb);
                float3 f = float3(src.eval(coord + float2( tapStep.x,     0.0)).rgb);
                float3 h = float3(src.eval(coord + float2(    0.0,  tapStep.y)).rgb);

                float noise = 1.0;
                if (denoise > 0.5) {
                    float bL = luma(b);
                    float dL = luma(d);
                    float eL = luma(e);
                    float fL = luma(f);
                    float hL = luma(h);
                    float range = max(max(bL, dL), max(max(fL, hL), eL))
                                - min(min(bL, dL), min(min(fL, hL), eL));
                    float bump = 0.25 * (bL + dL + fL + hL) - eL;
                    bump = range > EPS ? min(abs(bump) / range, 1.0) : 0.0;
                    noise = 1.0 - 0.5 * bump;
                }

                float lobe = max(lobeOf(b.r, d.r, f.r, h.r, e.r),
                             max(lobeOf(b.g, d.g, f.g, h.g, e.g),
                                 lobeOf(b.b, d.b, f.b, h.b, e.b)));
                lobe = max(-LIMIT, min(lobe, 0.0)) * con * noise;
                result = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);
            }

            result = clamp(result, 0.0, 1.0);
            if (abs(gammaExp - 1.0) > 0.0005) {
                result = pow(result, float3(gammaExp));
            }

            return half4(half3(result), 1.0);
        }
        """;

    private static SKRuntimeEffect? s_effect;
    private static bool s_compiled;

    /// <summary>コンパイルに失敗した理由（成功時は null）。テストと調査用。</summary>
    public static string? LastError { get; private set; }

    /// <summary>コンパイル済みのシェーダー。作れなければ null（呼び出し側はそのまま描画する）。</summary>
    public static SKRuntimeEffect? Effect
    {
        get
        {
            if (s_compiled)
            {
                return s_effect;
            }

            s_compiled = true;
            try
            {
                s_effect = SKRuntimeEffect.CreateShader(Source, out var errors);
                if (s_effect is null)
                {
                    LastError = errors;
                    Logger.Log($"ギャラリー用シェーダーをコンパイルできませんでした: {errors}", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Log($"ギャラリー用シェーダーを用意できませんでした: {ex.Message}", LogLevel.Warning);
                s_effect = null;
            }

            return s_effect;
        }
    }

    /// <summary>いまの設定からシェーダー用のパラメータを作る。</summary>
    public static GalleryShaderParameters CurrentParameters(double gamma)
        => new(
            ContrastAdaptiveSharpenService.ShaderMode,
            ContrastAdaptiveSharpenService.ShaderSharpness,
            ContrastAdaptiveSharpenService.ShaderDenoise ? 1f : 0f,
            ContrastAdaptiveSharpenService.ShaderCasPeak,
            gamma > 0 ? (float)(1.0 / gamma) : 1f);
}
