using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Win32;

namespace Kiriha.Views;

/// <summary>Lhamiel のダイアログ外観に合わせた、名前入力専用ダイアログ。</summary>
public partial class TextInputDialog : Window
{
    private const int SmRemoteSession = 0x1000;
    private bool _useAcrylic;
    private int _selectionLength;

    public TextInputDialog()
    {
        InitializeComponent();
    }

    public TextInputDialog(string title, string initial, int selectionLength, bool useAcrylic)
        : this()
    {
        Title = title;
        InputBox.Text = initial;
        _selectionLength = Math.Clamp(selectionLength, 0, initial.Length);
        _useAcrylic = useAcrylic;

        if (!useAcrylic)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
        }

        Opened += OnOpened;
        Activated += (_, _) => UpdateBackdrop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty)
            {
                UpdateBackdrop();
            }
        };
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        UpdateBackdrop();
        InputBox.Focus();
        InputBox.SelectionStart = 0;
        InputBox.SelectionEnd = _selectionLength;
    }

    private void UpdateBackdrop()
    {
        var useFallback = !_useAcrylic || ShouldUseOpaqueBackground();
        AcrylicBackdrop.IsVisible = _useAcrylic && !useFallback;
        OpaqueFallback.IsVisible = useFallback;
        AccentTintOverlay.IsVisible = _useAcrylic;
    }

    private bool ShouldUseOpaqueBackground()
    {
        if (ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur)
        {
            return true;
        }

        return OperatingSystem.IsWindows() && (IsRemoteSession() || !IsTransparencyEnabled());
    }

    private static bool IsRemoteSession()
    {
        try
        {
            return GetSystemMetrics(SmRemoteSession) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransparencyEnabled()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency",
                1);
            return value is not int enabled || enabled != 0;
        }
        catch
        {
            return true;
        }
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
        => Close(InputBox.Text ?? string.Empty);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);
}
