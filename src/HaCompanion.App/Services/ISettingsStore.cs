// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Models;

namespace HaCompanion.App.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/>; the token is stored encrypted (DPAPI).
///
/// Deliberately, there is NO whole-snapshot Save: every writer goes through
/// <see cref="Update"/> (field-local, atomic) or <see cref="ReplaceOnDisk"/> (file-level,
/// locked). A public Save(snapshot) was the root of a whole bug class — a stale snapshot
/// written back could revert concurrent changes, including re-enabling a command permission
/// the user had just switched off.
/// </summary>
public interface ISettingsStore
{
    AppSettings Load();

    /// <summary>
    /// Atomically read the current settings, apply <paramref name="mutate"/> and persist —
    /// all under one lock. Use from background components that own only a field or two
    /// (e.g. the mobile_app webhook id): writing back a whole snapshot captured earlier can
    /// silently revert a concurrent change to an unrelated field (e.g. a command toggle).
    /// The callback must not call back into the store.
    /// </summary>
    void Update(Action<AppSettings> mutate);

    /// <summary>
    /// Give up any encrypted secret the store is holding on to because it could not be
    /// decrypted on this machine (see the DPAPI note in <see cref="SettingsStore"/>).
    ///
    /// Call this on the paths that deliberately REMOVE credentials — dropping them when the
    /// HA origin changes, or a factory reset. Without it those paths would write an empty
    /// token while the preserved blob was silently written back, so a secret the user asked
    /// to delete would survive on disk.
    /// </summary>
    void DiscardPreservedSecrets();

    /// <summary>
    /// Run a direct, file-level change to settings.json under the store's own lock and drop the
    /// cache afterwards. Use for the two operations that bypass the object model — config import
    /// (merges raw JSON) and factory reset (deletes the file).
    ///
    /// Without this they raced every background <see cref="Update"/>: a heartbeat write landing
    /// between the import's write and its cache drop persisted the PRE-import snapshot, which
    /// re-paired the previous host's token with the freshly imported URL — the exact leak the
    /// import's credential drop exists to prevent.
    /// </summary>
    void ReplaceOnDisk(Action mutateFile);
}
