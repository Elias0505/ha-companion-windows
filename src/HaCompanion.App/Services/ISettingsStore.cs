// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Models;

namespace HaCompanion.App.Services;

/// <summary>Loads/saves <see cref="AppSettings"/>; the token is stored encrypted (DPAPI).</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
