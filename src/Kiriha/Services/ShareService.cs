using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

// List<IStorageItem> を WinRT 境界（DataPackage.SetStorageItems の IIterable<IStorageItem>）へ渡すための
// CCW vtable を CsWinRT ソースジェネレーターに明示生成させる。
[assembly: GeneratedWinRTExposedExternalType(typeof(List<IStorageItem>))]

namespace Kiriha.Services;

/// <summary>
/// Windows 標準の共有シートを開く（Explorer の「共有」と同じ体験）。
/// Win32 ウィンドウからは DataTransferManager を直接使えないため、
/// 公式の IDataTransferManagerInterop（RoGetActivationFactory 経由）で HWND に関連付ける。
/// </summary>
internal static partial class ShareService
{
    private static readonly Guid IidIDataTransferManager = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");

    /// <summary>DataRequested 発火まで共有対象を保持する（共有シートはウィンドウ単位）。</summary>
    private static IReadOnlyList<string>? _pendingPaths;

    /// <summary>ウィンドウに紐付いた DataTransferManager。イベント購読を 1 回に保つためキャッシュする。</summary>
    private static DataTransferManager? _manager;
    private static nint _managerHwnd;

    public static bool Show(nint hwnd, IReadOnlyList<string> paths)
    {
        if (hwnd == 0 || paths.Count == 0)
        {
            return false;
        }

        try
        {
            var interop = GetInterop();
            if (_manager is null || _managerHwnd != hwnd)
            {
                Marshal.ThrowExceptionForHR(interop.GetForWindow(hwnd, IidIDataTransferManager, out var abi));
                try
                {
                    _manager = MarshalInterface<DataTransferManager>.FromAbi(abi);
                }
                finally
                {
                    Marshal.Release(abi);
                }

                _managerHwnd = hwnd;
                _manager.DataRequested += OnDataRequested;
            }

            _pendingPaths = paths;
            Marshal.ThrowExceptionForHR(interop.ShowShareUIForWindow(hwnd));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogException("共有シートを開けませんでした", ex);
            return false;
        }
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var paths = _pendingPaths;
        if (paths is null || paths.Count == 0)
        {
            args.Request.FailWithDisplayText("共有する項目がありません");
            return;
        }

        // StorageFile の取得は非同期のため、Deferral で完了を明示するまで共有シートを待たせる
        var deferral = args.Request.GetDeferral();
        try
        {
            var items = new List<IStorageItem>(paths.Count);
            foreach (var path in paths)
            {
                items.Add(await StorageFile.GetFileFromPathAsync(path));
            }

            args.Request.Data.Properties.Title = items.Count == 1
                ? items[0].Name
                : $"{items.Count} 個のファイル";
            args.Request.Data.SetStorageItems(items, readOnly: true);
        }
        catch (Exception ex)
        {
            Logger.LogException("共有データの準備に失敗しました", ex);
            args.Request.FailWithDisplayText("共有データを準備できませんでした");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static IDataTransferManagerInterop GetInterop()
    {
        const string className = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var hstring));
        try
        {
            var iid = new Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in iid, out var factoryPtr));
            var wrappers = new StrategyBasedComWrappers();
            var interop = (IDataTransferManagerInterop)wrappers.GetOrCreateObjectForComInstance(factoryPtr, CreateObjectFlags.None);
            Marshal.Release(factoryPtr);
            return interop;
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
}

[GeneratedComInterface]
[Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
internal partial interface IDataTransferManagerInterop
{
    [PreserveSig]
    int GetForWindow(nint appWindow, in Guid riid, out nint dataTransferManager);

    [PreserveSig]
    int ShowShareUIForWindow(nint appWindow);
}
