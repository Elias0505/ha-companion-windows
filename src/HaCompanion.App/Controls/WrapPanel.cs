// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HaCompanion.App.Controls;

/// <summary>
/// A minimal horizontal wrap panel (WinUI ships none): lays children out left→right and
/// wraps to a new row when the available width runs out. Used for the category chip bar so
/// it reflows onto more rows as the window narrows instead of scrolling sideways.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutChanged));

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutChanged));

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var max = availableSize.Width;
        var childConstraint = new Size(double.PositiveInfinity, double.PositiveInfinity);

        double rowWidth = 0, rowHeight = 0, totalWidth = 0, totalHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(childConstraint);
            var d = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + HorizontalSpacing + d.Width > max)
            {
                totalWidth = System.Math.Max(totalWidth, rowWidth);
                totalHeight += rowHeight + VerticalSpacing;
                rowWidth = 0;
                rowHeight = 0;
            }
            rowWidth += (rowWidth > 0 ? HorizontalSpacing : 0) + d.Width;
            rowHeight = System.Math.Max(rowHeight, d.Height);
        }
        totalWidth = System.Math.Max(totalWidth, rowWidth);
        totalHeight += rowHeight;

        return new Size(
            double.IsInfinity(max) ? totalWidth : System.Math.Min(totalWidth, max),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var max = finalSize.Width;
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + d.Width > max)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }
            if (x > 0)
                x += HorizontalSpacing;
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width;
            rowHeight = System.Math.Max(rowHeight, d.Height);
        }
        return finalSize;
    }
}
