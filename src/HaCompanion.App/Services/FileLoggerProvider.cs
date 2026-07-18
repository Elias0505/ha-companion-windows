// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// Minimal file sink for <see cref="ILogger"/> — without it every internal warning
/// (WebSocket drops, failed hotkey registrations, store errors) vanished, because
/// <c>AddLogging()</c> had no provider configured. Writes to
/// <c>%LOCALAPPDATA%\HaCompanion\app.log</c>, size-capped, lock-guarded, no dependencies.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxLogBytes = 1_000_000;

    private readonly string _file;
    private readonly object _sync = new();

    public FileLoggerProvider(string filePath) => _file = filePath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        // Best-effort: logging must never take the app down or block for long.
        try
        {
            lock (_sync)
            {
                if (File.Exists(_file) && new FileInfo(_file).Length > MaxLogBytes)
                    // Rotate rather than wipe: keep one previous log (app.log.1) so the context
                    // leading up to a problem survives the size cap. Home Assistant rotates its
                    // own log the same way (home-assistant.log / home-assistant.log.1).
                    File.Move(_file, _file + ".1", overwrite: true);
                File.AppendAllText(_file, line);
            }
        }
        catch
        {
            // ignore — diagnostics only
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {Level(logLevel)} {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Write(line + Environment.NewLine);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => level.ToString().ToUpperInvariant(),
        };
    }
}
