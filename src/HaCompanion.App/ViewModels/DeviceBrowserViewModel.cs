// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>
/// One chip in the device-browser filter bar. A null <see cref="Domain"/> is the
/// "All" chip; every other chip mirrors one live <see cref="EntityGroupViewModel"/>.
/// </summary>
public sealed partial class CategoryChipViewModel : ObservableObject
{
    /// <summary>HA domain this chip filters to, or null for the "All" chip.</summary>
    public string? Domain { get; }

    /// <summary>MDI glyph; empty for the "All" chip (icon is then hidden).</summary>
    public string Glyph { get; }

    /// <summary>False for the "All" chip so its (absent) icon collapses.</summary>
    public bool HasIcon => Glyph.Length > 0;

    /// <summary>The backing group, or null for the "All" chip.</summary>
    public EntityGroupViewModel? Group { get; }

    /// <summary>Localized category title; tracks the group's header when the language changes.</summary>
    [ObservableProperty]
    private string _header;

    [ObservableProperty]
    private bool _isSelected;

    public CategoryChipViewModel(string? domain, string header, string glyph, EntityGroupViewModel? group)
    {
        Domain = domain;
        _header = header;
        Glyph = glyph;
        Group = group;
    }
}

/// <summary>
/// A per-page filter over the shared <see cref="EntityCatalogViewModel"/>: a chip bar
/// (an "All" chip plus one chip per domain currently present) and the groups the selected
/// chip shows. Everything is derived live from the catalog, so it adapts to <em>any</em>
/// Home Assistant setup — chips appear and disappear as whole domains come and go, with no
/// hardcoded categories. State lives here (not on the singleton catalog) so each tab filters
/// independently.
/// </summary>
public sealed partial class DeviceBrowserViewModel : ObservableObject
{
    private readonly EntityCatalogViewModel _catalog;
    private readonly LocalizationService _loc;
    private readonly CategoryChipViewModel _allChip;

    /// <summary>Filter chips: "All" first, then one per domain (in the catalog's order).</summary>
    public ObservableCollection<CategoryChipViewModel> Categories { get; } = new();

    /// <summary>Groups shown for the current selection (all groups, or the single picked one).</summary>
    public ObservableCollection<EntityGroupViewModel> VisibleGroups { get; } = new();

    [ObservableProperty]
    private CategoryChipViewModel _selectedCategory;

    public DeviceBrowserViewModel(EntityCatalogViewModel catalog, LocalizationService loc)
    {
        _catalog = catalog;
        _loc = loc;

        _allChip = new CategoryChipViewModel(null, loc["Cat_All"], string.Empty, null) { IsSelected = true };
        _selectedCategory = _allChip;
        Categories.Add(_allChip);
        foreach (var group in _catalog.Groups)
            Categories.Add(ChipFor(group));
        RebuildVisible();

        _catalog.Groups.CollectionChanged += OnGroupsChanged;
        _loc.LanguageChanged += (_, _) => _allChip.Header = _loc["Cat_All"];
    }

    private static CategoryChipViewModel ChipFor(EntityGroupViewModel group)
    {
        var chip = new CategoryChipViewModel(group.Domain, group.Header, group.Glyph, group);
        // Keep the chip label in sync with the (localized, live) group header.
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EntityGroupViewModel.Header))
                chip.Header = group.Header;
        };
        return chip;
    }

    // A whole domain appeared or vanished — mirror it in the chip list.
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var present = _catalog.Groups.ToList();

        for (var i = Categories.Count - 1; i >= 1; i--)
        {
            if (Categories[i].Group is { } g && !present.Contains(g))
            {
                if (Categories[i] == SelectedCategory)
                    SelectCategory(_allChip);
                Categories.RemoveAt(i);
            }
        }

        for (var gi = 0; gi < present.Count; gi++)
        {
            var group = present[gi];
            if (!Categories.Any(c => c.Group == group))
                Categories.Insert(Math.Min(gi + 1, Categories.Count), ChipFor(group));
        }

        RebuildVisible();
    }

    /// <summary>Make <paramref name="chip"/> the active filter (single-select).</summary>
    public void SelectCategory(CategoryChipViewModel chip)
    {
        foreach (var c in Categories)
            c.IsSelected = c == chip;
        SelectedCategory = chip;
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        VisibleGroups.Clear();
        if (SelectedCategory.Domain is null)
            foreach (var group in _catalog.Groups)
                VisibleGroups.Add(group);
        else if (SelectedCategory.Group is { } group)
            VisibleGroups.Add(group);
    }
}
