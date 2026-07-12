// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>One row on the shortcuts page: an entity plus its global hotkey.</summary>
public sealed class ShortcutItemViewModel
{
    public required ShortcutBinding Binding { get; init; }

    public string Hotkey => Binding.Hotkey;

    public string EntityId => Binding.EntityId;

    public required string FriendlyName { get; init; }

    public required string IconGlyph { get; init; }

    /// <summary>False when the hotkey could not be registered (combo already taken).</summary>
    public required bool IsActive { get; init; }
}

/// <summary>Backing view model for the shortcuts page: entity search + hotkey assignment.</summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly IShortcutStore _store;
    private readonly IShortcutManager _manager;
    private readonly EntityCatalogViewModel _catalog;
    private readonly MdiIconProvider _icons;

    public ObservableCollection<ShortcutItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private EntityTileViewModel? _selectedTile;

    [ObservableProperty]
    private string? _capturedCombo;

    [ObservableProperty]
    private bool _hasItems;

    public bool CanAdd => SelectedTile is not null && !string.IsNullOrEmpty(CapturedCombo);

    public ShortcutsViewModel(IShortcutStore store, IShortcutManager manager, EntityCatalogViewModel catalog, MdiIconProvider icons)
    {
        _store = store;
        _manager = manager;
        _catalog = catalog;
        _icons = icons;
        _manager.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    public IReadOnlyList<EntityTileViewModel> Search(string query) => _catalog.SearchTiles(query);

    partial void OnSelectedTileChanged(EntityTileViewModel? value) => OnPropertyChanged(nameof(CanAdd));

    partial void OnCapturedComboChanged(string? value) => OnPropertyChanged(nameof(CanAdd));

    [RelayCommand]
    private void Add()
    {
        if (SelectedTile is null || string.IsNullOrEmpty(CapturedCombo))
            return;

        // One binding per combo: re-recording an existing combo re-assigns it.
        var bindings = _store.Load()
            .Where(b => !string.Equals(b.Hotkey, CapturedCombo, StringComparison.OrdinalIgnoreCase))
            .ToList();
        bindings.Add(new ShortcutBinding(CapturedCombo, SelectedTile.EntityId));
        _store.Save(bindings);
        _manager.Reload(); // re-registers and raises Changed -> Rebuild()

        SelectedTile = null;
        CapturedCombo = null;
    }

    [RelayCommand]
    private void Remove(ShortcutItemViewModel item)
    {
        _store.Save(_store.Load().Where(b => b != item.Binding).ToList());
        _manager.Reload();
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var binding in _store.Load())
        {
            var tile = _catalog.FindTile(binding.EntityId);
            Items.Add(new ShortcutItemViewModel
            {
                Binding = binding,
                FriendlyName = tile?.FriendlyName ?? binding.EntityId,
                IconGlyph = tile?.IconGlyph ?? _icons.DomainGlyph(binding.EntityId.Split('.')[0]),
                IsActive = _manager.IsActive(binding),
            });
        }
        HasItems = Items.Count > 0;
    }
}
