namespace Kiriha.Services;

/// <summary>同一フォルダーの監視をタブ間で共有し、変更通知をまとめて配信する。</summary>
internal static class DirectoryObservationService
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Observation> Observations = new(WindowsPathIdentity.Instance);

    /// <summary>
    /// 変更通知を購読する。callback には「最後に観測したファイルシステムイベントの UTC 時刻」を渡す。
    /// 購読側はこの時刻より後に一覧を読み込み済みなら再読み込みを省略できる（多重リフレッシュ防止）。
    /// </summary>
    public static IDisposable? Subscribe(string path, Action<DateTime> callback)
    {
        try
        {
            lock (Gate)
            {
                if (!Observations.TryGetValue(path, out var observation))
                {
                    observation = new Observation(path);
                    Observations.Add(path, observation);
                }
                observation.Callbacks.Add(callback);
                return new Subscription(path, callback);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException($"フォルダー監視を開始できませんでした: {path}", ex);
            return null;
        }
    }

    private static void Unsubscribe(string path, Action<DateTime> callback)
    {
        lock (Gate)
        {
            if (!Observations.TryGetValue(path, out var observation)) return;
            observation.Callbacks.Remove(callback);
            if (observation.Callbacks.Count == 0)
            {
                observation.Dispose();
                Observations.Remove(path);
            }
        }
    }

    private sealed class Observation : IDisposable
    {
        private readonly string _path;
        private FileSystemWatcher _watcher;
        private CancellationTokenSource? _debounce;
        private long _lastEventTicksUtc;
        private bool _disposed;
        public List<Action<DateTime>> Callbacks { get; } = [];

        public Observation(string path)
        {
            _path = path;
            _watcher = CreateWatcher();
        }

        private FileSystemWatcher CreateWatcher()
        {
            var watcher = new FileSystemWatcher(_path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += Changed;
            watcher.Changed += Changed;
            watcher.Error += OnWatcherError;
            return watcher;
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Logger.LogException($"フォルダー監視でエラーが発生しました: {_path}", e.GetException());
            // FileSystemWatcher は Error 後にイベントを出さなくなることがある（ネットワーク瞬断・
            // バッファ溢れ等）。共有中の全タブが監視を失ったままにならないよう作り直して復旧する。
            _ = RestartWatcherAsync();
        }

        private async Task RestartWatcherAsync()
        {
            await Task.Delay(2000);
            var restarted = false;
            lock (Gate)
            {
                if (_disposed) return;
                try
                {
                    _watcher.Dispose();
                    _watcher = CreateWatcher();
                    restarted = true;
                }
                catch (Exception ex)
                {
                    // パス消失（アンマウント等）なら復旧不能。購読解除に任せる。
                    Logger.LogException($"フォルダー監視を再開できませんでした: {_path}", ex);
                }
            }
            if (restarted)
            {
                // 停止中に見逃した変更を取りこぼさないよう、購読側へ一度通知して再読込を促す
                Changed(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _path, null));
            }
        }

        private void Changed(object sender, FileSystemEventArgs e)
        {
            Interlocked.Exchange(ref _lastEventTicksUtc, DateTime.UtcNow.Ticks);
            // FileSystemWatcher のイベントは ThreadPool 上で並行に届く。_debounce の差し替えを
            // ロックなしで行うと、2 スレッドが同じ旧 CTS に対して Cancel/Dispose を重複実行し、
            // Dispose 済み CTS への Cancel が ObjectDisposedException（ThreadPool 上の未処理例外）
            // になり得るため、Dispose との競合も含めて Gate で直列化する。
            CancellationTokenSource cts;
            lock (Gate)
            {
                if (_disposed) return;
                _debounce?.Cancel();
                _debounce?.Dispose();
                cts = new CancellationTokenSource();
                _debounce = cts;
            }
            _ = NotifyAfterDelayAsync(cts.Token);
        }

        private async Task NotifyAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(600, token);
                var lastEventUtc = new DateTime(Interlocked.Read(ref _lastEventTicksUtc), DateTimeKind.Utc);
                Action<DateTime>[] callbacks;
                lock (Gate) callbacks = Callbacks.ToArray();
                foreach (var callback in callbacks) callback(lastEventUtc);
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            // Unsubscribe から Gate ロック下で呼ばれる。再起動処理との競合は _disposed で防ぐ。
            _disposed = true;
            _debounce?.Cancel();
            _debounce?.Dispose();
            _watcher.Dispose();
        }
    }

    private sealed class Subscription(string path, Action<DateTime> callback) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Unsubscribe(path, callback);
        }
    }
}
