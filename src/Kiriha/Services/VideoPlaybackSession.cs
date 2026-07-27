using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Kiriha.Services;

/// <summary>Media Engine の読み込み状態（UI のボタン活性やメッセージ切り替えに使う）。</summary>
internal enum VideoPlaybackState
{
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// 1 本の動画を frame-server モードで再生するセッション。
///
/// デコードは Windows にインストール済みのコーデック（＝エクスプローラーがサムネイルを出せる形式）に
/// 準ずる。フレームは <see cref="TryRenderNextFrame"/> が呼ばれたときにだけ WIC 経由で取り出して
/// <see cref="CurrentFrame"/> へ差し替えるので、描画の駆動は呼び出し側のタイマーが握る。
///
/// COM の生成・破棄と <see cref="TryRenderNextFrame"/> は UI スレッドから呼ぶ前提。Media Engine からの
/// 通知は MF のワーカースレッドで来るため、UI スレッドへ Post してから状態を触る。
/// </summary>
internal sealed partial class VideoPlaybackSession : IDisposable
{
    /// <summary>ギャラリーで再生対象にする拡張子（サムネイル生成対象と同じ範囲）。</summary>
    private static readonly string[] PlayableExtensions =
        [".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".mts", ".m2ts"];

    /// <summary>フレーム転送先の最大幅。
    ///
    /// ここを下げるとソフトウェアコピーは軽くなるが、映像の解像度をそのまま捨てることになる。
    /// 以前の 1280 では、フル HD の動画を 1280 へ縮めてから画面幅（高 DPI では 2560 前後）へ
    /// 拡大し直していて、等倍で出せるはずの素材が明確にぼけていた。
    /// 一般的な高 DPI ノートの物理解像度に合わせて 2560 とし、フル HD は等倍で通す。
    /// これより大きい 4K 素材だけが縮小の対象になる。</summary>
    private const int MaxFrameWidth = 2560;

    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static readonly Lock MediaFoundationLock = new();

    /// <summary>MFStartup 済みか。プロセスで 1 回だけ起こし、MFShutdown は呼ばない。
    ///
    /// 以前はセッションごとに参照カウントして 0 で MFShutdown していたが、動画を送るたびに
    /// プラットフォームの停止と再起動が走り、その直後に作った Media Engine が
    /// 「音は出るのに OnVideoStreamTick が永久に S_FALSE（＝映像が真っ黒）」になることがあった。
    /// MF の停止は engine の非同期解放と競合するため、起動しっぱなしにして揺らぎを断つ。</summary>
    private static bool s_mediaFoundationStarted;

    private readonly EngineNotify _notify;
    private readonly nint _notifyComPointer;
    private readonly IMFMediaEngine _engine;
    private readonly IWICImagingFactory _wicFactory;

    private IWICBitmap? _wicFrame;
    /// <summary>_wicFrame のネイティブポインタ。TransferVideoFrame へ毎フレーム渡すため保持する。</summary>
    private nint _wicFramePointer;
    private VideoFrame? _frontBuffer;
    private VideoFrame? _backBuffer;

    private PixelSize _frameSize;
    private bool _disposed;

    /// <summary>次の 1 枚は OnVideoStreamTick の結果によらず取り出す。
    /// 一時停止中はシークしても engine が「新しいフレームがある」と言ってこないため、
    /// 読み込み完了とシーク完了のタイミングだけは強制的に取り直す（古い絵が残るのを防ぐ）。</summary>
    private bool _forceNextFrame;

    /// <summary>フレーム取得失敗のログを 1 回だけに絞るためのフラグ。</summary>
    private bool _transferFailureLogged;

    /// <summary>シーク要求を engine へ投げっぱなしにしているか（EventSeeked で下ろす）。</summary>
    private bool _seekInFlight;

    /// <summary>実行中のシークが終わったら投げ直す位置。<see cref="_hasPendingSeek"/> が true のときだけ有効。</summary>
    private double _pendingSeek;
    private bool _hasPendingSeek;

    /// <summary>実行中のシークを投げた時刻（Stopwatch のタイムスタンプ）。取りこぼし対策の番人に使う。</summary>
    private long _seekIssuedAt;

    /// <summary>直近に要求したシーク位置。完了までの間はこれを <see cref="Position"/> として見せる。</summary>
    private double _seekTarget;

    /// <summary>EventSeeked が来ないコーデックに備えた打ち切り時間。これを過ぎたら次のシークを許す。</summary>
    private static readonly long SeekWatchdogTicks = Stopwatch.Frequency * 3;

    /// <summary>1 枚も絵が出ないまま経過したタイマー回数。原因調査用のログを 1 回だけ出すのに使う。</summary>
    private int _stallTicks;
    private bool _stallLogged;
    private int _lastTickResult;
    private long _framesRendered;

    /// <summary>絵が出ないまま一定時間が過ぎたら、engine 側の状態をまとめて 1 回だけ残す。</summary>
    private void ReportStall(string reason)
    {
        if (_stallLogged || ++_stallTicks < 120)
        {
            return;
        }

        _stallLogged = true;
        _engine.GetNativeVideoSize(out var width, out var height);
        Logger.Log(
            $"動画の映像が出ません（{reason}）: hasVideo={_engine.HasVideo()}, hasAudio={_engine.HasAudio()}, "
            + $"native={width}x{height}, readyState={_engine.GetReadyState()}, buffer={_frameSize.Width}x{_frameSize.Height}, "
            + $"tick=0x{_lastTickResult:X8}, rendered={_framesRendered}, paused={_engine.IsPaused()}",
            LogLevel.Warning);
    }

    private VideoPlaybackSession(IMFMediaEngine engine, IWICImagingFactory wicFactory, EngineNotify notify, nint notifyComPointer)
    {
        _engine = engine;
        _wicFactory = wicFactory;
        _notify = notify;
        _notifyComPointer = notifyComPointer;
        _notify.Owner = this;
    }

    /// <summary>再生対象として扱う拡張子か（先頭のドット込み、小文字）。</summary>
    public static bool IsPlayable(string extension)
        => PlayableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>読み込み状態。</summary>
    public VideoPlaybackState State { get; private set; } = VideoPlaybackState.Loading;

    /// <summary>直近に取り出したフレーム。まだ 1 枚も来ていなければ null。</summary>
    public VideoFrame? CurrentFrame { get; private set; }

    /// <summary>尺（秒）。取得前や不明な場合は 0。</summary>
    public double Duration { get; private set; }

    /// <summary>状態が変わった（読み込み完了 / 再生・停止 / 尺確定）ときに UI スレッドで発火する。</summary>
    public event EventHandler? Changed;

    /// <summary>末尾まで再生し終えたときに UI スレッドで発火する（ループ時は発火しない）。</summary>
    public event EventHandler? Ended;

    /// <summary>
    /// 指定ファイルの再生セッションを作る。コーデックが無い等で構築できなければ null。
    /// </summary>
    public static VideoPlaybackSession? TryCreate(string path)
    {
        if (!EnsureMediaFoundation())
        {
            return null;
        }

        nint attributesPointer = 0;
        nint notifyComPointer = 0;
        IWICImagingFactory? wicFactory = null;
        IMFMediaEngineClassFactory? factory = null;
        IMFAttributes? attributes = null;
        // セッションを作れたら wicFactory の解放責任はセッション側（Dispose）へ移る
        var wicFactoryOwnedBySession = false;
        try
        {
            if (MediaEngineInterop.CoCreateInstance(
                    MediaEngineInterop.ClsidWicImagingFactory,
                    0,
                    1 /* CLSCTX_INPROC_SERVER */,
                    MediaEngineInterop.IidIWicImagingFactory,
                    out var wicPointer) < 0 || wicPointer == 0)
            {
                return null;
            }

            try
            {
                wicFactory = (IWICImagingFactory)ComWrappers.GetOrCreateObjectForComInstance(wicPointer, CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(wicPointer);
            }

            if (MediaEngineInterop.CoCreateInstance(
                    MediaEngineInterop.ClsidMfMediaEngineClassFactory,
                    0,
                    1,
                    MediaEngineInterop.IidIMFMediaEngineClassFactory,
                    out var factoryPointer) < 0 || factoryPointer == 0)
            {
                return null;
            }

            try
            {
                factory = (IMFMediaEngineClassFactory)ComWrappers.GetOrCreateObjectForComInstance(factoryPointer, CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }

            if (MediaEngineInterop.MFCreateAttributes(out attributesPointer, 3) < 0 || attributesPointer == 0)
            {
                return null;
            }

            attributes = (IMFAttributes)ComWrappers.GetOrCreateObjectForComInstance(attributesPointer, CreateObjectFlags.None);

            var notify = new EngineNotify();
            notifyComPointer = ComWrappers.GetOrCreateComInterfaceForObject(notify, CreateComInterfaceFlags.None);

            if (attributes.SetUnknown(MediaEngineInterop.AttributeMediaEngineCallback, notifyComPointer) < 0
                || attributes.SetUINT32(MediaEngineInterop.AttributeVideoOutputFormat, MediaEngineInterop.DxgiFormatB8G8R8A8Unorm) < 0
                // 動画を送るたびに engine を作り直すので、前の解放が終わってから次を作れるようにする
                || attributes.SetUINT32(MediaEngineInterop.AttributeSynchronousClose, 1) < 0)
            {
                Marshal.Release(notifyComPointer);
                return null;
            }

            if (factory.CreateInstance(0, attributes, out var engine) < 0)
            {
                Marshal.Release(notifyComPointer);
                return null;
            }

            var session = new VideoPlaybackSession(engine, wicFactory, notify, notifyComPointer);
            wicFactoryOwnedBySession = true;
            if (!session.Open(path))
            {
                session.Dispose();
                return null;
            }

            return session;
        }
        catch (Exception ex)
        {
            Logger.LogException($"動画再生セッションを作成できませんでした: {path}", ex);
            if (notifyComPointer != 0)
            {
                Marshal.Release(notifyComPointer);
            }
            return null;
        }
        finally
        {
            // engine の生成にしか使わないので必ず手放す
            ComRelease.Release(attributes);
            ComRelease.Release(factory);
            if (!wicFactoryOwnedBySession)
            {
                ComRelease.Release(wicFactory);
            }

            if (attributesPointer != 0)
            {
                Marshal.Release(attributesPointer);
            }
        }
    }

    /// <summary>
    /// このセッションで別のファイルを開き直す。
    ///
    /// ファイルを送るたびに engine を作り直すと、直前の engine の解放と新しい engine の初期化が競合し、
    /// 「音は出るのに映像が真っ黒のまま」になることがあった（同じファイルでも、単独で開けば必ず映る）。
    /// engine は 1 本を使い回し、ソースだけ差し替える。
    /// </summary>
    public bool Open(string path)
    {
        if (_disposed)
        {
            return false;
        }

        // 前のファイルの状態を落とす（転送先は新しい映像サイズが分かってから作り直す）
        DisposeBuffers();
        State = VideoPlaybackState.Loading;
        Duration = 0;
        _forceNextFrame = false;
        _transferFailureLogged = false;
        _stallLogged = false;
        _stallTicks = 0;
        _framesRendered = 0;
        ResetSeekState();

        var url = Marshal.StringToBSTR(path);
        try
        {
            return _engine.SetSource(url) >= 0;
        }
        finally
        {
            Marshal.FreeBSTR(url);
        }
    }

    /// <summary>再生を開始する。</summary>
    public void Play()
    {
        if (_disposed) return;
        _ = _engine.Play();
    }

    /// <summary>一時停止する。</summary>
    public void Pause()
    {
        if (_disposed) return;
        _ = _engine.Pause();
    }

    /// <summary>再生中かどうか。</summary>
    public bool IsPlaying => !_disposed && _engine.IsPaused() == 0 && _engine.IsEnded() == 0;

    /// <summary>
    /// 現在位置（秒）。シーク中は engine の現在位置ではなく要求位置を返す。
    ///
    /// engine はシークが終わるまで移動前の時刻を返し続けるため、そのまま公開すると
    /// スライダーがつまみを離すまで元の位置へ引き戻され続ける（＝操作を受け付けていないように見える）。
    /// </summary>
    public double Position
        => _disposed ? 0 : (IsSeekOutstanding() ? _seekTarget : Sanitize(_engine.GetCurrentTime()));

    /// <summary>要求したシークがまだ終わっていないか。完了通知が来ないまま時間切れになった分は下ろす。</summary>
    private bool IsSeekOutstanding()
    {
        if (_seekInFlight && Stopwatch.GetTimestamp() - _seekIssuedAt >= SeekWatchdogTicks)
        {
            _seekInFlight = false;
            if (_hasPendingSeek)
            {
                IssueSeek(_pendingSeek);
            }
        }

        return _seekInFlight || _hasPendingSeek;
    }

    /// <summary>
    /// 再生位置を移動する。
    ///
    /// スライダーのドラッグは 1 回の操作で数十回この呼び出しを起こす。engine の SetCurrentTime は
    /// 要求ごとに正確なシーク（キーフレームまで戻ってデコードし直す）を行い、しかも実行中の要求を
    /// 取り消さずに順番に処理するため、素通しすると要求が行列を作り、つまみを離してから実際に
    /// その位置の絵が出るまで十数秒待たされていた。実行中は 1 本だけに絞り、途中の要求は
    /// 最後の 1 つへ畳んで完了時（EventSeeked）に投げ直す。
    /// </summary>
    public void Seek(double seconds)
    {
        if (_disposed) return;
        var clamped = Duration > 0 ? Math.Clamp(seconds, 0, Duration) : Math.Max(0, seconds);
        _seekTarget = clamped;

        // 完了通知が来ないまま長時間残っている場合だけは、行列に積まず新しい要求として投げ直す
        if (_seekInFlight && Stopwatch.GetTimestamp() - _seekIssuedAt < SeekWatchdogTicks)
        {
            _pendingSeek = clamped;
            _hasPendingSeek = true;
            return;
        }

        IssueSeek(clamped);
    }

    private void IssueSeek(double seconds)
    {
        _hasPendingSeek = false;
        _seekInFlight = true;
        _seekIssuedAt = Stopwatch.GetTimestamp();
        if (_engine.SetCurrentTime(seconds) < 0)
        {
            // 要求自体が弾かれたときは完了通知も来ないので、その場で下ろす
            _seekInFlight = false;
        }
    }

    /// <summary>シークの完了通知を受けて、畳んでおいた最後の要求を投げ直す。</summary>
    private void CompleteSeek()
    {
        _seekInFlight = false;
        if (_hasPendingSeek)
        {
            IssueSeek(_pendingSeek);
        }
    }

    private void ResetSeekState()
    {
        _seekInFlight = false;
        _hasPendingSeek = false;
        _pendingSeek = 0;
        _seekTarget = 0;
    }

    /// <summary>音量（0.0〜1.0）。</summary>
    public void SetVolume(double volume)
    {
        if (_disposed) return;
        _ = _engine.SetVolume(Math.Clamp(volume, 0, 1));
    }

    /// <summary>ミュートの切り替え。</summary>
    public void SetMuted(bool muted)
    {
        if (_disposed) return;
        _ = _engine.SetMuted(muted ? 1 : 0);
    }

    /// <summary>ループ再生の切り替え。</summary>
    public void SetLoop(bool loop)
    {
        if (_disposed) return;
        _ = _engine.SetLoop(loop ? 1 : 0);
    }

    /// <summary>再生速度（1.0 が等速）。</summary>
    public void SetRate(double rate)
    {
        if (_disposed) return;
        _ = _engine.SetPlaybackRate(Math.Clamp(rate, 0.25, 4.0));
    }

    /// <summary>
    /// 新しいフレームが来ていれば取り出して <see cref="CurrentFrame"/> を差し替える。
    /// 差し替えたときだけ true を返す（呼び出し側はそのときだけ再描画すればよい）。
    /// </summary>
    public bool TryRenderNextFrame()
    {
        if (_disposed)
        {
            return false;
        }

        // 転送先はイベント（LOADEDMETADATA / CANPLAY）で用意するが、その時点ではまだ
        // HasVideo / GetNativeVideoSize が 0 を返すことがある。1 回取りこぼすと以後ずっと
        // 映像が出ないままになるので、フレームを引く直前にも取り直す。
        if (_frameSize.Width <= 0)
        {
            PrepareBuffers();
            if (_frameSize.Width <= 0)
            {
                ReportStall("転送先バッファを用意できていない");
                return false;
            }
        }

        // S_OK 以外（S_FALSE = まだ次のフレームが無い）は何もしない
        var forced = _forceNextFrame;
        if (!forced)
        {
            _lastTickResult = _engine.OnVideoStreamTick(out _);
            if (_lastTickResult != 0)
            {
                ReportStall("engine が新しいフレームを出していない");
                return false;
            }
        }

        _forceNextFrame = false;
        _stallTicks = 0;

        if (_wicFramePointer == 0 || _backBuffer is null)
        {
            return false;
        }

        var destination = new MediaEngineInterop.NativeRect
        {
            Left = 0,
            Top = 0,
            Right = _frameSize.Width,
            Bottom = _frameSize.Height,
        };

        var transfer = _engine.TransferVideoFrame(_wicFramePointer, 0, destination, 0);
        if (transfer < 0 || !CopyToBackBuffer())
        {
            // 毎フレーム出すとログが溢れるので、最初の 1 回だけ理由を残す
            if (!_transferFailureLogged)
            {
                _transferFailureLogged = true;
                Logger.Log(
                    $"動画フレームを取得できませんでした: hr=0x{transfer:X8}, size={_frameSize.Width}x{_frameSize.Height}",
                    LogLevel.Warning);
            }

            return false;
        }

        // 表と裏を入れ替える。同じインスタンスを描き換えるとバインディングが変化を検知できないため、
        // 2 枚を交互に差し替えて必ず「別のインスタンス」を見せる。
        (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
        CurrentFrame = _frontBuffer;
        _framesRendered++;
        return true;
    }

    /// <summary>
    /// WIC ビットマップの中身を裏バッファへ写す。
    ///
    /// ここで <c>IWICBitmap::Lock</c> を使ってはいけない。返ってくる <c>IWICBitmapLock</c> の RCW は
    /// <see cref="ComObject"/> で <see cref="IDisposable"/> を実装しておらず、
    /// <c>(locked as IDisposable)?.Dispose()</c> は何もしない。結果としてロックが GC まで残り、
    /// ロックされたままのビットマップには Media Foundation が書き込めないので、
    /// <c>TransferVideoFrame</c> が S_OK を返し続けるのに中身が更新されない
    /// （＝絵が止まり、GC が走ったときだけ数秒に 1 回だけ切り替わる）状態になる。実測で確認した。
    /// <c>CopyPixels</c> ならロックオブジェクトを持たないので、この問題が起きない。
    ///
    /// 鮮鋭化とガンマ補正はここでは掛けない。描画時に GPU シェーダー
    /// （<see cref="Controls.VideoFrameView"/>）が掛けるので、ここは素の画素を写すだけにする。
    /// </summary>
    private bool CopyToBackBuffer()
    {
        if (_wicFrame is null || _backBuffer is null)
        {
            return false;
        }

        var stride = (uint)_backBuffer.Stride;
        unsafe
        {
            fixed (byte* buffer = _backBuffer.Pixels)
            {
                return _wicFrame.CopyPixels(0, stride, stride * (uint)_frameSize.Height, (nint)buffer) >= 0;
            }
        }
    }

    /// <summary>メタデータ確定時に映像サイズを確定し、転送先バッファを用意する。</summary>
    private void PrepareBuffers()
    {
        if (_disposed || _engine.HasVideo() == 0)
        {
            return;
        }

        if (_engine.GetNativeVideoSize(out var nativeWidth, out var nativeHeight) < 0
            || nativeWidth == 0
            || nativeHeight == 0)
        {
            return;
        }

        // 転送先は縦横とも偶数にする。YUV 4:2:0 の色差は 2x2 画素まとめなので、奇数サイズの
        // 転送先を渡すと色変換が成立せず、フレームは届くのに中身が真っ黒になる
        // （1920x802 の動画で 1280x535 を要求して踏んだ）。
        var width = MakeEven((int)Math.Min(nativeWidth, MaxFrameWidth));
        var height = MakeEven((int)Math.Round(width * (double)nativeHeight / nativeWidth));
        var size = new PixelSize(width, height);
        if (size == _frameSize && _wicFrame is not null)
        {
            return;
        }

        DisposeBuffers();

        if (_wicFactory.CreateBitmap(
                (uint)size.Width,
                (uint)size.Height,
                MediaEngineInterop.PixelFormat32bppPBGRA,
                MediaEngineInterop.WicBitmapCacheOnLoad,
                out var wicBitmap) < 0)
        {
            return;
        }

        // 静的メソッド。フィールド名 ComWrappers と衝突するので型名を完全修飾する。
        if (!System.Runtime.InteropServices.ComWrappers.TryGetComInstance(wicBitmap, out var wicPointer))
        {
            return;
        }

        _wicFrame = wicBitmap;
        _wicFramePointer = wicPointer;
        _frameSize = size;
        _frontBuffer = new VideoFrame(size);
        _backBuffer = new VideoFrame(size);
    }

    private void DisposeBuffers()
    {
        if (_wicFramePointer != 0)
        {
            Marshal.Release(_wicFramePointer);
            _wicFramePointer = 0;
        }

        // 生成された RCW は ComObject で IDisposable を実装していないため、明示的に参照を落とす
        ((object?)_wicFrame as ComObject)?.FinalRelease();
        _wicFrame = null;
        _frontBuffer = null;
        _backBuffer = null;
        CurrentFrame = null;
        _frameSize = default;
    }

    /// <summary>2 以上の偶数へ丸める。</summary>
    private static int MakeEven(int value) => Math.Max(2, value - (value % 2));

    private static double Sanitize(double value)
        => double.IsFinite(value) && value > 0 ? value : 0;

    /// <summary>MF のワーカースレッドから来た通知を UI スレッドで処理する。</summary>
    private void HandleEvent(uint mediaEvent)
    {
        if (_disposed)
        {
            return;
        }

        switch (mediaEvent)
        {
            case MediaEngineInterop.EventError:
                State = VideoPlaybackState.Failed;
                break;
            case MediaEngineInterop.EventLoadedMetadata:
            case MediaEngineInterop.EventFormatChange:
                PrepareBuffers();
                Duration = Sanitize(_engine.GetDuration());
                break;
            case MediaEngineInterop.EventCanPlay:
                PrepareBuffers();
                Duration = Sanitize(_engine.GetDuration());
                State = VideoPlaybackState.Ready;
                _forceNextFrame = true;
                break;
            case MediaEngineInterop.EventSeeked:
                CompleteSeek();
                _forceNextFrame = true;
                break;
            case MediaEngineInterop.EventFirstFrameReady:
                _forceNextFrame = true;
                break;
            case MediaEngineInterop.EventDurationChange:
                Duration = Sanitize(_engine.GetDuration());
                break;
            case MediaEngineInterop.EventEnded:
                Ended?.Invoke(this, EventArgs.Empty);
                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notify.Owner = null;

        try
        {
            _ = _engine.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.LogException("動画再生セッションの終了に失敗しました", ex);
        }

        DisposeBuffers();

        // engine を GC まで生かしておくと、MF_MEDIA_ENGINE_SYNCHRONOUS_CLOSE を立てた意図
        // （前の engine の解放が終わってから次を作る）が崩れるので、ここで確定的に手放す。
        ComRelease.Release(_engine);
        ComRelease.Release(_wicFactory);

        if (_notifyComPointer != 0)
        {
            Marshal.Release(_notifyComPointer);
        }
    }

    private static bool EnsureMediaFoundation()
    {
        lock (MediaFoundationLock)
        {
            if (s_mediaFoundationStarted)
            {
                return true;
            }

            if (MediaEngineInterop.MFStartup(MediaEngineInterop.MfVersion, MediaEngineInterop.MfStartupLite) < 0)
            {
                return false;
            }

            s_mediaFoundationStarted = true;
            return true;
        }
    }

    /// <summary>Media Engine へ渡すコールバック実体。engine が保持するため、セッションとは弱く結ぶ。</summary>
    [GeneratedComClass]
    internal sealed partial class EngineNotify : IMFMediaEngineNotify
    {
        public VideoPlaybackSession? Owner { get; set; }

        public int EventNotify(uint mediaEvent, nuint param1, uint param2)
        {
            var owner = Owner;
            if (owner is not null)
            {
                Dispatcher.UIThread.Post(() => owner.HandleEvent(mediaEvent), DispatcherPriority.Background);
            }

            return 0;
        }
    }
}
