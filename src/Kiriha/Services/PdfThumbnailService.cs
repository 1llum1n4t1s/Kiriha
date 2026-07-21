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
            using var pdfStream = File.OpenRead(path);
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
