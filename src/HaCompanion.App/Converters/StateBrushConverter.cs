// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HaCompanion.App.Converters;

/// <summary>Maps an entity's on/off state to a tile accent brush.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OnBrush = new(Color.FromArgb(255, 10, 132, 255));
    private static readonly SolidColorBrush OffBrush = new(Color.FromArgb(160, 140, 140, 140));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? OnBrush : OffBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
