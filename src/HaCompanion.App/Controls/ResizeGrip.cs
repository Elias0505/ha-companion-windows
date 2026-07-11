// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Controls;

/// <summary>
/// A thin, hit-testable strip that shows the horizontal resize cursor on hover. Used as the
/// drag handle on the quick panel's inner (left) edge so the width can be changed by dragging
/// instead of only through Settings. Deriving from a panel lets us set <see cref="Microsoft
/// .UI.Xaml.UIElement.ProtectedCursor"/>, which is only settable from a subclass.
/// </summary>
public sealed class ResizeGrip : Grid
{
    public ResizeGrip() =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}
