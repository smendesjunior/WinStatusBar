using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;

public class AiStudioUsageTextTests
{
    [Fact] public void ReadsTabSeparatedRateLimitTable()
    {
        const string page = "Rate limits\nFree tier\nModel\tCategory\tRPM\tTPM\tRPD\n" +
            "Gemini 2.5 Flash\tText-out models\t2 / 10\t1.2K / 250K\t37 / 250\n" +
            "Gemini 2.5 Flash-Lite\tText-out models\t0 / 15\t0 / 250K\t1 / 1K\n";
        var u = AiStudioUsageText.Parse(page)!;
        Assert.Equal("Free tier", u.Tier);
        Assert.Equal(2, u.Models.Count);
        var flash = u.Models[0];
        Assert.Equal("Gemini 2.5 Flash", flash.Model);
        Assert.Equal((2, 10), (flash.RpmUsed, flash.RpmLimit));
        Assert.Equal((1200, 250_000), (flash.TpmUsed, flash.TpmLimit));
        Assert.Equal((37, 250), (flash.RpdUsed, flash.RpdLimit));
        Assert.Equal(14.8, flash.DailyPercent!.Value, 1);
        Assert.Equal(1000, u.Models[1].RpdLimit);
    }

    [Fact] public void ReadsOneCellPerLineAndHeaderOrder()
    {
        const string page = "Limites de taxa\nNível sem custo\nModelo\nRPD\nRPM\nTPM\ngemini-3.5-flash-lite\n4 / 1.000\n1 / 30\n900 / 1M\n";
        var m = Assert.Single(AiStudioUsageText.Parse(page)!.Models);
        Assert.Equal("gemini-3.5-flash-lite", m.Model);
        Assert.Equal((4, 1000), (m.RpdUsed, m.RpdLimit));
        Assert.Equal((1, 30), (m.RpmUsed, m.RpmLimit));
        Assert.Equal((900, 1_000_000), (m.TpmUsed, m.TpmLimit));
    }

    [Fact] public void ReadsTheRealAiStudioLayoutAndItsTimeRange()
    {
        // Trecho real da página (07/11/2026): uma célula por linha, tabulações soltas e o intervalo aplicado.
        const string page = "Limite de taxa da API Gemini\nNível gratuito\nIntervalo de tempo\n28 dias\nLimites de taxa por modelo\nModelo\n\t\nCategoria\n\t\nRPM\n\t\nTPM\n\t\nRPD\n\tGráficos\n\n" +
            "Gemini 3.5 Flash Lite\n\tModelos de saída de texto\t\n7 / 15\n\t\n118.37K / 250K\n\t\n19 / 500\n\t\nbar_chart\n\n\n" +
            "Gemini 3.8 Flash\n\tModelos de saída de texto\t\n1 / 5\n\t\n16.29K / 250K\n\t\n4 / 20\n\t\nbar_chart\n";
        var u = AiStudioUsageText.Parse(page)!;
        Assert.Equal("Nível gratuito", u.Tier);
        Assert.Equal("28 dias", u.Period);
        Assert.False(AiStudioUsage.IsDaily(u.Period));
        Assert.True(AiStudioUsage.IsDaily("1 dia"));
        Assert.Equal(2, u.Models.Count);
        Assert.Equal((7, 15), (u.Models[0].RpmUsed, u.Models[0].RpmLimit));
        Assert.Equal((118_370, 250_000), (u.Models[0].TpmUsed, u.Models[0].TpmLimit));
        Assert.Equal((19, 500), (u.Models[0].RpdUsed, u.Models[0].RpdLimit));
        Assert.Equal((4, 20), (u.Models[1].RpdUsed, u.Models[1].RpdLimit));
    }

    [Fact] public void IgnoresPagesWithoutTheTable()
    {
        Assert.Null(AiStudioUsageText.Parse("Faça login para continuar"));
        Assert.Null(AiStudioUsageText.Parse(""));
    }

    [Theory]
    [InlineData("1K", 1000)] [InlineData("2.5M", 2_500_000)] [InlineData("1.000", 1000)] [InlineData("250", 250)] [InlineData("1,5K", 1500)]
    public void ParsesCompactNumbers(string text, long expected) => Assert.Equal(expected, AiStudioUsageText.Number(text));
}
