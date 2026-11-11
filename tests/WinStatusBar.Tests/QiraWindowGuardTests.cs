using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// As janelas pequenas do Hub do Qira (barrinha, bolha) ficam escondidas; a janela grande do app, não. Minimizada, a
/// janela grande informa um retângulo minúsculo (160×28): não pode ser escondida, senão some da barra de tarefas.
/// </summary>
public class QiraWindowGuardTests
{
    [Fact]
    public void Janelas_pequenas_ficam_escondidas_e_a_grande_minimizada_nao()
    {
        Assert.True(CodexIAs.Providers.QiraWindowGuard.ShouldHide(300, 60, minimized: false));   // barrinha
        Assert.True(CodexIAs.Providers.QiraWindowGuard.ShouldHide(64, 64, minimized: false));    // bolha
        Assert.False(CodexIAs.Providers.QiraWindowGuard.ShouldHide(1200, 800, minimized: false)); // app aberto de propósito
        Assert.False(CodexIAs.Providers.QiraWindowGuard.ShouldHide(160, 28, minimized: true));   // app minimizado
    }

    [Theory]
    [InlineData("src", "WinStatusBar", "Services", "QiraWindowGuard.cs")]
    [InlineData("src", "WinStatusBar.CodexIAs", "Providers", "QiraWindowGuard.cs")]
    public void As_duas_copias_conferem_se_a_janela_esta_minimizada(params string[] file)
    {
        string source = RepoSource.Read(file);
        Assert.Contains("ShouldHide(int width, int height, bool minimized) => !minimized && (height < 360 || width < 420)", source);
        Assert.Contains("IsIconic(h)", source);
        Assert.Contains("private static extern bool IsIconic(IntPtr h);", source);
    }
}
