using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kiriha.Services;

/// <summary>Windows Shell のサムネイルハンドラーから動画・RAW画像などのプレビュー画像を取得する。</summary>
internal static partial class ShellThumbnailService
{
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfThumbnailOnly = 0x00000008;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly Guid IidIShellItemImageFactory =
        new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>
    /// Shell が生成したサムネイルを Avalonia の Bitmap へコピーする。
    /// サムネイルハンドラーやコーデックがない場合は null を返す。
    /// </summary>
    public static Bitmap? TryGetThumbnail(string path, int requestedSize)
        => TryGetShellImage(path, requestedSize, SiigbfThumbnailOnly, AlphaFormat.Opaque);

    /// <summary>Shell が項目に割り当てた Windows 標準アイコンを取得する。</summary>
    public static Bitmap? TryGetIcon(string path, int requestedSize)
        => TryGetShellImage(path, requestedSize, SiigbfIconOnly, AlphaFormat.Premul);

    private static Bitmap? TryGetShellImage(
        string path,
        int requestedSize,
        uint flags,
        AlphaFormat alphaFormat)
    {
        var initializeResult = CoInitializeEx(0, 0);
        var shouldUninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            return null;
        }

        try
        {
            if (SHCreateItemFromParsingName(
                    path,
                    0,
                    IidIShellItemImageFactory,
                    out var factoryPointer) < 0
                || factoryPointer == 0)
            {
                return null;
            }

            IShellItemImageFactory factory;
            try
            {
                factory = (IShellItemImageFactory)ComWrappers.GetOrCreateObjectForComInstance(
                    factoryPointer,
                    CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }

            if (factory.GetImage(
                    new NativeSize(requestedSize, requestedSize),
                    flags,
                    out var hBitmap) < 0
                || hBitmap == 0)
            {
                return null;
            }

            try
            {
                return CopyBitmap(hBitmap, alphaFormat);
            }
            finally
            {
                _ = DeleteObject(hBitmap);
            }
        }
        finally
        {
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static Bitmap? CopyBitmap(nint hBitmap, AlphaFormat alphaFormat)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<NativeBitmap>(), out var source) == 0
            || source.Width <= 0
            || source.Height == 0)
        {
            return null;
        }

        var width = source.Width;
        var height = Math.Abs(source.Height);
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            alphaFormat);

        var copied = false;
        using (var framebuffer = bitmap.Lock())
        {
            if (framebuffer.RowBytes == checked(width * 4))
            {
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = BiRgb,
                        SizeImage = checked((uint)(framebuffer.RowBytes * height)),
                    },
                };

                var deviceContext = GetDC(0);
                if (deviceContext != 0)
                {
                    try
                    {
                        copied = GetDIBits(
                            deviceContext,
                            hBitmap,
                            0,
                            (uint)height,
                            framebuffer.Address,
                            ref info,
                            DibRgbColors) == height;
                    }
                    finally
                    {
                        _ = ReleaseDC(0, deviceContext);
                    }
                }
            }
        }

        if (!copied)
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        in Guid interfaceId,
        out nint shellItem);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(nint handle, int bufferSize, out NativeBitmap bitmap);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startScan,
        uint scanLines,
        nint bits,
        ref BitmapInfo bitmapInfo,
        uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeSize
    {
        public readonly int Width;
        public readonly int Height;

        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }
}

[GeneratedComInterface]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
internal partial interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(
        ShellThumbnailService.NativeSize size,
        uint flags,
        out nint bitmap);
}
