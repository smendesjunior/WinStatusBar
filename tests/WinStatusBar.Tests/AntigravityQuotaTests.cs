using WinStatusBar.Core.Ai.Antigravity;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Painel "Models &amp; Quota" do agy 1.5.9 (/usage), como apareceu em 14/11/2026.</summary>
public class AntigravityQuotaTests
{
    private const string Screen = """
        ▀▀▀▀▀▀       pessoa@gmail.com (Antigravity Starter Quota)
        ▀▀▀▀▀▀▀▀      Gemini 3.8 Flash (High)
        ? for shortcutsauto · Gemini 3.8 Flash · high
        /usage
        usage  View model quota usage
        └ Models & Quota
          Account: pessoa@gmail.com
        GEMINI MODELS
          Models within this group: Gemini 3.8 Flash
          Weekly Limit Remaining
        [██████████████████████████████████████████████░░░░] 92.39%
        Refreshes in 167h 29m
        CLAUDE AND GPT MODELS
          Models within this group: Claude Sonnet 4.6, Claude Opus 4.6, GPT-OSS 120B
          Weekly Limit Remaining
        [████████████████████████████████████████████████░░] 95.80%
        Refreshes in 167h 34m
          │Within each group, models share a weekly limit. Quota is consumed
          │proportionally to the cost of the tokens.
          ↑/↓ Scroll · pgup/pgdown Page · ctrl+end Bottom · ctrl+home Top · esc Close
        """;

    [Fact]
    public void Painel_de_cota_vira_grupos()
    {
        var s = AntigravityQuota.Parse(Screen)!;
        Assert.Equal("pessoa@gmail.com", s.Account);
        Assert.Equal("Antigravity Starter Quota", s.Plan);
        Assert.Equal(2, s.Groups.Count);
        Assert.Equal(("Gemini", 92.39, "167h 29m"), (s.Groups[0].Name, s.Groups[0].RemainingPercent, s.Groups[0].Refresh));
        Assert.Equal("Claude e GPT", s.Groups[1].Name);
        Assert.Equal(95.80, s.Groups[1].RemainingPercent);
        Assert.Equal("Weekly Limit", s.Groups[1].Limit);
        Assert.StartsWith("Claude Sonnet 4.6", s.Groups[1].Models);
        Assert.Equal("Antigravity: Gemini 92 % · Claude e GPT 96 % restantes", AntigravityQuota.MenuText(s, remaining: true));
        Assert.Equal("Antigravity: Gemini 8 % · Claude e GPT 4 % usados", AntigravityQuota.MenuText(s, remaining: false));
    }

    [Fact]
    public void Plano_desenhado_em_outra_posicao_da_tela()
    {
        string raw = "\x1b[3;22Hpessoa@gmail.com\x1b[14;3H? for shortcuts\x1b[3;42H (Antigravity Starter Quota)\x1b[14;3H"
                     + "\nModels & Quota\nGEMINI MODELS\nWeekly Limit Remaining\n[█░] 10%\nRefreshes in 1h 2m";
        var s = AntigravityQuota.Parse(raw)!;
        Assert.Equal("pessoa@gmail.com", s.Account);
        Assert.Equal("Antigravity Starter Quota", s.Plan);
    }

    [Fact]
    public void Sem_painel_nao_inventa_cota()
    {
        Assert.Null(AntigravityQuota.Parse("? for shortcuts\n/usage\nusage  View model quota usage"));
    }

    [Fact]
    public void Sequencias_do_terminal_sao_limpas()
    {
        string raw = "\x1b[?25l\x1b[12;1H\x1b[1mGEMINI MODELS\x1b[0m\x1b[13;3HWeekly Limit Remaining\x1b[14;1H[███░] 50.5%\x1b[15;1HRefreshes in 3h 5m\x1b]0;agy\x07";
        string clean = AntigravityQuota.Clean(raw);
        Assert.Contains("GEMINI MODELS\n", clean);
        Assert.DoesNotContain('\x1b', clean); // como caractere: a comparação de texto com cultura ignora caracteres de controle
        var s = AntigravityQuota.Parse("Models & Quota\n" + clean)!;
        Assert.Equal(50.5, s.Groups[0].RemainingPercent);
    }

    [Fact]
    public void Tempo_de_renovacao_curto()
    {
        Assert.Equal("6 d 23 h", AntigravityQuota.ShortRefresh("167h 29m"));
        Assert.Equal("3 h 5 min", AntigravityQuota.ShortRefresh("3h 5m"));
        Assert.Equal("12 min", AntigravityQuota.ShortRefresh("12m"));
    }

    [Fact]
    public void Tela_de_login_e_reconhecida()
    {
        Assert.True(AntigravityQuota.AsksForLogin("Welcome!\nSign in with Google to continue"));
        Assert.False(AntigravityQuota.AsksForLogin("? for shortcuts"));
    }
}
