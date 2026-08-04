using System.Text;
using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// 原子的なファイル書き込みの回帰テスト。
///
/// この仕組みの目的は「途中で失敗しても元ファイルを壊さない」ことなので、正常系の内容確認だけでなく、
/// 書き込みが失敗する経路で元の中身が残ること、置き換え用の一時ファイルを残さないことを押さえる。
/// </summary>
public class AtomicFileTests
{
    [Fact]
    public void WriteAllBytes_CreatesNewFile()
    {
        using var temp = new TempDirectory("atomic-new");
        var path = temp.Combine("created.bin");

        AtomicFile.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal<byte[]>([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllBytes_ReplacesExistingContent()
    {
        using var temp = new TempDirectory("atomic-replace");
        var path = temp.Combine("existing.bin");
        File.WriteAllBytes(path, new byte[64]);

        AtomicFile.WriteAllBytes(path, [9, 9]);

        Assert.Equal<byte[]>([9, 9], File.ReadAllBytes(path));
    }

    /// <summary>置き換えは 1 回の操作で完了し、作業用の一時ファイルをディレクトリに残さない。</summary>
    [Fact]
    public void WriteAllBytes_LeavesNoTemporaryFiles()
    {
        using var temp = new TempDirectory("atomic-clean");
        var path = temp.Combine("target.bin");

        AtomicFile.WriteAllBytes(path, [1]);
        AtomicFile.WriteAllBytes(path, [2]);

        var files = Directory.GetFiles(temp.Root, "*", SearchOption.AllDirectories);
        Assert.Equal([path], files);
    }

    /// <summary>
    /// 置き換えに失敗したときは、元ファイルの中身がそのまま残る（直書きとの決定的な違い）。
    /// 直書き（File.WriteAllBytes）なら、この状況では先に 0 バイトへ切り詰められてしまう。
    /// </summary>
    [Fact]
    public void WriteAllBytes_KeepsOriginalWhenReplaceFails()
    {
        using var temp = new TempDirectory("atomic-fail");
        var path = temp.Combine("photo.bin");
        var original = new byte[] { 7, 7, 7, 7 };
        File.WriteAllBytes(path, original);

        // 対象を他プロセスが排他で掴んでいる状態を作る（置き換え・移動のどちらも失敗する）
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllBytes(path, [1, 2]));
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        // 失敗しても一時ファイルは残さない
        Assert.Equal([path], Directory.GetFiles(temp.Root));
    }

    [Fact]
    public void WriteAllText_WritesUtf8WithoutBom()
    {
        using var temp = new TempDirectory("atomic-text");
        var path = temp.Combine("state.json");

        AtomicFile.WriteAllText(path, "{\"k\":\"あ\"}");

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("{\"k\":\"あ\"}", new UTF8Encoding(false).GetString(bytes));
    }
}
