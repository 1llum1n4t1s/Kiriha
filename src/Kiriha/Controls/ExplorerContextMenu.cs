using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Kiriha.Models;

namespace Kiriha.Controls;

/// <summary>
/// Windows 11 のエクスプローラー風に自前描画するコンテキストメニュー。
/// </summary>
/// <remarks>
/// <para>
/// 項目の実体は <see cref="ExplorerMenuEntry"/>。シェル由来の項目も Kiriha 自前の項目も
/// 同じモデルで受け取り、ここで Avalonia の <see cref="MenuItem"/> へ組み替える。
/// </para>
/// <para>
/// <b>選択された処理はメニューが閉じてから実行する。</b>ポップアップが出たままシェル拡張の verb を
/// 呼ぶと、拡張側が出すモーダル UI がメニューの下敷きになったり、メニューが閉じずに残ったりする。
/// クリック時は実行内容を <c>_pending</c> に控えるだけにして、<see cref="ContextMenu.Closed"/> の後の
/// <see cref="DispatcherPriority.Background"/> で実行し、そこで COM セッションも破棄する。
/// </para>
/// </remarks>
public sealed partial class ExplorerContextMenu : ContextMenu
{
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE。</summary>
    private const uint DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND。ウィンドウの角を DWM に丸めさせる。</summary>
    private const int DwmwcpRound = 2;

    /// <summary>上部のアイコン行を出すかどうか（テンプレートの可視性バインド用）。</summary>
    public static readonly StyledProperty<bool> HasHeaderEntriesProperty =
        AvaloniaProperty.Register<ExplorerContextMenu, bool>(nameof(HasHeaderEntries));

    private IReadOnlyList<ExplorerMenuEntry> _headerEntries = [];
    private IDisposable? _session;
    private Action? _pending;
    private Action? _afterInvoke;

    public bool HasHeaderEntries
    {
        get => GetValue(HasHeaderEntriesProperty);
        set => SetValue(HasHeaderEntriesProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ExplorerContextMenu);

    /// <summary>
    /// メニューを組み立ててマウス位置に表示する。
    /// </summary>
    /// <param name="target">配置の基準にするコントロール（この論理ツリー上にメニューがぶら下がる）。</param>
    /// <param name="entries">縦に並べる項目。</param>
    /// <param name="headerEntries">上部に横並びにするアイコン項目（空なら行ごと出さない）。</param>
    /// <param name="session">項目の実行に必要な COM セッション。閉じた後にここで破棄する。</param>
    /// <param name="afterInvoke">何か実行されたときだけ最後に呼ぶ後処理（一覧の再読み込み等）。</param>
    /// <param name="anchor">
    /// キーボード（アプリケーションキー / Shift+F10）から開くときの基準矩形。<paramref name="target"/> 上の
    /// 座標で指定する。null ならマウス位置に出す。マウスが別の場所にあるまま
    /// <see cref="PlacementMode.Pointer"/> で出すと、選択中の項目と無関係な位置に出てしまうため。
    /// </param>
    public static void Show(
        Control target,
        IReadOnlyList<ExplorerMenuEntry> entries,
        IReadOnlyList<ExplorerMenuEntry> headerEntries,
        IDisposable? session,
        Action? afterInvoke,
        Rect? anchor = null)
    {
        var menu = new ExplorerContextMenu
        {
            _headerEntries = headerEntries,
            _session = session,
            _afterInvoke = afterInvoke,
            HasHeaderEntries = headerEntries.Count > 0,
            Placement = anchor is null ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedLeft,
        };

        if (anchor is { } rect)
        {
            menu.PlacementRect = rect;
        }

        var itemTheme = FindTheme(target, "ExplorerMenuItemTheme");
        var separatorTheme = FindTheme(target, "ExplorerMenuSeparatorTheme");
        foreach (var control in menu.BuildItems(entries, itemTheme, separatorTheme))
        {
            menu.Items.Add(control);
        }

        menu.Closed += menu.OnMenuClosed;
        menu.Open(target);
    }

    private List<Control> BuildItems(
        IReadOnlyList<ExplorerMenuEntry> entries,
        ControlTheme? itemTheme,
        ControlTheme? separatorTheme)
    {
        var controls = new List<Control>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                // 先頭・末尾・連続の区切り線は出さない（項目を条件で間引くと簡単に発生する）
                if (controls.Count == 0 || controls[^1] is Separator)
                {
                    continue;
                }

                var separator = new Separator();
                if (separatorTheme is not null) separator.Theme = separatorTheme;
                controls.Add(separator);
                continue;
            }

            controls.Add(BuildItem(entry, itemTheme, separatorTheme));
        }

        while (controls.Count > 0 && controls[^1] is Separator)
        {
            controls.RemoveAt(controls.Count - 1);
        }

