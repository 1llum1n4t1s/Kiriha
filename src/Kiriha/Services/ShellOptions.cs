namespace Kiriha.Services;

public enum ShellOptionKind { ShowHidden, ShowExtensions, ShowCheckBoxes, UseMaterialIcons }
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
    private bool _useMaterialIcons;

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

    /// <summary>絵文字アイコンの代わりに Material Icon Theme のアイコンを使う。</summary>
    public bool UseMaterialIcons
    {
        get => _useMaterialIcons;
        set
        {
            if (_useMaterialIcons == value) return;
            _useMaterialIcons = value;
            Changed?.Invoke(this, new ShellOptionChangedEventArgs(ShellOptionKind.UseMaterialIcons));
        }
    }
}
