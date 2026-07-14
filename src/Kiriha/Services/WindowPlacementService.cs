using System.Runtime.InteropServices;

namespace Kiriha.Services;

/// <summary>ウィンドウを表示する前でも利用できる、Windows のモニター配置判定。</summary>
internal static partial class WindowPlacementService
{
    private const uint MonitorDefaultToNull = 0;
    private const int PositionProbeSize = 40;

    public static bool IsPositionOnAnyMonitor(int x, int y)
    {
        var probe = new NativeRect
        {
            Left = x,
            Top = y,
            Right = x + PositionProbeSize,
            Bottom = y + PositionProbeSize,
        };
        return MonitorFromRect(in probe, MonitorDefaultToNull) != 0;
    }

    public static bool TryResolveSavedMonitor(
        int x,
        int y,
        int width,
        int height,
        out MonitorWorkingArea workingArea)
    {
        workingArea = default;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var saved = new NativeRect
        {
            Left = x,
            Top = y,
            Right = x + width,
            Bottom = y + height,
        };
        var monitor = MonitorFromRect(in saved, MonitorDefaultToNull);
        if (monitor == 0)
        {
            return false;
        }

        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        var actualWidth = info.Work.Right - info.Work.Left;
        var actualHeight = info.Work.Bottom - info.Work.Top;
        var sameBounds = info.Work.Left == x
                         && info.Work.Top == y
                         && actualWidth == width
                         && actualHeight == height;
        var sameOrigin = info.Work.Left == x && info.Work.Top == y;
        var sameSize = actualWidth == width && actualHeight == height;
        if (!sameBounds && !sameOrigin && !sameSize)
        {
            return false;
        }

        workingArea = new MonitorWorkingArea(info.Work.Left, info.Work.Top, actualWidth, actualHeight);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(in NativeRect rect, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}

internal readonly record struct MonitorWorkingArea(int X, int Y, int Width, int Height);