        return controls;
    }

    private MenuItem BuildItem(
        ExplorerMenuEntry entry,
        ControlTheme? itemTheme,
        ControlTheme? separatorTheme)
    {
        var item = new MenuItem
        {
            Header = entry.Text,
            IsEnabled = entry.IsEnabled,
            // テンプレート側の「ショートカット表記」列がここを読む。KeyGesture ではなく文字列なのは、
            // シェルから来る表記が言語や拡張ごとに任意の文字列だから。
            Tag = entry.Shortcut,
            Icon = BuildIcon(entry),
        };

        if (itemTheme is not null)
        {
            item.Theme = itemTheme;
        }

        if (entry.IsDefault)
        {
            item.Classes.Add("defaultitem");
        }

        if (entry.Children.Count > 0)
        {
            foreach (var child in BuildItems(entry.Children, itemTheme, separatorTheme))
            {
                item.Items.Add(child);
            }

            item.SubmenuOpened += OnSubmenuOpened;
            return item;
        }

        if (entry.Invoke is { } invoke)
        {
            item.Click += (_, _) => _pending = invoke;
        }

        return item;
    }

    /// <summary>左のアイコン列に置く中身。シェル項目は画像、自前項目はアイコンフォントのグリフ。</summary>
    private static Control? BuildIcon(ExplorerMenuEntry entry)
    {
        if (entry.Image is { } image)
        {
            return new Image
            {
                Source = image,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
            };
        }

        var glyph = entry.Glyph ?? (entry.IsChecked ? "\uE73E" : null);
        if (glyph is null)
        {
            return null;
        }

        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        ApplyPopupChrome(TopLevel.GetTopLevel(this));

        if (e.NameScope.Find<Panel>("PART_HeaderPanel") is not { } panel)
        {
            return;
        }

        panel.Children.Clear();
        var buttonTheme = FindTheme(this, "ExplorerMenuIconButtonTheme");
        foreach (var entry in _headerEntries)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = entry.Glyph ?? string.Empty,
                    FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                    FontSize = 16,
                },
                IsEnabled = entry.IsEnabled,
            };
            if (buttonTheme is not null)
            {
                button.Theme = buttonTheme;
            }

            ToolTip.SetTip(button, entry.Text);
            AutomationProperties.SetName(button, entry.Text);

            var invoke = entry.Invoke;
            button.Click += (_, _) =>
            {
                _pending = invoke;
                Close();
            };
            panel.Children.Add(button);
        }
    }

    private void OnMenuClosed(object? sender, RoutedEventArgs e)
    {
        Closed -= OnMenuClosed;

        var pending = _pending;
        var session = _session;
        var afterInvoke = _afterInvoke;
        _pending = null;
        _session = null;
        _afterInvoke = null;

        // ポップアップが実際に消えてから実行する（クラス冒頭の注記を参照）。
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    if (pending is not null)
                    {
                        pending();
                        afterInvoke?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    Services.Logger.Log($"コンテキストメニュー項目の実行に失敗: {ex.Message}", Services.LogLevel.Warning);
                }
                finally
                {
                    // 項目が持つ Image はセッション所有のビットマップを指しているので、
                    // 破棄より先に手放す（このインスタンスは使い捨てで再表示しない）。
                    Items.Clear();
                    _headerEntries = [];
                    session?.Dispose();
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// メニューを載せているポップアップウィンドウ自体の見た目を整える（透明度と角丸）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 透明度を明示しないと、ポップアップは既定のままでテンプレートの
    /// <c>ExperimentalAcrylicBorder</c> が掘る先にぼかし面が無く、背後がそのまま素通しになる。
    /// アクリル無効時は None を指定して普通の不透明ウィンドウへ戻す。
    /// </para>
    /// <para>
    /// <b>角丸は DWM に切り取らせる。</b>アクリルはポップアップ<em>ウィンドウ全体</em>に掛かるので、
    /// 角丸の Border を自前で描くだけだと、その外側（四隅と、影用に余白を取った場合はその帯）に
    /// 四角いアクリルが残り「下地が枠からはみ出している」ように見える。ウィンドウ自体を
    /// 丸く切り取れば下地そのものが無くなり、影も OS が付けてくれる。
    /// </para>
    /// </remarks>
    private static void ApplyPopupChrome(TopLevel? popupRoot)
    {
        if (popupRoot is null)
        {
            return;
        }

        popupRoot.TransparencyLevelHint = Services.ThemeService.IsAcrylicEnabled
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];

        if (popupRoot.TryGetPlatformHandle()?.Handle is { } hwnd and not 0)
        {
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
    }

    /// <summary>サブメニューは別のポップアップウィンドウに載るので、そちらにも同じ設定を掛ける。</summary>
    private static void OnSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Items.Count: > 0 } item && item.Items[0] is Control child)
        {
            ApplyPopupChrome(TopLevel.GetTopLevel(child));
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);

    private static ControlTheme? FindTheme(StyledElement scope, string key)
        => scope.TryFindResource(key, out var value) && value is ControlTheme theme ? theme : null;
}
