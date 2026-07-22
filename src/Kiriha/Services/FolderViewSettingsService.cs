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
internal sealed class FolderViewSettingsService
{
    private const int MaxEntries = 4096;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiriha");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "folder-views.json");
    private static readonly string BackupPath = SettingsPath + ".bak";

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<string, FolderViewSettings> _settings = new(WindowsPathIdentity.Instance);
    private readonly Timer _saveTimer;
    private bool _dirty;

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
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _settings.Clear();
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>終了時などに保留中の変更を同期的に書き出す。</summary>
    public void Flush()
        => SavePending();

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

            var stored = Clone(entry);
            stored.Path = normalizedPath;
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

                _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return path == FileSystemService.ComputerPath
            ? path
            : Path.TrimEndingDirectorySeparator(path);
    }

    private static bool HasSameViewSettings(FolderViewSettings left, FolderViewSettings right)
        => left.ViewMode == right.ViewMode
           && left.IconSize.Equals(right.IconSize)
           && left.SortKey == right.SortKey
           && left.SortAscending == right.SortAscending;

    private static FolderViewSettings Clone(FolderViewSettings source)
        => new()
        {
            Path = source.Path,
            ViewMode = source.ViewMode,
            IconSize = source.IconSize,
            SortKey = source.SortKey,
            SortAscending = source.SortAscending,
            UpdatedUtcTicks = source.UpdatedUtcTicks,
        };
}
