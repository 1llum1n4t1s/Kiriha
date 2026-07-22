using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Kiriha.Views;

/// <summary>
/// アプリ外へ出てもカーソルへ追従する、ファイルD&D専用の非アクティブウィンドウ。
/// ドロップ判定を邪魔しないよう、Windowsではマウス透過の拡張スタイルを付与する。
/// </summary>
public partial class DragPreviewWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;

    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private string _operationText = "ドラッグ中";
    private string _resolvedOperationText = "ドラッグ中";

    public DragPreviewWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Opened += OnOpened;
        Closed += (_, _) => _followTimer.Stop();
        _followTimer.Tick += (_, _) => FollowCursor();
    }

    public DragPreviewWindow(IReadOnlyList<(string Path, bool IsDirectory)> entries) : this()
    {
        var first = entries[0];
        var firstName = Path.GetFileName(first.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        DragPreviewIcon.Text = entries.All(entry => entry.IsDirectory) ? "📁" : entries.Count > 1 ? "📦" : "📄";
        DragPreviewTitle.Text = string.IsNullOrWhiteSpace(firstName) ? first.Path : firstName;
        DragPreviewDetail.Text = entries.Count == 1
            ? first.IsDirectory ? "フォルダー" : "ファイル"
            : $"{entries.Count} 個の項目";
        BackCard1.IsVisible = entries.Count > 1;
        BackCard2.IsVisible = entries.Count > 2;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (TryGetPlatformHandle() is { } handle)
        {
            var current = GetWindowLongPtrW(handle.Handle, GwlExStyle).ToInt64();
            SetWindowLongPtrW(handle.Handle, GwlExStyle,
                new nint(current | WsExTransparent | WsExToolWindow | WsExNoActivate));
        }

        FollowCursor();
        _followTimer.Start();
    }

    private void FollowCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var point = new PixelPoint(cursor.X, cursor.Y);
        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        var workArea = screen?.WorkingArea ?? new PixelRect(point.X, point.Y, 1920, 1080);
        var width = (int)Math.Ceiling(Width * scaling);
        var height = (int)Math.Ceiling(Height * scaling);

        var x = cursor.X + 22;
        var y = cursor.Y + 24;
        if (x + width > workArea.Right)
        {
            x = cursor.X - width - 22;
        }

        if (y + height > workArea.Bottom)
        {
            y = cursor.Y - height - 24;
        }

        Position = new PixelPoint(Math.Max(workArea.X, x), Math.Max(workArea.Y, y));

        var operation = IsKeyDown(VkControl)
            ? "コピー"
            : IsKeyDown(VkShift)
                ? "移動"
                : _resolvedOperationText;
        if (_operationText != operation)
        {
            _operationText = operation;
            DragPreviewOperation.Text = operation;
        }
    }

    public void SetDropOperation(DragDropEffects effect, bool isBookmark, bool isTabOpen = false)
    {
        _resolvedOperationText = isTabOpen
            ? "タブで開く"
            : isBookmark
                ? "追加"
                : effect.HasFlag(DragDropEffects.Copy)
                    ? "コピー"
                    : effect.HasFlag(DragDropEffects.Move)
                        ? "移動"
                        : "ドラッグ中";
    }

    private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(nint hWnd, int index, nint newLong);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
