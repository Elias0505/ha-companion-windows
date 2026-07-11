// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        TokenBox.Password = ViewModel.Token; // one-time init; updates flow via PasswordChanged
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Token = TokenBox.Password;

    public bool IsNotBusy(bool isBusy) => !isBusy;
}
