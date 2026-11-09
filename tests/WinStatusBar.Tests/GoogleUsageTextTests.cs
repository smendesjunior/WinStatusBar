using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class GoogleUsageTextTests
{
    [Fact]
    public void ReadsPlanCurrentAndWeeklyFromThePortuguesePage()
    {
        const string page = "Limites de uso\nPLUS\nOs limites do seu plano determinam quanto você pode usar o Gemini.\nAtualizado agora\nUso atual\nQuantidade usada: 12%\n16:28: o limite de uso é redefinido\nLimite semanal\n10 de nov., 14:28: o limite de uso é redefinido\nQuantidade usada: 3%\nAumente o limite de uso em 2x com o AI Pro";
        var u = GoogleUsageText.Parse(page)!;
        Assert.Equal("PLUS", u.Plan);
        Assert.Equal(12, u.CurrentPercent);
        Assert.Equal("16:28", u.CurrentReset);
        Assert.Equal(3, u.WeeklyPercent);
        Assert.Equal("10 de nov., 14:28", u.WeeklyReset);
    }

    [Fact]
    public void SignInPageIsNotUsage() => Assert.Null(GoogleUsageText.Parse("Fazer login\nUse sua Conta do Google"));
}
