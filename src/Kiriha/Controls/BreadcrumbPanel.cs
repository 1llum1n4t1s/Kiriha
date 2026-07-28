using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Kiriha.Controls;

/// <summary>
/// アドレスバーのパンくずを横一列に並べ、幅が足りないときは<b>先頭側（浅い階層）から畳む</b>パネル。
///
/// 以前は横スクロールビューに左詰めで並べていたため、パスが長くなると末尾＝いま開いている
/// フォルダー側が右へはみ出して押せなくなっていた（スクロールバーも Hidden なので、
/// はみ出していること自体が画面から分からなかった）。エクスプローラーと同じく、
/// 削るのは浅い側にして現在フォルダーは必ず残す。
///
/// 畳んだ数は先祖の <see cref="ItemsControl"/> へ添付プロパティで載せる。パネルは
/// <c>ItemsPanelTemplate</c> の中にいて独自の名前スコープを持つため、XAML から直接
/// 名前で参照できない。ItemsControl 側に載せておけば、兄弟の「畳んだ分を開く」ボタンが
/// <c>{Binding #Crumbs.(c:BreadcrumbPanel.HasOverflow)}</c> で拾える。
///
/// 幅の再計算が振動しないのは、ボタンが出ると使える幅が減る方向にしか動かないため。
/// ボタンを出した状態（＝狭いほう）で収まるなら、ボタンを消して広くなっても必ず収まる。
/// </summary>
public sealed class BreadcrumbPanel : Panel
{
    /// <summary>畳まれたパンくずがあるか（「畳んだ分を開く」ボタンの表示切り替え用）。</summary>
    public static readonly AttachedProperty<bool> HasOverflowProperty =
        AvaloniaProperty.RegisterAttached<BreadcrumbPanel, Control, bool>("HasOverflow");

    /// <summary>畳まれた個数。先頭からこの数だけが隠れている。</summary>
    public static readonly AttachedProperty<int> OverflowCountProperty =
        AvaloniaProperty.RegisterAttached<BreadcrumbPanel, Control, int>("OverflowCount");

    public static bool GetHasOverflow(Control element) => element.GetValue(HasOverflowProperty);

    public static void SetHasOverflow(Control element, bool value) => element.SetValue(HasOverflowProperty, value);

    public static int GetOverflowCount(Control element) => element.GetValue(OverflowCountProperty);

    public static void SetOverflowCount(Control element, int value) => element.SetValue(OverflowCountProperty, value);

    /// <summary>最初に表示するパンくずの位置。これより前は畳む。</summary>
    private int _firstVisible;

    public BreadcrumbPanel()
    {
        // 畳んだ要素は幅 0 で配置するだけなので、はみ出しを描かせないために切り取る。
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (var child in Children)
        {
            // 畳んだ要素は幅 0 で配置する。切り取りを付けないと、配置の大きさに関係なく
            // 中身（ボタンの文字）がそのまま描かれて先頭に重なってしまう（実機で確認済み）。
            // IsVisible を使わないのは、測定中に書き換えるとレイアウトのやり直しを誘発するため。
            child.ClipToBounds = true;
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var used = 0.0;
        var first = 0;
        if (double.IsFinite(availableSize.Width))
        {
            // 末尾から詰めていき、入らなくなったところより前を畳む。
            for (var i = Children.Count - 1; i >= 0; i--)
            {
                var width = Children[i].DesiredSize.Width;
                // 末尾（現在フォルダー）だけは、単独で幅を超えていても必ず残す。
                if (i < Children.Count - 1 && used + width > availableSize.Width)
                {
                    first = i + 1;
                    break;
                }

                used += width;
            }
        }
        else
        {
            foreach (var child in Children)
            {
                used += child.DesiredSize.Width;
            }
        }

        _firstVisible = first;
        PublishOverflow(first);
        return new Size(
            double.IsFinite(availableSize.Width) ? Math.Min(used, availableSize.Width) : used,
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i < _firstVisible)
            {
                // 幅 0 で置くと当たり判定も描画も無くなるので、IsVisible を触らずに畳める
                // （測定中に IsVisible を書き換えるとレイアウトのやり直しを誘発するため避ける）。
                child.Arrange(default);
                continue;
            }

            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    /// <summary>畳んだ個数を先祖の ItemsControl へ載せる（値が変わったときだけ書く）。</summary>
    private void PublishOverflow(int count)
    {
        if (this.FindAncestorOfType<ItemsControl>() is not { } owner)
        {
            return;
        }

        if (GetOverflowCount(owner) != count)
        {
            SetOverflowCount(owner, count);
        }

        var hasOverflow = count > 0;
        if (GetHasOverflow(owner) != hasOverflow)
        {
            SetHasOverflow(owner, hasOverflow);
        }
    }
}
