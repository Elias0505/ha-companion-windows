// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Rest;
using HaCompanion.Core.Services;
using HaCompanion.Core.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace HaCompanion.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Home Assistant core clients and the <see cref="IHaConnection"/>
    /// facade as singletons.
    /// </summary>
    public static IServiceCollection AddHaCompanionCore(this IServiceCollection services)
    {
        services.AddSingleton<HaRestClient>();
        services.AddSingleton<HaWebSocketClient>();
        services.AddSingleton<IHaConnection, HaConnection>();
        return services;
    }
}
