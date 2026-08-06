namespace Kiriha.Services;

/// <summary>同一フォルダーの監視をタブ間で共有し、変更通知をまとめて配信する。</summary>
internal static class DirectoryObservationService
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Observation> Observations = new(WindowsPathIdentity.Instance);

    /// <summary>変更をまとめて配信するまでの待ち時間。最初のイベントから数えるので、
    /// 長いコピー中でもこの間隔で少しずつ画面へ反映される（イベントごとに延長しない）。</summary>
    private const int BatchDelayMilliseconds = 250;

    /// <summary>1 束で個別に配る変更の上限。これを超えたら全体の読み直しを依頼する
    /// （数千件の差分適用より再列挙の方が速く、監視バッファ溢れで取りこぼしている可能性も高い）。</summary>
    private const int MaxChangesPerBatch = 512;

    /// <summary>
    /// 変更通知を購読する。callback には「そのフォルダーで実際に何が起きたか」を渡す。
    /// 購読側は該当項目だけを更新でき、<see cref="DirectoryChangeBatch.NeedsFullReload"/> の
    /// ときだけ全体を読み直せばよい。
    /// </summary>
    public static IDisposable? Subscribe(string path, Action<DirectoryChangeBatch> callback)
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

    private static void Unsubscribe(string path, Action<DirectoryChangeBatch> callback)
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

        /// <summary>配信待ちの変更（パス→種類）。同じパスの重複は合流させる。</summary>
        private readonly Dictionary<string, DirectoryChangeKind> _pending = new(WindowsPathIdentity.Instance);
        private bool _pendingFullReload;
        private long _lastEventTicksUtc;
        private bool _disposed;
        public List<Action<DirectoryChangeBatch>> Callbacks { get; } = [];

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
            watcher.Created += (_, e) => Record(DirectoryChangeKind.Created, e.FullPath);
            watcher.Deleted += (_, e) => Record(DirectoryChangeKind.Deleted, e.FullPath);
            watcher.Changed += (_, e) => Record(DirectoryChangeKind.Updated, e.FullPath);
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            return watcher;
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            // 名前変更は「古い名前が消えて新しい名前が生えた」と等価に扱う。行の作り直しになるが、
            // 表示名・拡張子・並び順がまとめて変わるため、その方が取りこぼしがない。
            Record(DirectoryChangeKind.Deleted, e.OldFullPath);
            Record(DirectoryChangeKind.Created, e.FullPath);
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
                // 停止中に見逃した変更は差分では埋められないため、全体の読み直しを依頼する
                RequestFullReload();
            }
        }

        /// <summary>差分では追随できないことが分かったので、次の配信で全体の読み直しを依頼する。</summary>
        private void RequestFullReload()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _pendingFullReload = true;
                _pending.Clear();
            }

            Schedule();
        }

        private void Record(DirectoryChangeKind kind, string fullPath)
        {
            Interlocked.Exchange(ref _lastEventTicksUtc, DateTime.UtcNow.Ticks);
            lock (Gate)
            {
                if (_disposed) return;
                if (!_pendingFullReload)
                {
                    Merge(kind, fullPath);
                    if (_pending.Count > MaxChangesPerBatch)
                    {
                        _pendingFullReload = true;
                        _pending.Clear();
                    }
                }
            }

            Schedule();
        }

        /// <summary>同じパスに対する複数のイベントを 1 件へ畳む。Gate ロック下で呼ぶこと。</summary>
        private void Merge(DirectoryChangeKind kind, string fullPath)
        {
            if (!_pending.TryGetValue(fullPath, out var existing))
            {
                _pending[fullPath] = kind;
                return;
            }

            _pending[fullPath] = (existing, kind) switch
            {
                // 作られた直後に消えた（一時ファイル等）なら、表示上は何も起きていない扱いでよい。
                // ただし取りこぼしを避けるため Deleted として配る（存在しなければ何もしないだけ）。
                (DirectoryChangeKind.Created, DirectoryChangeKind.Deleted) => DirectoryChangeKind.Deleted,
                // 消えた直後に同名で作られた（上書き保存）なら、行としては作り直しになる。
                (DirectoryChangeKind.Deleted, DirectoryChangeKind.Created) => DirectoryChangeKind.Created,
                // 追加された項目への書き込みは追加のまま（まだ画面に出ていないので更新にはならない）
                (DirectoryChangeKind.Created, DirectoryChangeKind.Updated) => DirectoryChangeKind.Created,
                _ => kind,
            };
        }

        /// <summary>配信タイマーを起動する（動作中なら何もしない）。イベントごとに延長しないので、
        /// 変更が続いている間も <see cref="BatchDelayMilliseconds"/> ごとに反映される。</summary>
        private void Schedule()
        {
            CancellationTokenSource cts;
            lock (Gate)
            {
                if (_disposed || _debounce is not null) return;
                cts = new CancellationTokenSource();
                _debounce = cts;
            }

            _ = NotifyAfterDelayAsync(cts.Token);
        }

        private async Task NotifyAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(BatchDelayMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DirectoryChangeBatch batch;
            Action<DirectoryChangeBatch>[] callbacks;
            lock (Gate)
            {
                if (_disposed) return;
                _debounce?.Dispose();
                _debounce = null;
                batch = new DirectoryChangeBatch
                {
                    LastEventUtc = new DateTime(Interlocked.Read(ref _lastEventTicksUtc), DateTimeKind.Utc),
                    NeedsFullReload = _pendingFullReload,
                    Changes = _pending.Select(pair => new DirectoryChange(pair.Value, pair.Key)).ToList(),
                };
                _pending.Clear();
                _pendingFullReload = false;
                callbacks = Callbacks.ToArray();
            }

            if (!batch.NeedsFullReload && batch.Changes.Count == 0)
            {
                return;
            }

            foreach (var callback in callbacks) callback(batch);
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

    private sealed class Subscription(string path, Action<DirectoryChangeBatch> callback) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Unsubscribe(path, callback);
        }
    }
}
