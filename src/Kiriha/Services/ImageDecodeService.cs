using Avalonia.Media.Imaging;

namespace Kiriha.Services;

/// <summary>
/// 画像・PDF をデコードライブラリへ渡す前に、必ずメモリへ読み切るための窓口。
///
/// Bitmap.DecodeToWidth(FileStream) のようにファイルストリームを直接渡すと、Skia の
/// ネイティブコードから managed ストリームの Read がコールバックされる（SKManagedStream）。
/// そこで I/O 例外が投げられると、ネイティブフレームを越えて巻き戻すことができず、
/// 呼び出し側の try/catch には届かないまま未処理例外としてプロセスが即死する。
/// Google ドライブなどの仮想ドライブでは実体取得の瞬断で「ファンクションが間違っています」
/// （ERROR_INVALID_FUNCTION）「セマフォがタイムアウトしました」（ERROR_SEM_TIMEOUT）が実際に発生し、
/// 画像フォルダーの閲覧中にクラッシュする事例があった。
///
/// 先に読み切っておけば I/O 失敗は通常の managed 例外として捕捉でき、デコードは読み取りが
/// 失敗しない MemoryStream 上で行われるため、この経路でプロセスが落ちることはなくなる。
/// </summary>
internal static class ImageDecodeService
{
    /// <summary>クラウド同期ドライブの瞬断向けに 1 回だけ置く待ち時間。</summary>
    private const int RetryDelayMilliseconds = 150;

    /// <summary>ファイルをメモリへ読み切ってから指定幅でデコードする。読み取りに失敗したら null。</summary>
    public static Bitmap? TryDecodeToWidth(string path, int width, CancellationToken token = default)
    {
        if (TryReadAllBytes(path, token) is not { } bytes)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        return Bitmap.DecodeToWidth(stream, width);
    }

    /// <summary>ファイル全体をメモリへ読み込む。失敗したら（1 回だけ再試行したうえで）null を返す。</summary>
    public static byte[]? TryReadAllBytes(string path, CancellationToken token = default)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            // 仮想ドライブの実体取得失敗は一時的なことがあるため、少し待って 1 回だけ読み直す
            Logger.Log($"画像ファイルを読み込めませんでした（再試行します）: {path} ({ex.Message})", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Logger.Log($"画像ファイルを読み込めませんでした: {path} ({ex.Message})", LogLevel.Debug);
            return null;
        }

        if (token.IsCancellationRequested)
        {
            return null;
        }

        Thread.Sleep(RetryDelayMilliseconds);
        if (token.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Logger.Log($"画像ファイルを読み込めませんでした（再試行後）: {path} ({ex.Message})", LogLevel.Debug);
            return null;
        }
    }
}
