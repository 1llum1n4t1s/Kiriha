using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// Media Foundation の Media Engine と WIC の COM 定義。
///
/// 動画再生は「frame-server モード」で行う。Media Engine に DXGI デバイスマネージャーを渡さず、
/// <see cref="IMFMediaEngine.TransferVideoFrame"/> の転送先へ WIC ビットマップを指定すると、
/// デコード済みフレームを CPU 側のピクセルバッファとして受け取れる。ネイティブ HWND を重ねる方式
/// （MFPlay / NativeControlHost）と違って Avalonia の描画に乗るため、ギャラリーの閉じるボタン・
/// シェルコンテキストメニュー・ホイール送りといった既存のオーバーレイ操作がそのまま生き残る。
///
/// GUID と vtable の並びは Windows SDK ヘッダー（mfmediaengine.h / mfobjects.h / wincodec.h）が正本。
/// 呼ばないメソッドも slot を合わせるためだけに <c>Reserved…</c> として宣言してあるので、
/// 順番を入れ替えたり間に挿入したりしないこと（別のメソッドを呼ぶことになる）。
/// </summary>
internal static partial class MediaEngineInterop
{
    /// <summary>MF_VERSION（MF_SDK_VERSION &lt;&lt; 16 | MF_API_VERSION）。</summary>
    internal const uint MfVersion = 0x00020070;

    internal const uint MfStartupLite = 1;

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM。WIC の 32bppPBGRA と対になる出力形式。</summary>
    internal const uint DxgiFormatB8G8R8A8Unorm = 87;

    internal const uint WicBitmapCacheOnLoad = 2;

    // MF_MEDIA_ENGINE_EVENT（必要なものだけ）
    internal const uint EventError = 5;
    internal const uint EventPlay = 8;
    internal const uint EventPause = 9;
    internal const uint EventLoadedMetadata = 10;
    internal const uint EventPlaying = 13;
    internal const uint EventCanPlay = 14;
    internal const uint EventSeeked = 17;
    internal const uint EventTimeUpdate = 18;
    internal const uint EventEnded = 19;
    internal const uint EventDurationChange = 21;
    internal const uint EventFormatChange = 1000;
    internal const uint EventFirstFrameReady = 1009;

    internal static readonly Guid ClsidMfMediaEngineClassFactory =
        new("b44392da-499b-446b-a4cb-005fead0e6d5");

    internal static readonly Guid IidIMFMediaEngineClassFactory =
        new("4d645ace-26aa-4688-9be1-df3516990b93");

    internal static readonly Guid ClsidWicImagingFactory =
        new("cacaf262-9370-4615-a13b-9f5539da4c0a");

    internal static readonly Guid IidIWicImagingFactory =
        new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");

    internal static readonly Guid PixelFormat32bppPBGRA =
        new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    internal static readonly Guid AttributeMediaEngineCallback =
        new("c60381b8-83a4-41f8-a3d0-de05076849a9");

    internal static readonly Guid AttributeVideoOutputFormat =
        new("5066893c-8cf9-42bc-8b8a-472212e52726");

    /// <summary>Shutdown を同期実行させる。動画を送るたびに engine を作り直すため、
    /// 前の engine の解放が終わる前に次を作らないようにする。</summary>
    internal static readonly Guid AttributeSynchronousClose =
        new("c3c2e12f-7e0e-4e43-b91c-dc992ccdfa5e");

    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint flags);

    // MFShutdown は意図的に宣言しない。動画を送るたびにプラットフォームを停止・再起動すると
    // 直後に作った Media Engine が「音は出るのに映像が出ない」状態になる（VideoPlaybackSession 参照）。

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

