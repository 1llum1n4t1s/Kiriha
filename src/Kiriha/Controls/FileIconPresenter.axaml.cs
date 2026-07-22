using Avalonia;
using Avalonia.Controls;
using Kiriha.Models;
using Kiriha.ViewModels;

namespace Kiriha.Controls;

/// <summary>
/// ファイル一覧の 1 項目分のアイコン表示（Material / Windows / 絵文字）。
/// 3 つのビュー（詳細 / 一覧 / アイコン）でテンプレートの重複をなくすための共通コントロール。
/// </summary>
public partial class FileIconPresenter : UserControl
{
    /// <summary>表示対象の項目。</summary>
    public static readonly StyledProperty<FileSystemEntry?> EntryProperty =
        AvaloniaProperty.Register<FileIconPresenter, FileSystemEntry?>(nameof(Entry));

    /// <summary>アイコンセットの表示フラグを持つタブ。</summary>
    public static readonly StyledProperty<TabViewModel?> OwnerProperty =
        AvaloniaProperty.Register<FileIconPresenter, TabViewModel?>(nameof(Owner));

    /// <summary>画像アイコンの一辺のサイズ。</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<FileIconPresenter, double>(nameof(IconSize), 16d);

    /// <summary>ScaleEmoji=False のときの絵文字フォントサイズ。</summary>
    public static readonly StyledProperty<double> EmojiFontSizeProperty =
        AvaloniaProperty.Register<FileIconPresenter, double>(nameof(EmojiFontSize), 14d);

    /// <summary>絵文字を IconSize へ拡大表示するか（アイコンビュー用）。</summary>
    public static readonly StyledProperty<bool> ScaleEmojiProperty =
        AvaloniaProperty.Register<FileIconPresenter, bool>(nameof(ScaleEmoji));

    public FileIconPresenter()
    {
        InitializeComponent();
    }

    public FileSystemEntry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    public TabViewModel? Owner
    {
        get => GetValue(OwnerProperty);
        set => SetValue(OwnerProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double EmojiFontSize
    {
        get => GetValue(EmojiFontSizeProperty);
        set => SetValue(EmojiFontSizeProperty, value);
    }

    public bool ScaleEmoji
    {
        get => GetValue(ScaleEmojiProperty);
        set => SetValue(ScaleEmojiProperty, value);
    }
}
