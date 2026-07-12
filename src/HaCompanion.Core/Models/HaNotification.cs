// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Models;

/// <summary>A Home Assistant persistent notification (as pushed over the WebSocket).</summary>
public sealed record HaNotification(string NotificationId, string Title, string Message);
