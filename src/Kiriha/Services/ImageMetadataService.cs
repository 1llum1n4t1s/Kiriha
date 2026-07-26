using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// Windows シェルのプロパティストア（IShellItem2 / IPropertyStore）から画像の Exif・メタ情報を読み、
/// 表示用の (ラベル, 値) 一覧にして返す。値の整形は propsys の PSFormatForDisplay に委ねるため、
/// "f/2.8" や "1/125 秒"、"4032 x 3024" などのローカライズ済み表記になる。
///
/// Native AOT 対応のため source-generated COM（<see cref="IPropertyStore"/>）と
/// source-generated P/Invoke を使う。Exif を持たない形式（PNG 等）でも寸法・種類・サイズは取得できる。
/// </summary>
internal static partial class ImageMetadataService
{
    private static readonly Guid IidIPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    // プロパティの FMTID（System.* / System.Image.* / System.Photo.* / System.Photo.LensModel）
    private static readonly Guid FmtSummary = new("b725f130-47ef-101a-a5f1-02608c9eebac");
    private static readonly Guid FmtImage = new("6444048f-4c8b-11d1-8b70-080036b11a03");
    private static readonly Guid FmtPhoto = new("14b81da1-0135-4d31-96d9-6cbfc9671a99");
    private static readonly Guid FmtLens = new("e6ddcaf7-29c5-4f0a-9a68-d19412ec7090");

    /// <summary>表示するプロパティ（この順で、値があるものだけ出す）。
    /// ラベルはロケールキーで持ち、<see cref="Read"/> のたびに現在の言語へ解決する。</summary>
    private static readonly (string LabelKey, Guid FmtId, uint Pid)[] Keys =
    [
        ("Text.Exif.Type", FmtSummary, 4u),          // System.ItemTypeText
        ("Text.Exif.Dimensions", FmtImage, 13u),     // System.Image.Dimensions
        ("Text.Exif.Size", FmtSummary, 12u),         // System.Size
        ("Text.Exif.DateTaken", FmtPhoto, 36867u),   // System.Photo.DateTaken
        ("Text.Exif.DateModified", FmtSummary, 14u), // System.DateModified
        ("Text.Exif.CameraMaker", FmtPhoto, 271u),   // System.Photo.CameraManufacturer
        ("Text.Exif.Camera", FmtPhoto, 272u),        // System.Photo.CameraModel
        ("Text.Exif.Lens", FmtLens, 100u),           // System.Photo.LensModel
        ("Text.Exif.FocalLength", FmtPhoto, 37386u), // System.Photo.FocalLength
        ("Text.Exif.FNumber", FmtPhoto, 33437u),     // System.Photo.FNumber
        ("Text.Exif.ExposureTime", FmtPhoto, 33434u),// System.Photo.ExposureTime
        ("Text.Exif.Iso", FmtPhoto, 34855u),         // System.Photo.ISOSpeed
        ("Text.Exif.ExposureBias", FmtPhoto, 37380u),// System.Photo.ExposureBias
        ("Text.Exif.Flash", FmtPhoto, 37385u),       // System.Photo.Flash
    ];

    /// <summary>指定ファイルのメタ情報一覧を返す。取得できない場合は空リスト。</summary>
    public static List<(string Label, string Value)> Read(string path)
    {
        var result = new List<(string, string)>();

        // ThreadPool（未初期化なら MTA）でシェル COM を呼ぶため明示的に初期化する
        var init = CoInitializeEx(0, 0);
        var shouldUninitialize = init >= 0;
        if (init < 0 && init != RpcEChangedMode)
        {
            return result;
        }

        IPropertyStore? store = null;
        try
        {
            // GPS_NO_OPLOCK。既定ではプロパティハンドラーがファイルへ oplock を張り、その解除応答が
            // 返らないまま残ることがある。その状態で同じファイルを書き込みで開くと（ギャラリーの
            // 無劣化回転がまさにそれ）open が例外も出さずに待ち続けるため、oplock を張らせない。
            const int GpsNoOplock = 0x80;
            if (SHGetPropertyStoreFromParsingName(path, 0, GpsNoOplock, IidIPropertyStore, out var ptr) < 0 || ptr == 0)
            {
                return result;
            }

            var wrappers = new StrategyBasedComWrappers();
            store = (IPropertyStore)wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
            Marshal.Release(ptr);

            foreach (var (labelKey, fmt, pid) in Keys)
            {
                var key = new PropertyKey { FmtId = fmt, Pid = pid };
                if (store.GetValue(key, out var pv) < 0)
                {
                    continue;
                }

                try
                {
                    var text = Format(key, pv);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add((LocalizationService.Text(labelKey), text));
                    }
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
        }
        catch
        {
            // メタ情報を持たない形式・アクセス不可などは黙ってスキップ（呼び出し側は空リストで扱う）
        }
        finally
        {
            // CoUninitialize より先に COM オブジェクトを手放す
            ComRelease.Release(store);
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }

        return result;
    }

    /// <summary>PROPVARIANT を、そのプロパティの登録済み書式で表示用文字列へ整形する。</summary>
    private static string? Format(PropertyKey key, PropVariant pv)
    {
        // VT_EMPTY(0) / VT_NULL(1) は値なし
        if (pv.Vt is 0 or 1)
        {
            return null;
        }

        var buffer = Marshal.AllocCoTaskMem(512 * sizeof(char));
        try
        {
            return PSFormatForDisplay(key, pv, 0, buffer, 512) >= 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(
        string pszPath, nint pbc, int flags, in Guid riid, out nint ppv);

    [LibraryImport("propsys.dll")]
    private static partial int PSFormatForDisplay(
        in PropertyKey key, in PropVariant propvar, int pdfFlags, nint pszText, int cchText);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant pvar);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FmtId;
    public uint Pid;
}

/// <summary>PROPVARIANT（x64/ARM64 で 24 バイト）。値の解釈は propsys 側に任せるので中身は不透明に扱う。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public ulong Value1;
    public ulong Value2;
}

[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetValue(in PropertyKey key, in PropVariant pv);

    [PreserveSig]
    int Commit();
}
