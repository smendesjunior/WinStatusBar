using WinStatusBar.Core.Metrics;
using Xunit;
namespace WinStatusBar.Tests;
public class WindowsUpdatePresentationTests
{
    [Fact] public void OpeningUsesPassiveSnapshotWithoutRequestingScan()
    {
        var reads=0; var scans=0;
        var controller=new WindowsUpdateManualController(()=>{reads++;return new(WindowsUpdateState.UpToDate);},()=>scans++);
        controller.Open();controller.Open();
        Assert.Equal(2,reads);Assert.Equal(0,scans);
        controller.Check();Assert.Equal(1,scans);
    }
    [Fact] public void FailedUpdateDoesNotOfferRestartFromUnrelatedSystemSignal()
    {
        var entry=new WindowsUpdateEntry("Update","Falhou"){ErrorCode=unchecked((int)0xC1900401)};
        var result=WindowsUpdatePresentation.FromNative([entry],new DateTime(2026,11,7),false);
        Assert.Equal(WindowsUpdateState.Failed,result.State);
        Assert.Equal("Atualizações que falharam",WindowsUpdatePresentation.Heading(result));
        Assert.Equal("Repetir",entry.ActionLabel);
        Assert.False(WindowsUpdatePresentation.NeedsRestart(result));
        Assert.Contains("Não encontramos nenhuma atualização publicada",WindowsUpdatePresentation.ErrorMessage(entry.ErrorCode!.Value));
    }
    [Fact] public void RestartRequiresPendingUpdateInRebootStage()
    {
        var result=WindowsUpdatePresentation.FromNative([],DateTime.Now,true);
        Assert.True(WindowsUpdatePresentation.NeedsRestart(result));
    }
    [Theory]
    [InlineData(WindowsUpdateState.UpToDate,"Você está atualizado")]
    [InlineData(WindowsUpdateState.RestartRequired,"Reinicialização necessária")]
    [InlineData(WindowsUpdateState.Unknown,"Verificar se há atualizações")]
    public void NativeHeadings(WindowsUpdateState state,string expected)
        => Assert.Equal(expected,WindowsUpdatePresentation.Heading(new(state)));
    [Theory]
    [InlineData(0.37,37)] [InlineData(1.0,100)] [InlineData(2.0,100)] [InlineData(-1.0,0)]
    public void NativeProgressIsFraction(double value,int expected) => Assert.Equal(expected,WindowsUpdateActivity.FromNative("Download",value).Percent);
    [Fact] public void RealProgressOnlyAndSeparateStages()
    {
        Assert.Equal("Baixando — 37%",new WindowsUpdateActivity("Download",37).Label);
        Assert.Equal("Instalando — 12%",new WindowsUpdateActivity("Install",12).Label);
        Assert.Equal("Baixando…",new WindowsUpdateActivity("Download",null).Label);
    }
}


