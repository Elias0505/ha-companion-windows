// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>A selectable UI language.</summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// Runtime UI localization. XAML binds to the string indexer
/// (<c>{Binding [Key], Source={StaticResource Loc}}</c>); changing the language
/// raises the indexer change so all bindings refresh live, plus a
/// <see cref="LanguageChanged"/> event for code that caches strings.
/// </summary>
public sealed partial class LocalizationService : ObservableObject
{
    private readonly Dictionary<string, Dictionary<string, string>> _all;
    private Dictionary<string, string> _current = new();
    private Dictionary<string, string> _fallback = new();

    public IReadOnlyList<LanguageOption> Languages { get; } = new List<LanguageOption>
    {
        new("en", "English"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("zh", "中文"),
        new("hi", "हिन्दी"),
    };

    public string CurrentLanguage { get; private set; } = "en";

    public event EventHandler? LanguageChanged;

    public LocalizationService(ILogger<LocalizationService> logger)
    {
        _all = Load(logger);
        _fallback = _all.TryGetValue("en", out var en) ? en : new();
    }

    /// <summary>Localized string for <paramref name="key"/> (falls back to English, then the key).</summary>
    public string this[string key] =>
        (_current.TryGetValue(key, out var value) ? value : null)
        ?? (_fallback.TryGetValue(key, out var fallback) ? fallback : key);

    /// <summary>Localized group header for a Home Assistant domain (e.g. "light" → "Lights").</summary>
    public string Group(string domain) => this["Grp_" + domain];

    public void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !_all.ContainsKey(code))
            code = "en";
        CurrentLanguage = code;
        _current = _all[code];
        OnPropertyChanged("Item[]"); // refresh every {Binding [Key]}
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, Dictionary<string, string>> Load(ILogger logger)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "i18n", "strings.json");
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
                   ?? new Dictionary<string, Dictionary<string, string>>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load translations; UI will use string keys");
            return new Dictionary<string, Dictionary<string, string>>();
        }
    }
}
