// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using HaCompanion.App.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace HaCompanion.App.Services;

/// <inheritdoc cref="IQuickPanelController"/>
/// <remarks>
/// Lazily creates a single <see cref="QuickPanelWindow"/> and forwards show/hide.
/// The window owns its own open-state, animation and focus-loss dismissal.
/// </remarks>
public sealed class QuickPanelController : IQuickPanelController
{
    private readonly IServiceProvider _services;
    private QuickPanelWindow? _window;

    public QuickPanelController(IServiceProvider services) => _services = services;

    private QuickPanelWindow Window =>
        _window ??= new QuickPanelWindow(_services.GetRequiredService<QuickPanelViewModel>());

    public void Toggle() => Window.Toggle();

    public void Show() => Window.ShowAnimated();

    public void Hide() => _window?.HideAnimated();
}
