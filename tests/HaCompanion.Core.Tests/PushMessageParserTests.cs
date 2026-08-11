// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

public class PushMessageParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Full_payload_parses_everything()
    {
        var payload = Json("""
            {
              "title": "Tor",
              "message": "Tor ist offen",
              "hass_confirm_id": "abc123",
              "data": {
                "tag": "gate",
                "actions": [
                  {"action": "close_gate", "title": "Schließen"},
                  {"action": "ignore", "title": "Ignorieren"}
                ]
              }
            }
            """);
        Assert.True(PushMessageParser.TryParse(payload, out var msg));
        Assert.Equal("Tor", msg.Title);
        Assert.Equal("Tor ist offen", msg.Message);
        Assert.Equal("abc123", msg.ConfirmId);
        Assert.Equal("gate", msg.Tag);
        Assert.Equal(2, msg.Actions.Count);
        Assert.Equal(new PushAction("close_gate", "Schließen"), msg.Actions[0]);
    }

    [Fact]
    public void Minimal_payload_needs_only_message()
    {
        Assert.True(PushMessageParser.TryParse(Json("""{"message":"hi"}"""), out var msg));
        Assert.Equal("hi", msg.Message);
        Assert.Null(msg.Title);
        Assert.Null(msg.ConfirmId);
        Assert.Null(msg.Tag);
        Assert.Empty(msg.Actions);
    }

    [Theory]
    [InlineData("""{"title":"x"}""")]      // no message
    [InlineData("""{"message":42}""")]     // wrong type
    [InlineData("""[1,2]""")]              // not an object
    public void Invalid_payloads_are_rejected(string json) =>
        Assert.False(PushMessageParser.TryParse(Json(json), out _));

    [Fact]
    public void Malformed_actions_are_skipped()
    {
        var payload = Json("""
            {"message":"m","data":{"actions":[{"action":"ok","title":"Ok"},{"nope":1},"str"]}}
            """);
        Assert.True(PushMessageParser.TryParse(payload, out var msg));
        Assert.Single(msg.Actions);
    }

    [Theory]
    [InlineData("""{"message":"m","data":{"level":55}}""", "level", "55")]
    [InlineData("""{"message":"m","data":{"app":"spotify"}}""", "app", "spotify")]
    [InlineData("""{"message":"m","data":{}}""", "level", null)]
    [InlineData("""{"message":"m"}""", "level", null)]
    public void DataString_reads_string_and_number_params(string json, string field, string? expected) =>
        Assert.Equal(expected, PushMessageParser.DataString(Json(json), field));

    [Fact]
    public void Oversized_fields_are_capped()
    {
        // These values are retained by the dedup set and the history list, which bound
        // their COUNT but not their size — an unbounded field would let a hostile sender
        // pin megabytes in memory and bloat the diagnostics report.
        var huge = new string('a', 100_000);
        var payload = Json("{\"message\":\"" + huge + "\",\"title\":\"" + huge
            + "\",\"hass_confirm_id\":\"" + huge + "\",\"data\":{\"app\":\"" + huge + "\"}}");

        Assert.True(PushMessageParser.TryParse(payload, out var msg));
        Assert.Equal(4096, msg.Message.Length);
        Assert.Equal(512, msg.Title!.Length);
        Assert.Equal(512, msg.ConfirmId!.Length);
        Assert.Equal(512, PushMessageParser.DataString(payload, "app")!.Length);
    }
}
