using System.Runtime.InteropServices;

namespace Kiriha.Services;

/// <summary>
/// 「エクスプローラーが画面に出しているのと同じ文字列」を Windows から直接もらうサービス。
///
/// ドライブの表示名（ボリュームラベル無しの "ローカル ディスク (C:)"、Google ドライブのような
/// 仮想ドライブの独自名）と、容量の単位付き表記（"1.86 TB" / "180 GB"）はどちらも Windows が
/// 各言語ぶん持っている。自前で組み立てるとドライブ種別の訳語を 17 言語ぶん抱え込むうえ、
/// 有効数字の丸め方（180 GB / 0.98 TB）まで再現する必要が出るので、素直に本家の API へ委ねる。
/// </summary>
internal static partial class ShellDisplayService
{
    /// <summary>SHGFI_DISPLAYNAME。</summary>
    private const uint ShgfiDisplayName = 0x00000200;

    /// <summary>SHFILEINFOW の szDisplayName までのオフセット（hIcon + iIcon + dwAttributes）。</summary>
    private static readonly int DisplayNameOffset = nint.Size + 4 + 4;

    /// <summary>SHFILEINFOW 全体の大きさ（szDisplayName[260] + szTypeName[80]）。</summary>
    private static readonly int FileInfoSize = DisplayNameOffset + (260 * 2) + (80 * 2);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfo(string path, uint fileAttributes, nint fileInfo, uint fileInfoSize, uint flags);

    [LibraryImport("shlwapi.dll", EntryPoint = "StrFormatByteSizeW")]
    private static partial nint StrFormatByteSize(long size, nint buffer, uint bufferChars);

    /// <summary>
    /// エクスプローラーと同じ表示名を返す（例: "ローカル ディスク (C:)" / "Windows (C:)"）。
    /// 取得できなければ null を返すので、呼び出し側は従来の組み立てへ落とす。
    /// </summary>
    internal static string? TryGetDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(FileInfoSize);
        try
        {
            if (SHGetFileInfo(path, 0, buffer, (uint)FileInfoSize, ShgfiDisplayName) == 0)
            {
                return null;
            }

            var name = Marshal.PtrToStringUni(buffer + DisplayNameOffset);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            Logger.LogException($"表示名の取得に失敗しました: {path}", ex);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// エクスプローラーと同じ単位付きサイズ表記を返す（例: "1.86 TB" / "180 GB" / "4.88 KB"）。
    /// 失敗したときは Kiriha 自前の <see cref="Models.FileSystemEntry.FormatSize"/> へ落とす。
    /// </summary>
    internal static string FormatByteSize(long size)
    {
        // StrFormatByteSizeW の出力は最長でも十数文字。64 文字あれば足りる。
        const int BufferChars = 64;
        var buffer = Marshal.AllocHGlobal(BufferChars * 2);
        try
        {
            if (StrFormatByteSize(size, buffer, BufferChars) != 0
                && Marshal.PtrToStringUni(buffer) is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException($"サイズ表記の整形に失敗しました: {size}", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return Models.FileSystemEntry.FormatSize(size);
    }
}
