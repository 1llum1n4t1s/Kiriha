using System.Runtime.InteropServices;

namespace Kiriha.Services;

/// <summary>
/// Win32 クリップボードの CF_HDROP + "Preferred DropEffect" を直接読み書きし、
/// エクスプローラーとの切り取り / コピー / 貼り付け完全互換を実現する。
/// </summary>
internal static partial class ClipboardFileService
{
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;

    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;

    /// <summary>この プロセスで切り取り中のパス集合（エクスプローラー同様の半透明表示用）。</summary>
    private static HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>切り取り状態の変化通知（各タブが表示を更新する）。</summary>
    public static event EventHandler? CutStateChanged;

    public static bool IsCutPath(string path) => _cutPaths.Contains(path);

    /// <summary>OS クリップボードの実状態から切り取り集合を再同期する。エクスプローラー側で別の
    /// 切り取り/コピーが行われたときに、Kiriha 側の半透明表示が古いまま残らないようにする
    /// （Kiriha がフォアグラウンドに戻ったタイミングで呼ぶ）。内容が変わったときだけ
    /// CutStateChanged を発火する（自プロセスの操作での無駄な再描画を避ける）。</summary>
    public static void SyncFromClipboard()
    {
        HashSet<string> current;
        try
        {
            var files = GetFiles(out var isCut);
            current = isCut
                ? new HashSet<string>(files, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return;
        }

        if (!current.SetEquals(_cutPaths))
        {
            _cutPaths = current;
            CutStateChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>クリップボードにファイルがあるか（貼り付けボタンの活性制御用）。</summary>
    public static bool HasFiles() => IsClipboardFormatAvailable(CfHdrop);

    /// <summary>ファイル一覧をクリップボードへ設定する（cut = true で切り取り）。</summary>
    public static bool SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0 || !OpenClipboard(0))
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            var hDrop = CreateHDrop(paths);
            if (hDrop == 0 || SetClipboardData(CfHdrop, hDrop) == 0)
            {
                return false;
            }

            var effectFormat = RegisterClipboardFormatW("Preferred DropEffect");
            var hEffect = CreateDword(cut ? DropEffectMove : DropEffectCopy);
            if (hEffect != 0)
            {
                SetClipboardData(effectFormat, hEffect);
            }

            _cutPaths = cut
                ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CutStateChanged?.Invoke(null, EventArgs.Empty);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>クリップボードからファイル一覧を取得する。isCut は切り取り由来かどうか。</summary>
    public static List<string> GetFiles(out bool isCut)
    {
        var result = new List<string>();
        isCut = false;

        if (!IsClipboardFormatAvailable(CfHdrop) || !OpenClipboard(0))
        {
            return result;
        }

        try
        {
            var hDrop = GetClipboardData(CfHdrop);
            if (hDrop == 0)
            {
                return result;
            }

            var count = DragQueryFileW(hDrop, 0xFFFFFFFF, 0, 0);
            var buffer = new char[520];
            for (uint i = 0; i < count; i++)
            {
                uint len;
                unsafe
                {
                    fixed (char* p = buffer)
                    {
                        len = DragQueryFileW(hDrop, i, (nint)p, (uint)buffer.Length);
                    }
                }

                if (len > 0)
                {
                    result.Add(new string(buffer, 0, (int)len));
                }
            }

            var effectFormat = RegisterClipboardFormatW("Preferred DropEffect");
            var hEffect = GetClipboardData(effectFormat);
            if (hEffect != 0)
            {
                var ptr = GlobalLock(hEffect);
                if (ptr != 0)
                {
                    isCut = (Marshal.ReadInt32(ptr) & DropEffectMove) != 0;
                    GlobalUnlock(hEffect);
                }
            }

            return result;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>切り取り → 貼り付け完了後にクリップボードを空にする（エクスプローラーと同じ挙動）。</summary>
    public static void Clear()
    {
        if (OpenClipboard(0))
        {
            EmptyClipboard();
            CloseClipboard();
        }

        _cutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CutStateChanged?.Invoke(null, EventArgs.Empty);
    }

    private static nint CreateHDrop(IReadOnlyList<string> paths)
    {
        // DROPFILES (20 bytes) + 各パス (NUL 終端) + 終端 NUL
        var joined = string.Join('\0', paths) + "\0\0";
        var bytes = 20 + joined.Length * 2;
        var handle = GlobalAlloc(GmemMoveable, (nuint)bytes);
        if (handle == 0)
        {
            return 0;
        }

        var ptr = GlobalLock(handle);
        if (ptr == 0)
        {
            return 0;
        }

        Marshal.WriteInt32(ptr, 0, 20);      // pFiles
        Marshal.WriteInt32(ptr, 4, 0);       // pt.x
        Marshal.WriteInt32(ptr, 8, 0);       // pt.y
        Marshal.WriteInt32(ptr, 12, 0);      // fNC
        Marshal.WriteInt32(ptr, 16, 1);      // fWide
        unsafe
        {
            fixed (char* src = joined)
            {
                Buffer.MemoryCopy(src, (void*)(ptr + 20), joined.Length * 2, joined.Length * 2);
            }
        }

        GlobalUnlock(handle);
        return handle;
    }

    private static nint CreateDword(uint value)
    {
        var handle = GlobalAlloc(GmemMoveable, 4);
        if (handle == 0)
        {
            return 0;
        }

        var ptr = GlobalLock(handle);
        if (ptr == 0)
        {
            return 0;
        }

        Marshal.WriteInt32(ptr, (int)value);
        GlobalUnlock(handle);
        return handle;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint hDrop, uint index, nint buffer, uint cch);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint handle);
}
