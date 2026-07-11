// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaCompanion.App.ViewModels;

/// <summary>A titled group of quick-action tiles (one Home Assistant domain).</summary>
public sealed partial class EntityGroupViewModel : ObservableObject
{
    public string Domain { get; }

    public string Header { get; }

    public string Glyph { get; }

    public ObservableCollection<EntityTileViewModel> Tiles { get; } = new();

    [ObservableProperty]
    private int _count;

    public EntityGroupViewModel(string domain, string header, string glyph)
    {
        Domain = domain;
        Header = header;
        Glyph = glyph;
    }
}
