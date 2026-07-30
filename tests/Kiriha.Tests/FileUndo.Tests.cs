using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// Ctrl+Z（元に戻す）の履歴。実際の復元は Windows のごみ箱 COM に委ねるため、
/// ここでは「何を、どの順で、いくつ覚えているか」だけを固定する。
/// </summary>
public class FileUndoTests
{
    private static RecycledItem Item(string name)
        => new($@"C:\src\{name}", $@"::{{645FF040-5081-101B-9F08-00AA002F954E}}\{name}");

    /// <summary>各テストの開始時点で履歴を空にする（静的な履歴を共有するため）。</summary>
    private static void Drain()
    {
        while (FileUndoService.PopDelete() is not null)
        {
        }
    }

    [Fact]
    public void 履歴が空なら取り出せない()
    {
        Drain();

        Assert.False(FileUndoService.CanUndo);
        Assert.Null(FileUndoService.PopDelete());
    }

    [Fact]
    public void 積んだ削除をそのまま取り出せる()
    {
        Drain();
        var pushed = new[] { Item("a.txt"), Item("b.txt") };

        FileUndoService.PushDelete(pushed);

        Assert.True(FileUndoService.CanUndo);
        var popped = FileUndoService.PopDelete();
        Assert.Equal(pushed, popped);
        Assert.False(FileUndoService.CanUndo);
    }

    [Fact]
    public void 取り出しは新しいものから()
    {
        Drain();
        FileUndoService.PushDelete([Item("old.txt")]);
        FileUndoService.PushDelete([Item("new.txt")]);

        Assert.Equal(@"C:\src\new.txt", FileUndoService.PopDelete()![0].OriginalPath);
        Assert.Equal(@"C:\src\old.txt", FileUndoService.PopDelete()![0].OriginalPath);
    }

    [Fact]
    public void 空の削除は積まない()
    {
        Drain();

        FileUndoService.PushDelete([]);

        Assert.False(FileUndoService.CanUndo);
    }

    [Fact]
    public void 上限を超えたら古いものから捨てる()
    {
        Drain();
        for (var i = 0; i < 40; i++)
        {
            FileUndoService.PushDelete([Item($"f{i}.txt")]);
        }

        // 直近 32 件だけが残る（f8 〜 f39）
        var seen = new List<string>();
        while (FileUndoService.PopDelete() is { } entry)
        {
            seen.Add(entry[0].OriginalPath);
        }

        Assert.Equal(32, seen.Count);
        Assert.Equal(@"C:\src\f39.txt", seen[0]);
        Assert.Equal(@"C:\src\f8.txt", seen[^1]);
    }
}
