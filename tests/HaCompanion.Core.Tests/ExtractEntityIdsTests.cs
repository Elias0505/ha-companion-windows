// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.Services;
using Xunit;

namespace HaCompanion.Core.Tests;

public class ExtractEntityIdsTests
{
    private static List<string> Extract(string json)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        HaConnection.ExtractEntityIds(doc.RootElement, ids, seen);
        return ids;
    }

    [Fact]
    public void Finds_entity_and_entity_id_recursively_and_dedupes()
    {
        var ids = Extract("""
            {
              "views": [
                { "cards": [
                    { "entity": "light.kitchen" },
                    { "entity_id": "switch.tv" },
                    { "entities": ["light.kitchen", "sensor.power", { "entity": "climate.living" }] }
                ] }
              ]
            }
            """);
        Assert.Equal(new[] { "light.kitchen", "switch.tv", "sensor.power", "climate.living" }, ids);
    }

    [Fact]
    public void Ignores_non_entity_strings()
    {
        var ids = Extract("""{ "entities": ["not-an-entity", "two.dots.here", "ok.fine"] }""");
        Assert.Equal(new[] { "ok.fine" }, ids);
    }
}
