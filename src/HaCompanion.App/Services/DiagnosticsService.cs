// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.IO;
using System.Text;
using HaCompanion.Core.Diagnostics;
using HaCompanion.Core.Services;

namespace HaCompanion.App.Services;

/// <summary>
/// Builds a plain-text diagnostics report the user can attach to a bug report:
/// version, system, connection state, redacted settings and the log tails.
/// The token and the webhook id NEVER appear in the output.
/// </summary>
public interface IDiagnosticsService
{
    string BuildReport();

    /// <summary>Folder holding app.log / crash.log (for the "open log folder" button).</summary>
    string LogFolderPath { get; }
}

/// <inheritdoc cref="IDiagnosticsService"/>
public sealed class DiagnosticsService : IDiagnosticsService
{
    private const int TailBytes = 64 * 1024;

    private readonly ISettingsStore _settings;
    private readonly IHaConnection _connection;
    private readonly ISensorPublisher _sensors;
    private readonly LocalizationService _loc;

    public DiagnosticsService(ISettingsStore settings, IHaConnection connection,
        ISensorPublisher sensors, LocalizationService loc)
    {
        _settings = settings;
        _connection = connection;
        _sensors = sensors;
        _loc = loc;
    }

    public string LogFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaCompanion");

    public string BuildReport()
    {
        var s = _settings.Load();
        var sb = new StringBuilder();

        sb.AppendLine("HA Companion diagnostics report");
        sb.AppendLine("Generated: " + DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        sb.AppendLine("Note: the access token and webhook id are redacted. Review the base URL");
        sb.AppendLine("before sharing if you consider it sensitive.");
        sb.AppendLine();

        sb.AppendLine("[app]");
        sb.AppendLine("version = " + (typeof(DiagnosticsService).Assembly.GetName().Version?.ToString(3) ?? "?"));
        sb.AppendLine("os = " + Environment.OSVersion.VersionString);
        sb.AppendLine("arch = " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        sb.AppendLine("language = " + _loc.CurrentLanguage);
        sb.AppendLine();

        sb.AppendLine("[connection]");
        sb.AppendLine("status = " + _connection.Status);
        sb.AppendLine("entities = " + _connection.Entities.Count);
        sb.AppendLine();

        sb.AppendLine("[settings]");
        sb.AppendLine("base_url = " + s.BaseUrl);
        sb.AppendLine("token = " + DiagnosticsRedactor.Redacted);
        sb.AppendLine("ignore_certificate_errors = " + s.IgnoreCertificateErrors);
        sb.AppendLine("hotkey = " + s.Hotkey);
        sb.AppendLine("autostart_language = " + s.Language);
        sb.AppendLine("report_sensors = " + s.ReportSensors);
        sb.AppendLine("mobile_app_device_id = " + s.MobileAppDeviceId);
        sb.AppendLine("mobile_app_webhook_id = "
            + (string.IsNullOrEmpty(s.MobileAppWebhookId) ? "(none)" : DiagnosticsRedactor.Redacted));
        sb.AppendLine("idle_threshold_minutes = " + s.IdleSensorThresholdMinutes);
        sb.AppendLine("allow_commands = lock:" + s.AllowCmdLock + " monitor_off:" + s.AllowCmdMonitorOff
            + " volume:" + s.AllowCmdVolume + " sleep:" + s.AllowCmdSleep
            + " shutdown:" + s.AllowCmdShutdown + " launch:" + s.AllowCmdLaunch);
        sb.AppendLine("sensor_status = " + _sensors.StatusText);
        sb.AppendLine();

        AppendLog(sb, "app.log");
        AppendLog(sb, "crash.log");

        // Belt and braces: even if a secret ever leaked into a log line, it must not
        // survive into the report.
        return DiagnosticsRedactor.Redact(sb.ToString(), new[] { s.Token, s.MobileAppWebhookId });
    }

    private void AppendLog(StringBuilder sb, string fileName)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"[{fileName} — last {TailBytes / 1024} KB]");
        var tail = DiagnosticsRedactor.TailFile(Path.Combine(LogFolderPath, fileName), TailBytes);
        sb.AppendLine(string.IsNullOrEmpty(tail) ? "(empty)" : tail);
        sb.AppendLine();
    }
}
