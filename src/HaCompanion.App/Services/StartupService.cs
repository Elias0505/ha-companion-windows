// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace HaCompanion.App.Services;

/// <summary>Controls whether the app starts with Windows (per-user, no admin needed).</summary>
public interface IStartupService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);

    /// <summary>Keep an existing autostart entry pointing at the current exe (call once at startup).</summary>
    void SelfHeal();
}

/// <inheritdoc cref="IStartupService"/>
/// <remarks>
/// The registry Run key is the single source of truth (nothing is duplicated into
/// settings.json). The stored command carries <c>--autostart</c> so the app can start
/// silently into the tray instead of opening the main window on every boot.
/// </remarks>
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HaCompanion";

    /// <summary>Command-line switch that makes the app start hidden in the tray.</summary>
    public const string AutostartArg = "--autostart";

    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService> logger) => _logger = logger;

    private static string Command => $"\"{Environment.ProcessPath}\" {AutostartArg}";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            _logger.LogInformation("Autostart {State}", enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the autostart registry entry");
        }
    }

    public void SelfHeal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string current && current != Command)
            {
                // The install folder moved (e.g. Debug -> Release publish) — keep autostart working.
                key.SetValue(ValueName, Command);
                _logger.LogInformation("Autostart entry updated to the current exe path");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autostart self-heal failed");
        }
    }
}
