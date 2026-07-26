using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiriha.Services;

/// <summary>1 フォルダー分の表示方法と並べ替え設定。</summary>
internal sealed class FolderViewSettings
{
    public string Path { get; set; } = "";

    public string ViewMode { get; set; } = "Details";

    public double IconSize { get; set; } = 28;

    public string SortKey { get; set; } = "Name";

    public bool SortAscending { get; set; } = true;

    /// <summary>詳細表示の列幅（キーは DetailColumnViewModel.Key）。
    /// 未保存のフォルダーでは空で、その場合は設定の既定幅を使う。</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = [];

    public long UpdatedUtcTicks { get; set; }
}

internal sealed class FolderViewSettingsStore
{
    public List<FolderViewSettings> Folders { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FolderViewSettingsStore))]
internal sealed partial class FolderViewSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// フォルダー別表示設定をメモリ上の辞書で管理し、変更をまとめて JSON へ永続化する。
/// </summary>
internal sealed class FolderViewSettingsService : IDisposable
{
    private const int MaxEntries = 4096;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);
    // 置き場は AppStoragePaths が正本（テストが実ユーザーの設定を壊さないよう差し替え可能）。
    private static string SettingsDirectory => AppStoragePaths.Directory;
    private static string SettingsPath => Path.Combine(SettingsDirectory, "folder-views.json");
    private static string BackupPath => SettingsPath + ".bak";

    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly Dictionary<string, FolderViewSettings> _settings = new(WindowsPathIdentity.Instance);
    private readonly Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public FolderViewSettingsService()
    {
        Load();
        _saveTimer = new Timer(static state => ((FolderViewSettingsService)state!).SavePending(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_dirty)
        {
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public bool TryGet(string path, out FolderViewSettings settings)
    {
        var normalizedPath = NormalizePath(path);
        lock (_gate)
        {
            if (_settings.TryGetValue(normalizedPath, out var stored))
            {
                settings = Clone(stored);
                return true;
            }
        }

        settings = null!;
        return false;
    }

    public void Set(string path, FolderViewSettings settings)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            // 破棄後は記憶を変更しない。Dispose で書き切った後に遅れて Set / Clear が届いても
            // 保存済みの内容を上書きしないようにする（終了処理の順序に依存しないため）。
            if (_disposed)
            {
                return;
            }

            if (_settings.TryGetValue(normalizedPath, out var current)
                && HasSameViewSettings(current, settings))
            {
                return;
            }

            if (!_settings.ContainsKey(normalizedPath) && _settings.Count >= MaxEntries)
            {
                RemoveOldestEntries();
            }

            var stored = Clone(settings);
            stored.Path = normalizedPath;
            stored.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            _settings[normalizedPath] = stored;
            _dirty = true;
            ScheduleSave();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Set と同じ理由で、破棄後の全消去は保存済みの内容へ反映しない。
            if (_disposed)
            {
                return;
            }

            _settings.Clear();
            _dirty = true;
            ScheduleSave();
        }
    }

    /// <summary>遅延保存を予約する。呼び出し元は破棄済みを弾いているが、
    /// タイマー操作の単一の出口として破棄済みガードもここに置く（_gate 内から呼ぶ）。</summary>
    private void ScheduleSave()
    {
        if (_disposed)
        {
            return;
        }

        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>終了時などに保留中の変更を同期的に書き出す。</summary>
    public void Flush()
        => SavePending();

    /// <summary>
    /// 遅延保存タイマーを止め、保留中の変更を書き切る。多重呼び出しは安全。
    /// 破棄後は Set / Clear を無視するため、書き切った内容が後から上書きされることはない。
    /// 破棄後の Flush も（保留がないので）ファイルを変更しない。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // タイマー停止後に保留分を書き切る（_gate の外で行い、SavePending 内の lock と競合させない）。
        SavePending();
        _saveTimer.Dispose();
    }

    private void Load()
    {
        var store = TryLoad(SettingsPath) ?? TryLoad(BackupPath);
        if (store is null)
        {
            return;
        }

        foreach (var entry in store.Folders)
        {
            var normalizedPath = NormalizePath(entry.Path);
            if (normalizedPath.Length == 0)
            {
                continue;
            }

            // 正規化で複数エントリが同じキーへ畳まれることがある（区切り文字統一の強化以前に
            // C:/work と C:\work の両方が記録されていた場合など）。保存は新しい順に並べるため
            // 素朴に代入すると後から来る古いエントリが新しい設定を上書きしてしまう。
            // 最も新しい更新日時のものを残す。
            if (_settings.TryGetValue(normalizedPath, out var existing)
                && existing.UpdatedUtcTicks >= entry.UpdatedUtcTicks)
            {
                // 重複を1件に畳んだ結果はファイルへ書き戻す必要がある。
                _dirty = true;
                continue;
            }

            var stored = Clone(entry);
            stored.Path = normalizedPath;
            if (_settings.ContainsKey(normalizedPath))
            {
                _dirty = true;
            }

            _settings[normalizedPath] = stored;
        }

        if (_settings.Count > MaxEntries)
        {
            RemoveOldestEntries(_settings.Count - MaxEntries);
            _dirty = true;
        }
    }

    private static FolderViewSettingsStore? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                FolderViewSettingsJsonContext.Default.FolderViewSettingsStore);
        }
        catch (Exception ex)
        {
            Logger.LogException($"フォルダー別表示設定を読み込めませんでした: {path}", ex);
            return null;
        }
    }

    private void SavePending()
    {
        lock (_writeGate)
        {
            FolderViewSettingsStore store;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                // 破棄済みなら Change は ObjectDisposedException を投げる。
                // 終了処理は ShutdownRequested で Dispose → その後 MainWindow.OnClosing 経由で Flush が
                // 走る順序になり得るため（保存が失敗して _dirty が戻った場合はここへ到達する）、
                // 破棄済みではタイマー操作を飛ばして書き出しだけ行う。
                if (!_disposed)
                {
                    _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }

                store = new FolderViewSettingsStore
                {
                    Folders = _settings.Values
                        .OrderByDescending(value => value.UpdatedUtcTicks)
                        .Select(Clone)
                        .ToList(),
                };
                _dirty = false;
            }

            if (!TrySave(store))
            {
                lock (_gate)
                {
                    _dirty = true;
                }
            }
        }
    }

    private static bool TrySave(FolderViewSettingsStore store)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporary = Path.Combine(SettingsDirectory, $"folder-views-{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(
                    store,
                    FolderViewSettingsJsonContext.Default.FolderViewSettingsStore);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporary, SettingsPath, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, SettingsPath);
                }
            }
            finally
            {
                File.Delete(temporary);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogException("フォルダー別表示設定を保存できませんでした", ex);
            return false;
        }
    }

    private void RemoveOldestEntries(int count = MaxEntries / 8)
    {
        foreach (var path in _settings
                     .OrderBy(pair => pair.Value.UpdatedUtcTicks)
                     .Take(count)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _settings.Remove(path);
        }
    }

    private static string NormalizePath(string path)
        => WindowsPathIdentity.Normalize(path);

    private static bool HasSameViewSettings(FolderViewSettings left, FolderViewSettings right)
        => left.ViewMode == right.ViewMode
           && left.IconSize.Equals(right.IconSize)
           && left.SortKey == right.SortKey
           && left.SortAscending == right.SortAscending
           && HasSameColumnWidths(left.ColumnWidths, right.ColumnWidths);

    private static bool HasSameColumnWidths(Dictionary<string, double> left, Dictionary<string, double> right)
        => left.Count == right.Count
           && left.All(pair => right.TryGetValue(pair.Key, out var width) && width.Equals(pair.Value));

    private static FolderViewSettings Clone(FolderViewSettings source)
        => new()
        {
            Path = source.Path,
            ViewMode = source.ViewMode,
            IconSize = source.IconSize,
            SortKey = source.SortKey,
            SortAscending = source.SortAscending,
            // 旧形式（列幅を持たない）や手書きの null を読み込んでも落ちないよう空辞書へ寄せる。
            ColumnWidths = source.ColumnWidths is null ? [] : new Dictionary<string, double>(source.ColumnWidths),
            UpdatedUtcTicks = source.UpdatedUtcTicks,
        };
}
