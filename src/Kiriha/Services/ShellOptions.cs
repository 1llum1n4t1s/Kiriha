namespace Kiriha.Services;

public enum FileIconSet { Original, Material, Windows }
public enum ShellOptionKind { ShowHidden, ShowExtensions, ShowCheckBoxes, IconSet }
public sealed class ShellOptionChangedEventArgs(ShellOptionKind kind) : EventArgs
{
    public ShellOptionKind Kind { get; } = kind;
}

/// <summary>全タブ共通の表示オプション（隠しファイル / 拡張子）。変更時に Changed を発火する。</summary>
public sealed class ShellOptions
{
    private bool _showHidden;
    private bool _showExtensions;
    private bool _showCheckBoxes;
    private FileIconSet _iconSet;

    public event EventHandler<ShellOptionChangedEventArgs>? Changed;

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (_showHidden == value) return;
            _showHidden = value;
            Changed?.Invoke(this, new ShellOptionChangedEventArgs(ShellOptionKind.ShowHidden));
        }
    }

    public bool ShowExtensions
    {
        get => _showExtensions;
        set
        {
            if (_showExtensions == value) return;
            _showExtensions = value;
            Changed?.Invoke(this, new ShellOptionChangedEventArgs(ShellOptionKind.ShowExtensions));
        }
    }

    /// <summary>エクスプローラーの「項目チェックボックス」相当。</summary>
    public bool ShowCheckBoxes
    {
        get => _showCheckBoxes;
        set
        {
            if (_showCheckBoxes == value) return;
            _showCheckBoxes = value;
            Changed?.Invoke(this, new ShellOptionChangedEventArgs(ShellOptionKind.ShowCheckBoxes));
        }
    }

    /// <summary>ファイル一覧で使うアイコンセット。</summary>
    public FileIconSet IconSet
    {
        get => _iconSet;
        set
        {
            if (_iconSet == value) return;
            _iconSet = value;
            Changed?.Invoke(this, new ShellOptionChangedEventArgs(ShellOptionKind.IconSet));
        }
    }
}
