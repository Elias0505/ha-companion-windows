// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.System;

namespace HaCompanion.App.Controls;

/// <summary>
/// Replaces the built-in mouse-wheel scroll animation of a ScrollViewer with a strictly
/// monotonic exponential glide. The stock animation restarts on every wheel tick and can
/// visibly overshoot/settle backwards for a frame or two; an exponential approach toward
/// the target can never move against the scroll direction, so the judder is gone by design.
/// Touch/pen panning, scrollbar drags and Ctrl+wheel are untouched.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Pixels per wheel notch — measured to match the stock ScrollViewer distance.</summary>
    private const double StepPx = 210;

    /// <summary>Fraction of the remaining distance covered per rendered frame.</summary>
    private const double Approach = 0.28;

    public static void Attach(ScrollViewer viewer) => _ = new Driver(viewer);

    private sealed class Driver
    {
        private readonly ScrollViewer _viewer;
        private double _target;
        // Tracked locally instead of re-reading VerticalOffset per frame: the property lags
        // ChangeView by a frame, and computing the next step from the stale value moves the
        // content briefly backwards — the exact judder this class exists to remove.
        private double _current;
        private bool _animating;
        private EventHandler<object>? _tick;

        public Driver(ScrollViewer viewer)
        {
            _viewer = viewer;
            // Subscribe on the content, not the viewer: the event bubbles through the content
            // first, so marking it handled there stops the stock wheel processing entirely.
            if (viewer.Content is UIElement content)
                content.PointerWheelChanged += OnWheel;
            else
                viewer.PointerWheelChanged += OnWheel;
            viewer.Unloaded += (_, _) => Stop();
        }

        private void OnWheel(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(_viewer).Properties;
            if (props.IsHorizontalMouseWheel)
                return;
            if ((e.KeyModifiers & VirtualKeyModifiers.Control) != 0)
                return; // leave Ctrl+wheel (zoom gestures) to the system
            if (_viewer.ScrollableHeight <= 0)
                return;

            if (!_animating)
            {
                _current = _viewer.VerticalOffset;
                _target = _current;
            }
            _target = Math.Clamp(_target - props.MouseWheelDelta / 120.0 * StepPx, 0, _viewer.ScrollableHeight);
            e.Handled = true;
            Start();
        }

        private void Start()
        {
            if (_animating)
                return;
            _animating = true;
            _tick = (_, _) => Step();
            CompositionTarget.Rendering += _tick;
        }

        private void Step()
        {
            // Content may have re-laid-out mid-glide (window resize, tiles added).
            _target = Math.Clamp(_target, 0, _viewer.ScrollableHeight);
            _current = Math.Min(_current, _viewer.ScrollableHeight);
            var next = _current + (_target - _current) * Approach;
            if (Math.Abs(_target - next) < 0.5)
            {
                _current = _target;
                _viewer.ChangeView(null, _target, null, disableAnimation: true);
                Stop();
                return;
            }
            _current = next;
            _viewer.ChangeView(null, next, null, disableAnimation: true);
        }

        private void Stop()
        {
            if (!_animating)
                return;
            _animating = false;
            if (_tick is not null)
            {
                CompositionTarget.Rendering -= _tick;
                _tick = null;
            }
        }
    }
}
