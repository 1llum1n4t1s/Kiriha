using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>ごみ箱へ入った項目 1 件。<see cref="RecycledParsingName"/> は「ごみ箱名前空間での」解析名で、
/// 実ファイルパス（$R…）ではない。復元はごみ箱の項目のまま移動する必要があるため、こちらを持つ。</summary>
internal sealed record RecycledItem(string OriginalPath, string RecycledParsingName);

/// <summary>
/// 削除時に <see cref="IFileOperation"/> から通知を受け、ごみ箱へ入った項目を控えるシンク。
/// Ctrl+Z（元に戻す）で復元元として使う。ごみ箱を後から列挙して元パスで突き合わせる方法もあるが、
/// 同名ファイルを複数回削除した場合に取り違えるため、削除時に通知される「その項目そのもの」を使う。
/// </summary>
[GeneratedComClass]
internal sealed partial class DeleteRecycleSink : IFileOperationProgressSink
{
    private const int SigdnFilesysPath = unchecked((int)0x80058000);
    private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);

    public List<RecycledItem> Recycled { get; } = [];

    public int PostDeleteItem(uint flags, IShellItem item, int deleteResult, IShellItem? newlyCreated)
    {
        // newlyCreated はごみ箱へ入った後の姿。完全削除ではここが null になる。
        if (deleteResult >= 0
            && newlyCreated is not null
            && GetName(item, SigdnFilesysPath) is { Length: > 0 } original
            && GetName(newlyCreated, SigdnDesktopAbsoluteParsing) is { Length: > 0 } recycled)
        {
            Recycled.Add(new RecycledItem(original, recycled));
        }

        return 0;
    }

    private static string GetName(IShellItem item, int kind)
    {
        if (item.GetDisplayName(kind, out var pointer) < 0 || pointer == 0)
        {
            return "";
        }

        try
        {
            return Marshal.PtrToStringUni(pointer) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    // ここから下は使わないが、vtable の並びを保つために実装は省略できない。
    public int StartOperations() => 0;

    public int FinishOperations(int result) => 0;

    public int PreRenameItem(uint flags, IShellItem item, string newName) => 0;

    public int PostRenameItem(uint flags, IShellItem item, string newName, int renameResult, IShellItem? newlyCreated) => 0;

    public int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

    public int PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int moveResult, IShellItem? newlyCreated) => 0;

    public int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

    public int PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int copyResult, IShellItem? newlyCreated) => 0;

    public int PreDeleteItem(uint flags, IShellItem item) => 0;

    public int PreNewItem(uint flags, IShellItem destinationFolder, string? newName) => 0;

    public int PostNewItem(uint flags, IShellItem destinationFolder, string? newName, string? templateName, uint fileAttributes, int newResult, IShellItem? newItem) => 0;

    public int UpdateProgress(uint workTotal, uint workSoFar) => 0;

    public int ResetTimer() => 0;

    public int PauseTimer() => 0;

    public int ResumeTimer() => 0;
}

/// <summary>IFileOperation の進捗通知。メソッドは vtable 順に並べる必要があるため、
/// 使わないものも省略せず宣言する。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int result);

    [PreserveSig]
    int PreRenameItem(uint flags, IShellItem item, string newName);

    [PreserveSig]
    int PostRenameItem(uint flags, IShellItem item, string newName, int renameResult, IShellItem? newlyCreated);

    [PreserveSig]
    int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName);

    [PreserveSig]
    int PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int moveResult, IShellItem? newlyCreated);

    [PreserveSig]
    int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName);

    [PreserveSig]
    int PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int copyResult, IShellItem? newlyCreated);

    [PreserveSig]
    int PreDeleteItem(uint flags, IShellItem item);

    [PreserveSig]
    int PostDeleteItem(uint flags, IShellItem item, int deleteResult, IShellItem? newlyCreated);

    [PreserveSig]
    int PreNewItem(uint flags, IShellItem destinationFolder, string? newName);

    [PreserveSig]
    int PostNewItem(uint flags, IShellItem destinationFolder, string? newName, string? templateName, uint fileAttributes, int newResult, IShellItem? newItem);

    [PreserveSig]
    int UpdateProgress(uint workTotal, uint workSoFar);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}
