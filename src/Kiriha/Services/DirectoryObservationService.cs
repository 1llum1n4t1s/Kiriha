namespace Kiriha.Services;

/// <summary>同一フォルダーの監視をタブ間で共有し、変更通知をまとめて配信する。</summary>
internal static class DirectoryObservationService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Observation> Observations = new(WindowsPathIdentity.Instance);

    public static IDisposable? Subscribe(string path, Action callback)
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

    private static void Unsubscribe(string path, Action callback)
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
        public List<Action> Callbacks { get; } = [];

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
            _debounce?.Cancel();
            var cts = new CancellationTokenSource();
            _debounce = cts;
            _ = NotifyAfterDelayAsync(cts.Token);
        }

        private async Task NotifyAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(600, token);
                Action[] callbacks;
                lock (Gate) callbacks = Callbacks.ToArray();
                foreach (var callback in callbacks) callback();
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _debounce?.Cancel();
            _watcher.Dispose();
        }
    }

    private sealed class Subscription(string path, Action callback) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Unsubscribe(path, callback);
        }
    }
}
