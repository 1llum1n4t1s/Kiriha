using Avalonia.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace Kiriha.Services;

/// <summary>PDF の先頭ページを、環境の Shell 拡張に依存せずサムネイル化する。</summary>
internal static class PdfThumbnailService
{
    public static Bitmap? TryGetThumbnail(string path, int width)
    {
        try
        {
            // pdfium もネイティブ側から managed ストリームを読み返すため、
            // 途中の I/O 例外でプロセスが落ちないようメモリへ読み切ってから渡す（ImageDecodeService 参照）。
            if (ImageDecodeService.TryReadAllBytes(path) is not { } bytes)
            {
                return null;
            }

            using var pdfStream = new MemoryStream(bytes, writable: false);
            using var rendered = Conversion.ToImage(
                pdfStream,
                page: 0,
                leaveOpen: false,
                password: null,
                options: new RenderOptions
                {
                    Width = width,
                    Height = null,
                    WithAspectRatio = true,
                });
            using var image = SKImage.FromBitmap(rendered);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            using var imageStream = encoded.AsStream();
            return new Bitmap(imageStream);
        }
        catch
        {
            // 破損・暗号化 PDF などは呼び出し側で通常アイコンへフォールバックする。
            return null;
        }
    }
}
