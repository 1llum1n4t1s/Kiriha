using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Kiriha.Controls;

/// <summary>
/// ギャラリーのメイン画像で使う「手のひらツール」のカーソル。
///
/// Windows の標準カーソル（<see cref="StandardCursorType"/>）には手のひら形が無く、
/// <c>Hand</c> はリンク用の指差しになってしまう。画像ビューアーの手のひらツールとして
/// 通じる形にするため、開いた手と握った手を実行時に描き起こしてカーソルにする。
///
/// 生成にはレンダリング基盤が要るので UI スレッドから最初に必要になった時点で作り、
/// 以降は使い回す（カーソルはウィンドウ間で共有してよい）。
/// </summary>
internal static class HandCursors
{
    private const int Size = 32;

    private static readonly IBrush Fill = Brushes.White;
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), 1.4);

    private static Cursor? s_open;
    private static Cursor? s_grabbing;

    /// <summary>開いた手（ドラッグしていないとき）。</summary>
    public static Cursor Open => s_open ??= Build(grabbing: false);

    /// <summary>握った手（ドラッグ中）。</summary>
    public static Cursor Grabbing => s_grabbing ??= Build(grabbing: true);

    private static Cursor Build(bool grabbing)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            if (grabbing)
            {
                DrawGrabbing(ctx);
            }
            else
            {
                DrawOpen(ctx);
            }
        }

        // ホットスポットは手のひらの中心。掴んだ位置がそのまま動くように見える。
        return new Cursor(bitmap, new PixelPoint(15, 16));
    }

    /// <summary>開いた手（指 4 本 + 親指 + 手のひら）。</summary>
    private static void DrawOpen(DrawingContext ctx)
    {
        Capsule(ctx, 9.0, 6.0, 4.4, 13.0);   // 人差し指
        Capsule(ctx, 13.8, 4.0, 4.4, 15.0);  // 中指
        Capsule(ctx, 18.6, 5.4, 4.4, 13.6);  // 薬指
        Capsule(ctx, 23.2, 8.0, 4.2, 11.0);  // 小指
        Thumb(ctx, -32);
        Palm(ctx, 8.6, 14.0, 19.0, 12.0, 5.5);
    }

    /// <summary>握った手（指を折り畳んだ状態）。ドラッグ中はこちらに切り替える。</summary>
    private static void DrawGrabbing(DrawingContext ctx)
    {
        Capsule(ctx, 9.6, 9.5, 4.2, 7.5);
        Capsule(ctx, 14.2, 8.2, 4.2, 8.5);
        Capsule(ctx, 18.8, 9.0, 4.2, 8.0);
        Capsule(ctx, 23.0, 10.4, 4.0, 7.0);
        Thumb(ctx, -18);
        Palm(ctx, 8.6, 14.0, 19.0, 12.0, 6.0);
    }

    /// <summary>親指。手のひらの左側から斜めに出す。</summary>
    private static void Thumb(DrawingContext ctx, double degrees)
    {
        var center = new Point(8.0, 18.0);
        using (ctx.PushTransform(
                   Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateRotation(degrees * Math.PI / 180)
                   * Matrix.CreateTranslation(center.X, center.Y)))
        {
            Capsule(ctx, 5.6, 12.0, 4.4, 10.0);
        }
    }

    private static void Capsule(DrawingContext ctx, double x, double y, double width, double height)
        => ctx.DrawRectangle(Fill, Outline, new RoundedRect(new Rect(x, y, width, height), width / 2));

    private static void Palm(DrawingContext ctx, double x, double y, double width, double height, double radius)
        => ctx.DrawRectangle(Fill, Outline, new RoundedRect(new Rect(x, y, width, height), radius));
}
