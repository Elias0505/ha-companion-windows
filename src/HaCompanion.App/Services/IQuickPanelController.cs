// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.Services;

/// <summary>Controls the slide-in quick-action panel (the Win+Ctrl+H flyout).</summary>
public interface IQuickPanelController
{
    /// <summary>Show the panel if hidden, hide it if shown.</summary>
    void Toggle();

    void Show();

    void Hide();

    /// <summary>Show/resize the panel at the current width setting as a live preview (auto-hides shortly after).</summary>
    void PreviewWidth();

    /// <summary>
    /// Drop the embedded WebView so the next open rebuilds it. Call after the HA URL,
    /// token or certificate setting changed — otherwise the existing WebView keeps
    /// enforcing the OLD origin/certificate rules and carries the OLD token until restart.
    /// </summary>
    void ResetWebView();

    /// <summary>
    /// Build the panel window and pre-load its start view (incl. the embedded dashboard)
    /// invisibly so the first hotkey press slides in an already-rendered panel.
    /// </summary>
    void Prewarm();
}
