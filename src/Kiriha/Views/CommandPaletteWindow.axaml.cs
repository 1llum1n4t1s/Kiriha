using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kiriha.Models;
using Kiriha.Services;

namespace Kiriha.Views;

public partial class CommandPaletteWindow : Window
{
    public ObservableCollection<FeatureCommand> Commands { get; } = new();
    public event EventHandler<FeatureCommand>? CommandSelected;

    public CommandPaletteWindow() : this(false)
    {
    }

    public CommandPaletteWindow(bool useAcrylic)
    {
        InitializeComponent();
        DataContext = this;
        TransparencyLevelHint = useAcrylic
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        RefreshItems();
        Opened += (_, _) => SearchBox.Focus();
    }

    private void RefreshItems()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var matches = FeatureCatalog.All.Where(x => query.Length == 0
            || x.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Commands.Clear();
        foreach (var command in matches) Commands.Add(command);
        CountText.Text = query.Length == 0 ? "" : $"候補 {matches.Count} 件";
        CommandsList.SelectedIndex = Commands.Count > 0 ? 0 : -1;
    }

    private void RunSelected()
    {
        if (CommandsList.SelectedItem is not FeatureCommand command) return;
        CommandSelected?.Invoke(this, command);
        Close();
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshItems();

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { CommandsList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { RunSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void CommandsList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void CommandsList_DoubleTapped(object? sender, TappedEventArgs e) => RunSelected();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
