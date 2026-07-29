using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// ドロップ先に .lnk ショートカットを作る（右ボタンドラッグの「ショートカットをここに作成」）。
/// エクスプローラーと同じく Windows の IShellLink / IPersistFile を使い、名前も
/// 「元の名前 - ショートカット.lnk」（重複時は「 (2)」を付ける）に揃える。
/// Native AOT 対応のため source-generated COM と source-generated P/Invoke を使う。
/// </summary>
internal static partial class ShellLinkService
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid IidIShellLinkW = new("000214f9-0000-0000-c000-000000000046");
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int EFail = unchecked((int)0x80004005);
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;

    /// <summary>指定した各パスへのショートカットを destDir に作る。1 件でも失敗したらそこで打ち切る。</summary>
    public static FileOperationResult Create(IReadOnlyList<string> targets, string destDir)
    {
        var initializeResult = CoInitializeEx(0, CoinitApartmentThreaded);
        var shouldUninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            return new FileOperationResult(FileOperationOutcome.Failed, initializeResult);
        }

        try
        {
            foreach (var target in targets)
            {
                var hr = CreateOne(target, destDir);
                if (hr < 0)
                {
                    Logger.Log($"ショートカットの作成に失敗しました: {target} -> {destDir} (0x{hr:X8})", LogLevel.Error);
                    return new FileOperationResult(FileOperationOutcome.Failed, hr);
                }
            }

            return new FileOperationResult(FileOperationOutcome.Success, 0);
        }
        catch (Exception ex)
        {
            Logger.LogException($"ショートカットの作成に失敗しました: {destDir}", ex);
            return new FileOperationResult(FileOperationOutcome.Failed, EFail);
        }
        finally
        {
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static int CreateOne(string target, string destDir)
    {
        var hr = CoCreateInstance(ClsidShellLink, 0, ClsctxInprocServer, IidIShellLinkW, out var instance);
        if (hr < 0 || instance == 0)
        {
            return hr < 0 ? hr : EFail;
        }

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)ComWrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
            Marshal.Release(instance);
            instance = 0;

            hr = link.SetPath(target);
            if (hr < 0)
            {
                return hr;
            }

            // 作業フォルダーはエクスプローラーが作るショートカットと同じく元の場所にしておく
            if (Path.GetDirectoryName(target) is { Length: > 0 } workingDirectory)
            {
                link.SetWorkingDirectory(workingDirectory);
            }

            // 同一 RCW への QueryInterface（ComObject が IDynamicInterfaceCastable を実装している）
            var persist = (IPersistFile)link;
            return persist.Save(BuildUniqueShortcutPath(target, destDir), true);
        }
        finally
        {
            if (instance != 0)
            {
                Marshal.Release(instance);
            }

            ComRelease.Release(link);
        }
    }

    /// <summary>実際のファイル有無を見て、重複しないショートカットのフルパスを決める。</summary>
    private static string BuildUniqueShortcutPath(string target, string destDir)
        => Path.Combine(destDir, BuildShortcutFileName(target, destDir, File.Exists));

    /// <summary>
    /// エクスプローラーと同じ命名の .lnk ファイル名を作る。既存判定は <paramref name="exists"/> に委ねる
    /// （テストから実ファイルなしで検証できるようにするため）。
    /// </summary>
    internal static string BuildShortcutFileName(string target, string destDir, Func<string, bool> exists)
    {
        var baseName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (baseName.Length == 0)
        {
            // ドライブ直下（"C:\"）などファイル名を持たないパス
            baseName = target.Replace(":", "").Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (baseName.Length == 0)
        {
            baseName = "shortcut";
        }

        var stem = baseName + LocalizationService.Text("Text.Drop.ShortcutSuffix");
        var candidate = Path.Combine(destDir, stem + ".lnk");
        for (var index = 2; exists(candidate); index++)
        {
            candidate = Path.Combine(destDir, $"{stem} ({index}).lnk");
        }

        return Path.GetFileName(candidate);
    }

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
}

/// <summary>ショートカット（.lnk）の作成に使う COM。メソッドは vtable 順に並べる必要があるため、
/// 使わないメソッドも省略せず宣言する（使わないものは引数を nint のままにしてある）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214f9-0000-0000-c000-000000000046")]
internal partial interface IShellLinkW
{
    [PreserveSig]
    int GetPath(nint file, int maxLength, nint findData, uint flags);

    [PreserveSig]
    int GetIDList(out nint idList);

    [PreserveSig]
    int SetIDList(nint idList);

    [PreserveSig]
    int GetDescription(nint name, int maxLength);

    [PreserveSig]
    int SetDescription(string name);

    [PreserveSig]
    int GetWorkingDirectory(nint dir, int maxLength);

    [PreserveSig]
    int SetWorkingDirectory(string dir);

    [PreserveSig]
    int GetArguments(nint args, int maxLength);

    [PreserveSig]
    int SetArguments(string args);

    [PreserveSig]
    int GetHotkey(out ushort hotkey);

    [PreserveSig]
    int SetHotkey(ushort hotkey);

    [PreserveSig]
    int GetShowCmd(out int showCmd);

    [PreserveSig]
    int SetShowCmd(int showCmd);

    [PreserveSig]
    int GetIconLocation(nint iconPath, int maxLength, out int icon);

    [PreserveSig]
    int SetIconLocation(string iconPath, int icon);

    [PreserveSig]
    int SetRelativePath(string pathRel, uint reserved);

    [PreserveSig]
    int Resolve(nint hwnd, uint flags);

    [PreserveSig]
    int SetPath(string file);
}

/// <summary>IPersistFile（先頭 1 メソッドは IPersist 由来）。Save 以外は使わない。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-c000-000000000046")]
internal partial interface IPersistFile
{
    [PreserveSig]
    int GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load(string fileName, uint mode);

    [PreserveSig]
    int Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    [PreserveSig]
    int SaveCompleted(string fileName);

    [PreserveSig]
    int GetCurFile(out nint fileName);
}
