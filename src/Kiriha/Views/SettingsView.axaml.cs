using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Kiriha.Services;
using Kiriha.ViewModels;

namespace Kiriha.Views;

/// <summary>設定タブの中身（MainWindow.axaml から分離）。DataContext は設定タブの TabViewModel。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        // Avalonia 12 では Window の内容が TopLevelHost 配下にホストされるため、VisualRoot は Window ではなく
        // TopLevelHost になる。VisualRoot を Window として見ると常に外れて、ボタンが無反応になる。
        // この画面の XAML が使っている $parent[Window] と同じく論理ツリーを辿って所有ウィンドウを得る。
        if (this.FindLogicalAncestorOfType<Window>() is not { } window)
        {
            Logger.Log("更新チェック: 所有ウィンドウを特定できませんでした", LogLevel.Warning);
            return;
        }

        if (window.DataContext is not MainWindowViewModel vm)
        {
            Logger.Log(
                $"更新チェック: ウィンドウの DataContext が想定外です ({window.DataContext?.GetType().Name ?? "null"})",
                LogLevel.Warning);
            return;
        }

        UpdateService.Check4Update(window, vm.Settings, manually: true);
    }
}
