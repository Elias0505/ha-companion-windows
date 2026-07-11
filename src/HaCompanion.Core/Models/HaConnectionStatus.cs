// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Models;

/// <summary>
/// Lifecycle state of the connection to a Home Assistant instance.
/// </summary>
public enum HaConnectionStatus
{
    Disconnected,
    Connecting,
    Authenticating,
    AuthFailed,
    Connected,
    Reconnecting,
}
