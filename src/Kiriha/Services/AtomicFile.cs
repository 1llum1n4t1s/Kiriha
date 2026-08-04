using System.Text;

namespace Kiriha.Services;

/// <summary>
/// 「書いている途中で落ちても元ファイルが壊れない」書き込み。
///
/// <see cref="File.WriteAllBytes(string, byte[])"/> は先に対象を 0 バイトへ切り詰めてから書くため、
/// 途中でプロセスが終了したり I/O が失敗したりすると、元の中身が失われた中途半端なファイルが残る。
/// 設定 JSON なら既定値へ戻るだけで済むが、ギャラリーの回転はユーザーの写真そのものを書き換える
/// ので、失敗が即データ損失になる。そこで同じディレクトリへ一時ファイルを書き切ってから、
/// 置き換え（<see cref="File.Replace(string, string, string?)"/>）で本ファイルと差し替える。
/// 置き換えは「成功して新しい内容」か「失敗して元のまま」のどちらかにしかならない。
///
/// 一時ファイルを同じディレクトリに作るのは、別ボリューム間だと置き換えができずコピーになるため
/// （%TEMP% が別ドライブのことは珍しくない）。
/// </summary>
internal static class AtomicFile
{
    /// <summary>バイト列を原子的に書き込む。失敗時は例外を投げ、元ファイルは触らない。</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
            ?? throw new IOException($"書き込み先のディレクトリを特定できません: {path}");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".kiriha-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                // 置き換え前にディスクまで送っておく（電源断で「置き換えは済んだが中身は空」を避ける）
                stream.Flush(flushToDisk: true);
            }

            Replace(temporary, full);
        }
        finally
        {
            // 置き換えに成功していれば既に無い。失敗経路でゴミを残さないための後始末。
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex)
            {
                Logger.Log($"一時ファイルを削除できませんでした: {temporary}（{ex.GetType().Name}）", LogLevel.Debug);
            }
        }
    }

    /// <summary>文字列を UTF-8（BOM なし）で原子的に書き込む。</summary>
    public static void WriteAllText(string path, string text)
        => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>一時ファイルを本ファイルへ差し替える。</summary>
    private static void Replace(string temporary, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporary, destination);
            return;
        }

        try
        {
            // File.Replace は元ファイルの属性・ACL・作成日時を引き継ぐ（写真の書き戻しではこちらが望ましい）。
            File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // ReplaceFile を実装しないファイルシステム（クラウド同期ドライブなど）向けの退避路。
            // MoveFileEx 相当なので、こちらも「置き換わるか元のまま」のどちらかで中途半端にはならない。
            Logger.Log($"置き換えに失敗したため移動で差し替えます: {destination}（{ex.GetType().Name}）", LogLevel.Debug);
            File.Move(temporary, destination, overwrite: true);
        }
    }
}
