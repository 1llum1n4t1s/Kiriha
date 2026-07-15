using Avalonia;
using Avalonia.Controls;

namespace Kiriha.Views;

/// <summary>アクリル設定と現在のカラーテーマに追従する共通ダイアログ。</summary>
public partial class ThemedDialogWindow : Window
{
    public ThemedDialogWindow() : this(useAcrylic: false)
    {
    }

    public ThemedDialogWindow(bool useAcrylic)
    {
        InitializeComponent();

        ExtendClientAreaToDecorationsHint = useAcrylic;
        ExtendClientAreaTitleBarHeightHint = useAcrylic ? 32 : -1;
        TransparencyLevelHint = useAcrylic
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        AcrylicBackdrop.IsVisible = useAcrylic;
        ContentSurface.Padding = useAcrylic ? new Thickness(0, 32, 0, 0) : default;
    }

    /// <summary>アクリル背景の前面へ表示するダイアログ内容。</summary>
    public object? DialogContent
    {
        get => DialogContentPresenter.Content;
        set => DialogContentPresenter.Content = value;
    }
}
