using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class MicrosoftCreditsTextTests
{
    [Fact]
    public void ReadsRemainingTotalAndUsed()
    {
        var c = MicrosoftCreditsText.Parse("Alterar plano de assinatura\nSaldo de crédito de IA\nSua assinatura inclui créditos de IA que são recarregados no início de cada mês\nTotal restante\n42\n / 60 créditos\n18 usado\nAcompanhe o uso")!;
        Assert.Equal(42, c.Remaining);
        Assert.Equal(60, c.Total);
        Assert.Equal(18, c.Used);
    }

    [Fact]
    public void ReadsPlanFromTheSubscriptionsList()
    {
        var c = MicrosoftCreditsText.Parse("Assinaturas\nGerenciar: Microsoft 365 Family\nÉ renovada em 20/11/2026")!;
        Assert.Equal("Microsoft 365 Family", c.Plan);
        Assert.Null(c.Total);
    }

    [Fact]
    public void PageWithoutCreditsIsNull() => Assert.Null(MicrosoftCreditsText.Parse("Entrar\nConta da Microsoft"));
}
