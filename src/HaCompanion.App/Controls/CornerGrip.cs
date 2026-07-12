// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Controls;

/// <summary>
/// The tile resize handle shown in edit mode: a small round grip on the tile's bottom-right
/// corner with the diagonal resize cursor. Dragging it resizes the tile freely (snapping to
/// grid cells); a plain click cycles the size presets. Subclassing a panel is required to
/// set <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/>.
/// </summary>
public sealed partial class CornerGrip : Grid
{
    public CornerGrip() =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
}
