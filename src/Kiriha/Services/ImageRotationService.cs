namespace Kiriha.Services;

/// <summary>
/// 静止画の無劣化回転の窓口。形式ごとに最適な方式へ振り分ける。
///
/// - JPEG / TIFF: Exif の Orientation を書き換えるだけ（<see cref="ExifOrientationService"/>）。
///   画素に触れないので一瞬で終わり、圧縮も掛け直さない。
/// - PNG: 圧縮が可逆なので、画素を並べ替えて再エンコードする（<see cref="PngRotationService"/>）。
///   こちらも画質は落ちない（ファイルサイズだけは再圧縮で前後する）。
///
/// 呼び出し側はこのクラスだけを見れば良い。形式が増えたときもここへ足す。
/// </summary>
internal static class ImageRotationService
{
    /// <summary>この拡張子を無劣化回転の対象にできるか（実際に回せるかはファイル内容次第）。</summary>
    public static bool CanRotate(string extension)
        => ExifOrientationService.CanRotate(extension) || PngRotationService.CanRotate(extension);

    /// <summary>90 度ぶん回転して同じパスへ保存する。成功したら true。</summary>
    /// <param name="path">対象ファイル。</param>
    /// <param name="clockwise">true で右回り、false で左回り。</param>
    public static bool TryRotate(string path, bool clockwise)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return PngRotationService.CanRotate(extension)
            ? PngRotationService.TryRotate(path, clockwise)
            : ExifOrientationService.TryRotate(path, clockwise);
    }
}
