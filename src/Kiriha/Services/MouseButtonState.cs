using System.Runtime.InteropServices;

namespace Kiriha.Services;

/// <summary>
/// 物理的なマウスボタンの押下状態。ドラッグ＆ドロップの受け側で「右ボタンドラッグかどうか」を
/// 判定するために使う。Avalonia の DragEventArgs はキーボード修飾（KeyModifiers）しか公開せず、
/// マウスボタンを取れないため、ここだけ Win32 に降りる。
///
/// GetKeyState ではなく GetAsyncKeyState を使う: 別プロセス（エクスプローラー等）が開始した
/// ドラッグでは、ドラッグ中のマウスメッセージがこちらのスレッドのキューに入らないため、
/// キュー同期型の GetKeyState はボタンが上がったままに見える。
/// </summary>
internal static partial class MouseButtonState
{
    private const int VkRButton = 0x02;

    /// <summary>右ボタンが物理的に押されているか。</summary>
    public static bool IsRightButtonDown => (GetAsyncKeyState(VkRButton) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}
