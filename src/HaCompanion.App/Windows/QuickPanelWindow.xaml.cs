// SPDX-License-Identifier: AGPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using HaCompanion.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace HaCompanion.App.Windows;

/// <summary>
/// The Win+Ctrl+H quick panel: a borderless, always-on-top overlay pinned to the
/// right edge of the work area that slides in/out with a Composition animation,
/// dismisses on focus loss or Esc, and hosts the editable pinned-tile layout.
/// </summary>
public sealed partial class QuickPanelWindow : Window
{
    private const int PanelWidthDip = 400;
    private const float SlideDistance = PanelWidthDip + 40f;

    public QuickPanelViewModel ViewModel { get; }

    private readonly IntPtr _hwnd;
    private bool _isOpen;

    public QuickPanelWindow(QuickPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        RootGrid.DataContext = viewModel;

        Title = "HA Companion — Quick Panel";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);

        // Animate via Translation (layout-safe), starting offscreen to avoid a first-open flash.
        ElementCompositionPreview.SetIsTranslationEnabled(RootGrid, true);
        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        visual.Properties.InsertVector3("Translation", new Vector3(SlideDistance, 0, 0));
        visual.Opacity = 0f;

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        Activated += OnActivated;
        AppWindow.Hide();
    }

    public void Toggle()
    {
        if (_isOpen)
            HideAnimated();
        else
            ShowAnimated();
    }

    public void ShowAnimated()
    {
        Reposition();

        // Reset to the offscreen start state before showing.
        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        visual.Properties.InsertVector3("Translation", new Vector3(SlideDistance, 0, 0));
        visual.Opacity = 0f;

        AppWindow.Show();
        Activate();
        _isOpen = true;
        AnimateIn();
        _ = ViewModel.EnsureDashboardsAsync();

        // Put focus inside the panel so Esc + Deactivated dismissal work immediately.
        if (FocusManager.FindFirstFocusableElement(RootGrid) is Control focusable)
            focusable.Focus(FocusState.Programmatic);
    }

    public void HideAnimated()
    {
        if (!_isOpen)
            return;
        _isOpen = false;

        // Leaving the panel also leaves edit mode (layout is already persisted live).
        ViewModel.Catalog.IsEditing = false;
        UpdateEditIcon();

        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        var comp = visual.Compositor;
        var batch = comp.CreateScopedBatch(CompositionBatchTypes.Animation);

        var slide = comp.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(1f, SlideDistance);
        slide.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Translation.X", slide);

        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Opacity", fade);

        batch.End();
        batch.Completed += (_, _) => AppWindow.Hide();
    }

    private void AnimateIn()
    {
        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var slide = comp.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, SlideDistance);
        slide.InsertKeyFrame(1f, 0f, ease);
        slide.Duration = TimeSpan.FromMilliseconds(280);
        visual.StartAnimation("Translation.X", slide);

        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(280);
        visual.StartAnimation("Opacity", fade);
    }

    private void Reposition()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)(PanelWidthDip * scale);
        var x = work.X + work.Width - width;
        AppWindow.MoveAndResize(new RectInt32(x, work.Y, width, work.Height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _isOpen)
            HideAnimated();
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HideAnimated();
    }

    // ----- edit mode -----

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Catalog.IsEditing = !ViewModel.Catalog.IsEditing;
        UpdateEditIcon();
    }

    private void UpdateEditIcon() =>
        EditIcon.Glyph = ViewModel.Catalog.IsEditing ? "" : ""; // check / pencil

    private void AddTileButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ViewModel.Catalog.FilterCandidates(string.Empty);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.Catalog.FilterCandidates(SearchBox.Text);

    private void SearchResult_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntityTileViewModel tile)
        {
            ViewModel.Catalog.TogglePin(tile);
            ViewModel.Catalog.FilterCandidates(SearchBox.Text);
        }
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            ViewModel.Catalog.TogglePin(tile);
    }

    private async void Pinned_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.Catalog.IsEditing)
            return; // in edit mode taps are for arranging, not switching
        if (e.ClickedItem is EntityTileViewModel tile)
            await tile.ToggleCommand.ExecuteAsync(null);
    }

    private async void Dashboard_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntityTileViewModel tile)
            await tile.ToggleCommand.ExecuteAsync(null);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
