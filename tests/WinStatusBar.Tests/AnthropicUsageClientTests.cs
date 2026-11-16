using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class AnthropicUsageClientTests
{
    private static Dictionary<DateTime, ApiUsageDay> Days() => new()
    {
        [new DateTime(2026, 11, 14)] = new(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), 0, 0, 0),
        [new DateTime(2026, 11, 15)] = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), 0, 0, 0),
    };

    [Fact]
    public void SumsAllInputTokenKindsAndOutput()
    {
        var days = Days();
        AnthropicUsageClient.Merge(days, JsonNode.Parse("""
        {"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[
          {"uncached_input_tokens":100,"cache_read_input_tokens":20,"cache_creation":{"ephemeral_1h_input_tokens":3,"ephemeral_5m_input_tokens":7},"output_tokens":50},
          {"uncached_input_tokens":10,"output_tokens":5}]}],"has_more":false,"next_page":null}
        """), cost: false);
        var day = days[new DateTime(2026, 11, 15)];
        Assert.Equal(140, day.Input);
        Assert.Equal(55, day.Output);
        Assert.Equal(0, days[new DateTime(2026, 11, 14)].Input);
    }

    [Fact]
    public void CostAmountsAreCentsAsDecimalStrings()
    {
        var days = Days();
        AnthropicUsageClient.Merge(days, JsonNode.Parse("""
        {"data":[{"starting_at":"2026-11-14T00:00:00Z","results":[{"currency":"USD","amount":"1234.5"},{"currency":"USD","amount":"65.5"}]},
                 {"starting_at":"2026-10-01T00:00:00Z","results":[{"currency":"USD","amount":"999"}]}]}
        """), cost: true);
        Assert.Equal(13.00m, days[new DateTime(2026, 11, 14)].Cost);
        Assert.Equal(0m, days[new DateTime(2026, 11, 15)].Cost); // dia fora do período não entra
    }
}
