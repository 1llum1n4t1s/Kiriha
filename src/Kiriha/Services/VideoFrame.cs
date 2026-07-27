using Avalonia;

namespace Kiriha.Services;

/// <summary>
/// 1 枚ぶんの動画フレーム（BGRA / 行間の余白なし）。
///
/// 以前は <c>WriteableBitmap</c> をそのまま画面へ渡していたが、鮮鋭化とガンマを
/// GPU シェーダーへ移した（<see cref="Controls.VideoFrameView"/>）ため、
/// ここでは素の画素だけを持ち、Skia の <c>SKImage</c> は描画側で作る。
///
/// 中身は UI スレッドだけが書き換える。バインディングに変化を気付かせるため、
/// <see cref="VideoPlaybackSession"/> は 2 枚を交互に差し替えて必ず別インスタンスを見せる。
/// </summary>
// TabViewModel の public なプロパティで公開するため public にしている。
public sealed class VideoFrame
{
    public VideoFrame(PixelSize size)
    {
        Size = size;
        Stride = size.Width * 4;
        Pixels = new byte[(long)Stride * size.Height];
    }

    public PixelSize Size { get; }

    /// <summary>1 行あたりのバイト数。</summary>
    public int Stride { get; }

    public byte[] Pixels { get; }
}