/// <summary>MFCreateAttributes で作る属性ストア（Media Engine の生成パラメーター渡しに使う）。</summary>
[GeneratedComInterface]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal partial interface IMFAttributes
{
    [PreserveSig] int ReservedGetItem(nint key, nint value);
    [PreserveSig] int ReservedGetItemType(nint key, nint type);
    [PreserveSig] int ReservedCompareItem(nint key, nint value, nint result);
    [PreserveSig] int ReservedCompare(nint theirs, uint matchType, nint result);
    [PreserveSig] int ReservedGetUINT32(nint key, nint value);
    [PreserveSig] int ReservedGetUINT64(nint key, nint value);
    [PreserveSig] int ReservedGetDouble(nint key, nint value);
    [PreserveSig] int ReservedGetGUID(nint key, nint value);
    [PreserveSig] int ReservedGetStringLength(nint key, nint length);
    [PreserveSig] int ReservedGetString(nint key, nint value, uint size, nint length);
    [PreserveSig] int ReservedGetAllocatedString(nint key, nint value, nint length);
    [PreserveSig] int ReservedGetBlobSize(nint key, nint size);
    [PreserveSig] int ReservedGetBlob(nint key, nint buffer, uint size, nint written);
    [PreserveSig] int ReservedGetAllocatedBlob(nint key, nint buffer, nint size);
    [PreserveSig] int ReservedGetUnknown(nint key, in Guid interfaceId, out nint value);
    [PreserveSig] int ReservedSetItem(nint key, nint value);
    [PreserveSig] int ReservedDeleteItem(nint key);
    [PreserveSig] int ReservedDeleteAllItems();

    [PreserveSig] int SetUINT32(in Guid key, uint value);

    [PreserveSig] int ReservedSetUINT64(in Guid key, ulong value);
    [PreserveSig] int ReservedSetDouble(in Guid key, double value);
    [PreserveSig] int ReservedSetGUID(in Guid key, in Guid value);
    [PreserveSig] int ReservedSetString(in Guid key, nint value);
    [PreserveSig] int ReservedSetBlob(in Guid key, nint buffer, uint size);

    [PreserveSig] int SetUnknown(in Guid key, nint value);

    [PreserveSig] int ReservedLockStore();
    [PreserveSig] int ReservedUnlockStore();
    [PreserveSig] int ReservedGetCount(nint count);
    [PreserveSig] int ReservedGetItemByIndex(uint index, nint key, nint value);
    [PreserveSig] int ReservedCopyAllItems(nint destination);
}

/// <summary>Media Engine の生成ファクトリ（CLSID_MFMediaEngineClassFactory）。</summary>
[GeneratedComInterface]
[Guid("4d645ace-26aa-4688-9be1-df3516990b93")]
internal partial interface IMFMediaEngineClassFactory
{
    [PreserveSig]
    int CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngine engine);

    [PreserveSig] int ReservedCreateTimeRange(out nint timeRange);
    [PreserveSig] int ReservedCreateError(out nint error);
}

/// <summary>
/// Media Engine 本体。HTML5 の &lt;video&gt; とほぼ同じ API 体系で、多くの getter は HRESULT ではなく
/// 値そのものを返すため、すべて <see cref="PreserveSigAttribute"/> で宣言している。
/// </summary>
[GeneratedComInterface]
[Guid("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93")]
internal partial interface IMFMediaEngine
{
    [PreserveSig] int ReservedGetError(out nint error);
    [PreserveSig] int ReservedSetErrorCode(uint error);
    [PreserveSig] int ReservedSetSourceElements(nint elements);

    /// <summary>再生するメディアの URL（BSTR）。ローカルファイルはパスをそのまま渡せる。</summary>
    [PreserveSig] int SetSource(nint url);

    [PreserveSig] int ReservedGetCurrentSource(out nint url);
    [PreserveSig] ushort ReservedGetNetworkState();
    [PreserveSig] int ReservedGetPreload();
    [PreserveSig] int ReservedSetPreload(int preload);
    [PreserveSig] int ReservedGetBuffered(out nint ranges);

    [PreserveSig] int Load();

    [PreserveSig] int ReservedCanPlayType(nint type, out int answer);

    /// <summary>HTMLMediaElement.readyState 相当（0=HAVE_NOTHING …… 4=HAVE_ENOUGH_DATA）。</summary>
    [PreserveSig] ushort GetReadyState();

    [PreserveSig] int ReservedIsSeeking();

    [PreserveSig] double GetCurrentTime();

    [PreserveSig] int SetCurrentTime(double seconds);

    [PreserveSig] double ReservedGetStartTime();

    [PreserveSig] double GetDuration();

    /// <summary>一時停止中なら非 0。</summary>
    [PreserveSig] int IsPaused();

    [PreserveSig] double ReservedGetDefaultPlaybackRate();
    [PreserveSig] int ReservedSetDefaultPlaybackRate(double rate);

    [PreserveSig] double GetPlaybackRate();

    [PreserveSig] int SetPlaybackRate(double rate);

    [PreserveSig] int ReservedGetPlayed(out nint ranges);
    [PreserveSig] int ReservedGetSeekable(out nint ranges);

    /// <summary>再生が末尾に達していれば非 0。</summary>
    [PreserveSig] int IsEnded();

