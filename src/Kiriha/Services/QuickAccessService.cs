using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// Windows 標準のクイックアクセス（エクスプローラー左ペイン）のフォルダー一覧を
/// Shell COM（IShellItem / IEnumShellItems、Native AOT 対応の source-generated COM）で取得する。
/// </summary>
internal static partial class QuickAccessService
{
    /// <summary>クイックアクセスの Shell 仮想フォルダー CLSID。</summary>
    private const string QuickAccessParsingName = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid IidIEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const int SigdnNormalDisplay = 0;
    private const int SigdnFilesysPath = unchecked((int)0x80058000);

    /// <summary>
    /// クイックアクセスのフォルダー一覧（表示名, パス）を返す。
    /// COM 取得に失敗した場合は標準ユーザーフォルダーへフォールバックする。
    /// </summary>
    public static List<(string Name, string Path)> GetFolders()
    {
        var result = new List<(string Name, string Path)>();
        try
        {
            EnumerateQuickAccess(result, wantFolders: true);
        }
        catch
        {
            // COM 初期化不備などで失敗した場合はフォールバックに任せる
        }

        if (result.Count == 0)
        {
            AddFallbackFolders(result);
        }

        return result;
    }

    /// <summary>クイックアクセスの「最近使用したファイル」一覧（最大 10 件）。</summary>
    public static List<(string Name, string Path)> GetRecentFiles()
    {
        var result = new List<(string Name, string Path)>();
        try
        {
            EnumerateQuickAccess(result, wantFolders: false);
        }
        catch
        {
            // 取得できなければ空のまま（セクション自体を出さない）
        }

        return result.Take(10).ToList();
    }

    private static void EnumerateQuickAccess(List<(string Name, string Path)> result, bool wantFolders)
    {
        if (SHCreateItemFromParsingName(QuickAccessParsingName, 0, IidIShellItem, out var rootPtr) < 0 || rootPtr == 0)
        {
            return;
        }

        var wrappers = new StrategyBasedComWrappers();
        var root = (IShellItem)wrappers.GetOrCreateObjectForComInstance(rootPtr, CreateObjectFlags.None);
        Marshal.Release(rootPtr);

        if (root.BindToHandler(0, BhidEnumItems, IidIEnumShellItems, out var enumPtr) < 0 || enumPtr == 0)
        {
            return;
        }

        var enumItems = (IEnumShellItems)wrappers.GetOrCreateObjectForComInstance(enumPtr, CreateObjectFlags.None);
        Marshal.Release(enumPtr);

        while (enumItems.Next(1, out var itemPtr, out var fetched) == 0 && fetched == 1 && itemPtr != 0)
        {
            var item = (IShellItem)wrappers.GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.None);
            Marshal.Release(itemPtr);

            // クイックアクセスの列挙にはフォルダーと「最近使用したファイル」が混ざる
            var path = GetDisplayName(item, SigdnFilesysPath);
            if (path is null || (wantFolders ? !Directory.Exists(path) : !File.Exists(path)))
            {
                continue;
            }

            if (result.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = GetDisplayName(item, SigdnNormalDisplay) ?? Path.GetFileName(path);
            result.Add((name, path));
        }
    }

    private static string? GetDisplayName(IShellItem item, int sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) < 0 || ptr == 0)
        {
            return null;
        }

        var value = Marshal.PtrToStringUni(ptr);
        Marshal.FreeCoTaskMem(ptr);
        return value;
    }

    private static void AddFallbackFolders(List<(string Name, string Path)> result)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (string Name, string Path)[]
        {
            ("デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("ダウンロード", Path.Combine(profile, "Downloads")),
            ("ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("ピクチャ", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("ミュージック", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("ビデオ", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };

        foreach (var (name, path) in candidates)
        {
            if (Directory.Exists(path))
            {
                result.Add((name, path));
            }
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, out nint ppv);
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetParent(out nint ppsi);

    [PreserveSig]
    int GetDisplayName(int sigdnName, out nint ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(nint psi, uint hint, out int piOrder);
}

[GeneratedComInterface]
[Guid("70629033-e363-4a28-a567-0db78006e6d7")]
internal partial interface IEnumShellItems
{
    [PreserveSig]
    int Next(uint celt, out nint rgelt, out uint pceltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out nint ppenum);
}
