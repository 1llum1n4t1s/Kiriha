using System.Runtime.InteropServices;

namespace Kiriha.Services;

internal enum FileOperationOutcome
{
    Success,
    Cancelled,
    Failed,
    Busy,
}

internal readonly record struct FileOperationResult(FileOperationOutcome Outcome, int NativeErrorCode)
{
    public bool IsSuccess => Outcome == FileOperationOutcome.Success;
    public bool IsCancelled => Outcome == FileOperationOutcome.Cancelled;
    public bool IsBusy => Outcome == FileOperationOutcome.Busy;
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

    /// <summary>SHFileOperationW はプロセス内で同時に複数スレッドから呼び出すと進捗ダイアログの競合等で
    /// 失敗することがある旧世代の API（本物の Explorer が使う IFileOperation は COM インスタンスごとに
    /// 独立して並列実行できるが、こちらは非対応）。呼び出し元（タブ・右クリックメニュー等）に関わらず
    /// ここで直列化する。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

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

    private static string FuncName(uint func) => func switch
    {
        FoMove => "移動",
        FoCopy => "コピー",
        FoDelete => "削除",
        FoRename => "名前の変更",
        _ => func.ToString(),
    };

    /// <summary>SHFileOperationW の主要なエラーコードを利用者向けの日本語説明へ変換する。
    /// 未知のコードは空文字を返し、呼び出し元は生コード表示のままにする。</summary>
    public static string DescribeError(int code) => code switch
    {
        2 or 3 => "パスが見つかりません",           // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
        5 => "アクセスが拒否されました",             // ERROR_ACCESS_DENIED
        19 => "書き込み禁止です",                    // ERROR_WRITE_PROTECT
        32 => "他のプロセスが使用中です",            // ERROR_SHARING_VIOLATION
        112 => "ディスクの空き領域が不足しています", // ERROR_DISK_FULL
        206 => "パスが長すぎます",                   // ERROR_FILENAME_EXCED_RANGE
        0x71 => "同じファイルが既に存在します",      // DE_SAMEFILE
        0x7C => "パスが無効です",                    // DE_INVALIDFILES
        _ => string.Empty,
    };

    private static FileOperationResult Execute(uint func, string from, string? to, ushort flags)
    {
        // 0 タイムアウトで即座に試すだけ（待たない）。取得できなければ呼び出し元に「busy」を返し、
        // 呼び出し元が UI スレッドをブロックせず「お待ちください」等の案内を出せるようにする。
        if (!Gate.Wait(0))
        {
            return new FileOperationResult(FileOperationOutcome.Busy, 0);
        }

        try
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
                    // StatusText は次の操作で消えるため、事後調査用にログへも必ず残す
                    Logger.Log(
                        $"SHFileOperationW 失敗: func={FuncName(func)}, error={error}（{DescribeError(error)}）, from={from.TrimEnd('\0').Replace('\0', '|')}, to={to?.TrimEnd('\0')}",
                        LogLevel.Warning);
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
        finally
        {
            Gate.Release();
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