    [PreserveSig] int ReservedGetAutoPlay();
    [PreserveSig] int ReservedSetAutoPlay(int autoPlay);
    [PreserveSig] int ReservedGetLoop();

    [PreserveSig] int SetLoop(int loop);

    [PreserveSig] int Play();

    [PreserveSig] int Pause();

    [PreserveSig] int ReservedGetMuted();

    [PreserveSig] int SetMuted(int muted);

    [PreserveSig] double ReservedGetVolume();

    [PreserveSig] int SetVolume(double volume);

    /// <summary>映像トラックを持っていれば非 0。</summary>
    [PreserveSig] int HasVideo();

    [PreserveSig] int HasAudio();

    /// <summary>映像の実サイズ。フレーム転送先ビットマップの縦横比を決めるのに使う。</summary>
    [PreserveSig] int GetNativeVideoSize(out uint width, out uint height);

    [PreserveSig] int ReservedGetVideoAspectRatio(out uint x, out uint y);

    [PreserveSig] int Shutdown();

    /// <summary>現在のフレームを転送先サーフェス（ここでは IWICBitmap）へコピーする。</summary>
    [PreserveSig]
    int TransferVideoFrame(
        nint destination,
        nint sourceRect,
        in MediaEngineInterop.NativeRect destinationRect,
        nint borderColor);

    /// <summary>新しいフレームが用意できていれば S_OK、まだなら S_FALSE を返す。</summary>
    [PreserveSig] int OnVideoStreamTick(out long presentationTime);
}

/// <summary>Media Engine からの状態通知を受け取るコールバック（こちら側で実装して engine へ渡す）。</summary>
[GeneratedComInterface]
[Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5")]
internal partial interface IMFMediaEngineNotify
{
    [PreserveSig]
    int EventNotify(uint mediaEvent, nuint param1, uint param2);
}

/// <summary>WIC のファクトリ。ここでは転送先ビットマップの生成にだけ使う。</summary>
[GeneratedComInterface]
[Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
internal partial interface IWICImagingFactory
{
    [PreserveSig] int ReservedCreateDecoderFromFilename(nint a, nint b, uint c, uint d, out nint e);
    [PreserveSig] int ReservedCreateDecoderFromStream(nint a, nint b, uint c, out nint d);
    [PreserveSig] int ReservedCreateDecoderFromFileHandle(nuint a, nint b, uint c, out nint d);
    [PreserveSig] int ReservedCreateComponentInfo(in Guid a, out nint b);
    [PreserveSig] int ReservedCreateDecoder(in Guid a, nint b, out nint c);
    [PreserveSig] int ReservedCreateEncoder(in Guid a, nint b, out nint c);
    [PreserveSig] int ReservedCreatePalette(out nint a);
    [PreserveSig] int ReservedCreateFormatConverter(out nint a);
    [PreserveSig] int ReservedCreateBitmapScaler(out nint a);
    [PreserveSig] int ReservedCreateBitmapClipper(out nint a);
    [PreserveSig] int ReservedCreateBitmapFlipRotator(out nint a);
    [PreserveSig] int ReservedCreateStream(out nint a);
    [PreserveSig] int ReservedCreateColorContext(out nint a);
    [PreserveSig] int ReservedCreateColorTransformer(out nint a);

    [PreserveSig]
    int CreateBitmap(uint width, uint height, in Guid pixelFormat, uint cacheOption, out IWICBitmap bitmap);
}

/// <summary>フレーム転送先のメモリビットマップ。</summary>
[GeneratedComInterface]
[Guid("00000121-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICBitmap
{
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int ReservedGetPixelFormat(out Guid format);
    [PreserveSig] int ReservedGetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int ReservedCopyPalette(nint palette);
    /// <summary>ビットマップ全体（rect = 0）を指定バッファへコピーする。
    /// Lock を使わないのは、返ってくる RCW を解放する手段が無く、ロックが残ると
    /// Media Foundation が次のフレームを書き込めなくなるため（CopyToBackBuffer 参照）。</summary>
    [PreserveSig] int CopyPixels(nint rect, uint stride, uint bufferSize, nint buffer);

    /// <summary>使わない。IWICBitmapLock の RCW を解放する手段が無く、ロックが残ると
    /// Media Foundation がフレームを書き込めなくなる（CopyPixels を使うこと）。</summary>
    [PreserveSig] int ReservedLock(nint rect, uint flags, out nint bitmapLock);

    [PreserveSig] int ReservedSetPalette(nint palette);
    [PreserveSig] int ReservedSetResolution(double dpiX, double dpiY);
}
