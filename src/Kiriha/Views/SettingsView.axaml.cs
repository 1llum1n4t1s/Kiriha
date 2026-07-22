using Avalonia.Controls;
using Avalonia.Interactivity;
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
        if (VisualRoot is Window window && window.DataContext is MainWindowViewModel vm)
        {
            UpdateService.Check4Update(window, vm.Settings, manually: true);
        }
    }
}
