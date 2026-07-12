using System.Runtime.InteropServices;

namespace Kiriha.Services;

internal enum FileOperationOutcome
{
    Success,
    Cancelled,
    Failed,
}

internal readonly record struct FileOperationResult(FileOperationOutcome Outcome, int NativeErrorCode)
{
    public bool IsSuccess => Outcome == FileOperationOutcome.Success;
    public bool IsCancelled => Outcome == FileOperationOutcome.Cancelled;
}

/// <summary>
/// SHFileOperationW によるファイル操作。ごみ箱への削除・進捗ダイアログ・
/// 名前競合ダイアログなど Windows 標準（エクスプローラー同等）の UI/挙動になる。
/// </summary>
internal static partial class FileOperationService
{
    private const uint FoMove = 1;
    private const uint FoCopy = 2;
    private const uint FoDelete = 3;
    private const uint FoRename = 4;

    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofRenameOnCollision = 0x0008;

    /// <summary>コピーまたは移動。競合時はエクスプローラー標準のダイアログが出る。</summary>
    public static FileOperationResult CopyOrMove(IReadOnlyList<string> sources, string destDir, bool move, bool renameOnCollision = false)
        => Execute(move ? FoMove : FoCopy, JoinPaths(sources), destDir + "\0\0",
            (ushort)(FofAllowUndo | (renameOnCollision ? FofRenameOnCollision : 0)));

    /// <summary>ごみ箱へ削除（Explorer 同様 Undo 可能）。permanent = true で完全削除（システム確認あり）。</summary>
    public static FileOperationResult DeleteToRecycleBin(IReadOnlyList<string> sources, bool permanent = false)
        => Execute(FoDelete, JoinPaths(sources), null, permanent ? (ushort)0 : FofAllowUndo);

    /// <summary>ごみ箱を空にする（システム確認ダイアログあり）。</summary>
    public static void EmptyRecycleBin(nint hwnd)
        => SHEmptyRecycleBinW(hwnd, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial int SHEmptyRecycleBinW(nint hwnd, nint pszRootPath, uint flags);

    /// <summary>名前の変更。</summary>
    public static FileOperationResult Rename(string source, string newPath)
        => Execute(FoRename, source + "\0\0", newPath + "\0\0", FofAllowUndo);

    /// <summary>エクスプローラーの「プロパティ」ダイアログを表示する。</summary>
    public static void ShowProperties(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x0000000C, // SEE_MASK_INVOKEIDLIST | SEE_MASK_NOCLOSEPROCESS 相当
            Verb = Marshal.StringToHGlobalUni("properties"),
            File = Marshal.StringToHGlobalUni(path),
            Show = 1,
        };
        try
        {
            ShellExecuteExW(ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(info.Verb);
            Marshal.FreeHGlobal(info.File);
        }
    }

    private static string JoinPaths(IReadOnlyList<string> paths)
        => string.Join('\0', paths) + "\0\0";

    private static FileOperationResult Execute(uint func, string from, string? to, ushort flags)
    {
        var pFrom = Marshal.StringToHGlobalUni(from);
        var pTo = to is null ? 0 : Marshal.StringToHGlobalUni(to);
        try
        {
            var op = new ShFileOpStruct
            {
                Func = func,
                From = pFrom,
                To = pTo,
                Flags = flags,
            };
            var error = SHFileOperationW(ref op);
            if (error != 0)
            {
                return new FileOperationResult(FileOperationOutcome.Failed, error);
            }

            return op.AnyOperationsAborted != 0
                ? new FileOperationResult(FileOperationOutcome.Cancelled, 0)
                : new FileOperationResult(FileOperationOutcome.Success, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
            if (pTo != 0)
            {
                Marshal.FreeHGlobal(pTo);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint Func;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Hwnd;
        public nint Verb;
        public nint File;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        public nint Class;
        public nint KeyClass;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref ShFileOpStruct op);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ref ShellExecuteInfo info);
}
