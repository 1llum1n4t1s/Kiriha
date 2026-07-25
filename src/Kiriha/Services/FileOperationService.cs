using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

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
/// IFileOperation（Explorer 本体と同じ COM API）によるファイル操作。ごみ箱への削除・進捗ダイアログ・
/// 名前競合ダイアログなど Windows 標準（エクスプローラー同等）の UI/挙動になる。
/// COM インスタンスごとに独立しているため、コピー中に別のコピーを始めるなど複数操作を並行実行できる。
/// </summary>
internal static partial class FileOperationService
{
    private const uint FofAllowUndo = 0x0040;
    private const uint FofRenameOnCollision = 0x0008;
    /// <summary>削除を「ごみ箱へ」にする（FOFX_RECYCLEONDELETE）。FOF_ALLOWUNDO と併せて指定する。</summary>
    private const uint FofxRecycleOnDelete = 0x00080000;

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidIFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private const int RpcEChangedMode = unchecked((int)0x80010106);
    /// <summary>ユーザーが進捗ダイアログでキャンセルしたときに PerformOperations が返す HRESULT。</summary>
    private const int CoprocErrorCancelled = unchecked((int)0x80270000);

    /// <summary>コピーまたは移動。競合時はエクスプローラー標準のダイアログが出る。</summary>
    public static FileOperationResult CopyOrMove(IReadOnlyList<string> sources, string destDir, bool move, bool renameOnCollision = false)
        => Execute(
            FofAllowUndo | (renameOnCollision ? FofRenameOnCollision : 0),
            move ? "移動" : "コピー",
            (operation, destination) =>
            {
                foreach (var source in sources)
                {
                    var item = CreateShellItem(source);
                    var hr = move
                        ? operation.MoveItem(item, destination!, null, 0)
                        : operation.CopyItem(item, destination!, null, 0);
                    if (hr < 0)
                    {
                        return hr;
                    }
                }

                return 0;
            },
            destDir);

    /// <summary>ごみ箱へ削除（Explorer 同様 Undo 可能）。permanent = true で完全削除（システム確認あり）。</summary>
    public static FileOperationResult DeleteToRecycleBin(IReadOnlyList<string> sources, bool permanent = false)
        => Execute(
            permanent ? 0 : FofAllowUndo | FofxRecycleOnDelete,
            "削除",
            (operation, _) =>
            {
                foreach (var source in sources)
                {
                    var hr = operation.DeleteItem(CreateShellItem(source), 0);
                    if (hr < 0)
                    {
                        return hr;
                    }
                }

                return 0;
            },
            destination: null);

    /// <summary>ごみ箱を空にする（システム確認ダイアログあり）。</summary>
    public static void EmptyRecycleBin(nint hwnd)
        => SHEmptyRecycleBinW(hwnd, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial int SHEmptyRecycleBinW(nint hwnd, nint pszRootPath, uint flags);

    /// <summary>名前の変更。</summary>
    public static FileOperationResult Rename(string source, string newPath)
        => Execute(
            FofAllowUndo,
            "名前の変更",
            (operation, _) => operation.RenameItem(CreateShellItem(source), Path.GetFileName(newPath), 0),
            destination: null);

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

    /// <summary>ファイル操作の主要なエラーコードを利用者向けの日本語説明へ変換する。
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

    /// <summary>1 回のファイル操作を専用の STA スレッド上で実行する。
    /// IFileOperation はインスタンスごとに独立しているため、この呼び出し同士は並行して走ってよい
    /// （コピー中に別のコピーを始められる）。STA なのは進捗ダイアログがメッセージポンプを必要とするため。</summary>
    private static FileOperationResult Execute(
        uint flags,
        string operationName,
        Func<IFileOperation, IShellItem?, int> queue,
        string? destination)
    {
        var result = new FileOperationResult(FileOperationOutcome.Failed, 0);
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = ExecuteCore(flags, operationName, queue, destination);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = $"Kiriha-FileOperation-{operationName}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            Logger.LogException($"ファイル操作を実行できませんでした: {operationName}", error);
            return new FileOperationResult(FileOperationOutcome.Failed, Marshal.GetHRForException(error));
        }

        return result;
    }

    private static FileOperationResult ExecuteCore(
        uint flags,
        string operationName,
        Func<IFileOperation, IShellItem?, int> queue,
        string? destination)
    {
        var initializeResult = CoInitializeEx(0, CoinitApartmentThreaded);
        var shouldUninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            return Fail(operationName, initializeResult, destination);
        }

