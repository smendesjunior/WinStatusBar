using Windows.UI;
using Color = Windows.UI.Color;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using Xunit;

namespace WinStatusBar.Tests;

public class AdaptiveLogicTests
{
    private static readonly Color MicaTint = Color.FromArgb(255, 0x1C, 0x1B, 0x22), Fallback = Color.FromArgb(255, 0x20, 0x20, 0x20),
                                  ElectronSolid = Color.FromArgb(255, 0x19, 0x19, 0x19), Accent = Color.FromArgb(255, 0x1F, 0x9D, 0xB3);

    [Fact]
    public void ExplorerActive_IsMaterial_AndInactive_IsTheSameMaterialInactive()
    {
        // Caso do log (08/11/2026): o Explorador alternava entre "Mica Alt" e "#202020" e a barra recriava o fundo a cada troca.
        Assert.Equal((AdaptiveState.Material, true), AdaptiveLogic.Classify(BackdropKind.MicaAlt, true, false, MicaTint, false, null));
        Assert.Equal((AdaptiveState.Material, false), AdaptiveLogic.Classify(BackdropKind.MicaAlt, false, false, Fallback, false, null));
    }

    [Fact]
    public void ElectronThatDeclaresMicaButPaintsSolid_IsSolid()
        => Assert.Equal(AdaptiveState.Maximized, AdaptiveLogic.Classify(BackdropKind.Mica, true, false, ElectronSolid, false, null).State);

    [Fact]
    public void AccentColorOnTitleBars_ActiveMicaWindowIsSolidAccent()
        => Assert.Equal(AdaptiveState.Maximized, AdaptiveLogic.Classify(BackdropKind.MicaAlt, true, true, Accent, false, null).State);

    [Fact]
    public void AppDefinedCaptionColor_IsSolid()
        => Assert.Equal(AdaptiveState.Maximized, AdaptiveLogic.Classify(BackdropKind.Mica, true, false, MicaTint, true, Accent).State);

    [Fact]
    public void ActivationToggleOnFallbackColor_KeepsCurrentMode_NoTeardown()
    {
        var hwnd = new IntPtr(42);
        // Claude: ativo é sólido (#191919), inativo vira a reserva #202020 → continua sólido (só a cor muda).
        Assert.Equal(ModeDecision.KeepCurrentMode,
            AdaptiveLogic.DecideMode(AdaptiveState.Maximized, hwnd, AdaptiveState.Material, hwnd, fallbackLike: true, null, IntPtr.Zero));
    }

    [Fact]
    public void ModeSwitchOnSameWindow_NeedsTwoAgreeingReadings()
    {
        var hwnd = new IntPtr(42);
        Assert.Equal(ModeDecision.WaitForConfirmation,
            AdaptiveLogic.DecideMode(AdaptiveState.Maximized, hwnd, AdaptiveState.Material, hwnd, false, null, IntPtr.Zero));
        Assert.Equal(ModeDecision.Apply,
            AdaptiveLogic.DecideMode(AdaptiveState.Maximized, hwnd, AdaptiveState.Material, hwnd, false, AdaptiveState.Material, hwnd));
        // Outra janela: aplica na hora.
        Assert.Equal(ModeDecision.Apply,
            AdaptiveLogic.DecideMode(AdaptiveState.Maximized, hwnd, AdaptiveState.Material, new IntPtr(7), false, null, IntPtr.Zero));
    }

    [Fact]
    public void SnappedHalves_EachGetTheirOwnSegment()
    {
        // Metade esquerda e metade direita encaixadas, área de trabalho 0–2560.
        var segs = AdaptiveLogic.Segments([(0, 1280), (1280, 2560)], fillBehind: false, 0, 2560);
        Assert.Equal([(0, 1280, 0), (1280, 2560, 1)], segs);
    }

    [Fact]
    public void FrontWindowWins_AndMaximizedBehindFillsTheRest()
    {
        // Janela encaixada à esquerda na frente de uma maximizada: a esquerda é dela, o resto é da maximizada.
        var segs = AdaptiveLogic.Segments([(0, 1280)], fillBehind: true, 0, 2560);
        Assert.Equal([(0, 1280, 0), (1280, 2560, -1)], segs);
        // Duas janelas sobrepostas: a da frente (índice 0) fica com a sobreposição.
        var overlap = AdaptiveLogic.Segments([(800, 1600), (0, 1200)], fillBehind: false, 0, 2560);
        Assert.Equal([(0, 800, 1), (800, 1600, 0)], overlap);
    }

    [Fact]
    public void CoveredAt_OnlyWindowsCrossingTheRow()
    {
        var covered = AdaptiveLogic.CoveredAt([(100, 50, 300, 90), (400, 80, 600, 400), (700, 200, 900, 300)], 84);
        Assert.Equal([(100, 300), (400, 600)], covered);
    }

    [Fact]
    public void LightSkyWallpaper_GetsDarkText()
    {
        // Caso relatado (08/11/2026): céu azul-claro atrás da barra transparente e texto branco, com pouco contraste.
        double sky = AdaptiveLogic.RelativeLuminance(Color.FromArgb(255, 120, 150, 170));
        Assert.True(AdaptiveLogic.DarkTextFor(sky, currentDarkText: false));
    }

    [Fact]
    public void DarkWallpaper_GetsLightText_AndMidToneKeepsCurrent()
    {
        Assert.False(AdaptiveLogic.DarkTextFor(AdaptiveLogic.RelativeLuminance(Color.FromArgb(255, 30, 40, 60)), currentDarkText: true));
        double mid = AdaptiveLogic.RelativeLuminance(Color.FromArgb(255, 118, 118, 118)); // contraste quase igual com preto e branco
        Assert.True(AdaptiveLogic.DarkTextFor(mid, currentDarkText: true));
        Assert.False(AdaptiveLogic.DarkTextFor(mid, currentDarkText: false));
        Assert.Null(AdaptiveLogic.DarkTextFor(null, true));
    }

    [Fact]
    public void FallbackColors_AreRecognized()
    {
        Assert.True(AdaptiveLogic.IsFallbackColor(Fallback));
        Assert.True(AdaptiveLogic.IsFallbackColor(Color.FromArgb(255, 0xF3, 0xF3, 0xF3)));
        Assert.False(AdaptiveLogic.IsFallbackColor(ElectronSolid with { R = 0x10, G = 0x10, B = 0x10 }));
        Assert.False(AdaptiveLogic.IsFallbackColor(Accent));
    }
}
