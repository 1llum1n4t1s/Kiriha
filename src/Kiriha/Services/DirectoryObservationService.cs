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
        private readonly FileSystemWatcher _watcher;
        private CancellationTokenSource? _debounce;
        private long _lastEventTicksUtc;
        public List<Action<DateTime>> Callbacks { get; } = [];

        public Observation(string path)
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += Changed;
            _watcher.Changed += Changed;
            _watcher.Error += (_, e) => Logger.LogException($"フォルダー監視でエラーが発生しました: {path}", e.GetException());
        }

        private void Changed(object sender, FileSystemEventArgs e)
        {
            Interlocked.Exchange(ref _lastEventTicksUtc, DateTime.UtcNow.Ticks);
            var previous = _debounce;
            previous?.Cancel();
            previous?.Dispose();
            var cts = new CancellationTokenSource();
            _debounce = cts;
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