        try
        {
            var hr = CoCreateInstance(
                ClsidFileOperation,
                0,
                ClsctxInprocServer,
                IidIFileOperation,
                out var operationPointer);
            if (hr < 0 || operationPointer == 0)
            {
                return Fail(operationName, hr, destination);
            }

            IFileOperation operation;
            try
            {
                operation = (IFileOperation)ComWrappers.GetOrCreateObjectForComInstance(
                    operationPointer,
                    CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(operationPointer);
            }

            hr = operation.SetOperationFlags(flags);
            if (hr < 0)
            {
                return Fail(operationName, hr, destination);
            }

            var destinationItem = destination is null ? null : CreateShellItem(destination);

            hr = queue(operation, destinationItem);
            if (hr < 0)
            {
                return Fail(operationName, hr, destination);
            }

            hr = operation.PerformOperations();
            // ユーザーがキャンセルした場合も HRESULT が返るため、中断フラグより先に判定する。
            if (hr == CoprocErrorCancelled)
            {
                return new FileOperationResult(FileOperationOutcome.Cancelled, 0);
            }

            if (hr < 0)
            {
                return Fail(operationName, hr, destination);
            }

            return operation.GetAnyOperationsAborted(out var aborted) >= 0 && aborted
                ? new FileOperationResult(FileOperationOutcome.Cancelled, 0)
                : new FileOperationResult(FileOperationOutcome.Success, 0);
        }
        finally
        {
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static FileOperationResult Fail(string operationName, int hr, string? destination)
    {
        var code = ToErrorCode(hr);
        // StatusText は次の操作で消えるため、事後調査用にログへも必ず残す
        Logger.Log(
            $"IFileOperation 失敗: 操作={operationName}, hr=0x{hr:X8}, code={code}（{DescribeError(code)}）, to={destination}",
            LogLevel.Warning);
        return new FileOperationResult(FileOperationOutcome.Failed, code);
    }

    /// <summary>HRESULT を利用者に見せるエラーコードへ変換する。
    /// HRESULT_FROM_WIN32 形式（0x8007xxxx）なら Win32 エラー番号を取り出し、
    /// それ以外（コピーエンジン固有エラー等）は HRESULT をそのまま数値として見せる。</summary>
    private static int ToErrorCode(int hr)
        => (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hr & 0xFFFF : hr;

    private static IShellItem CreateShellItem(string path)
    {
        var hr = SHCreateItemFromParsingName(path, 0, IidIShellItem, out var pointer);
        if (hr < 0 || pointer == 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            return (IShellItem)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        in Guid interfaceId,
        out nint shellItem);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ref ShellExecuteInfo info);
}

/// <summary>Explorer 本体と同じファイル操作 COM。メソッドは vtable 順に並べる必要があるため、
/// 使わないメソッドも省略せず宣言する（使わないものは引数を nint のままにしてある）。</summary>
[GeneratedComInterface]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    [PreserveSig]
    int Advise(nint sink, out uint cookie);

    [PreserveSig]
    int Unadvise(uint cookie);

    [PreserveSig]
    int SetOperationFlags(uint flags);

    [PreserveSig]
    int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    [PreserveSig]
    int SetProgressDialog(nint popupDialog);

    [PreserveSig]
    int SetProperties(nint propertiesArray);

    [PreserveSig]
    int SetOwnerWindow(nint owner);

    [PreserveSig]
    int ApplyPropertiesToItem(IShellItem item);

    [PreserveSig]
    int ApplyPropertiesToItems(nint items);

    [PreserveSig]
    int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, nint sink);

    [PreserveSig]
    int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    [PreserveSig]
    int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, nint sink);

    [PreserveSig]
    int MoveItems(nint items, IShellItem destinationFolder);

    [PreserveSig]
    int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, nint sink);

    [PreserveSig]
    int CopyItems(nint items, IShellItem destinationFolder);

    [PreserveSig]
    int DeleteItem(IShellItem item, nint sink);

    [PreserveSig]
    int DeleteItems(nint items);

    [PreserveSig]
    int NewItem(
        IShellItem destinationFolder,
        uint fileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
        nint sink);

    [PreserveSig]
    int PerformOperations();

    [PreserveSig]
    int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
