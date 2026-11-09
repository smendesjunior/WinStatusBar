using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class SvgRecolorTests
{
    [Fact]
    public void EveryVisiblePaintBecomesOneColor()
    {
        const string svg = """<svg viewBox="0 0 24 24"><path d="M0 0" fill="#3186FF"/><path d="M1 1" fill="url(#g)" stroke="#123456" stroke-width="2" fill-rule="evenodd"/><path d="M2 2" fill="none" style="stroke:#ABCDEF;fill-opacity:.5"/><defs><linearGradient id="g"><stop stop-color="#FF0000"/></linearGradient><mask id="m"><rect fill="#fff"/></mask></defs></svg>""";
        string mono = SvgRecolor.Monochrome(svg, "#FFFFFF");
        Assert.StartsWith("""<svg fill="#FFFFFF" viewBox""", mono);
        Assert.DoesNotContain("#3186FF", mono);
        Assert.DoesNotContain("url(#g)", mono);
        Assert.DoesNotContain("#123456", mono);
        Assert.DoesNotContain("#ABCDEF", mono);
        Assert.Contains("""fill="none" style="stroke:#FFFFFF;fill-opacity:.5""", mono); // "none" continua sem pintura
        Assert.Contains("""stroke-width="2" fill-rule="evenodd""", mono);
        Assert.Contains("""<stop stop-color="#FF0000"/>""", mono);                 // definições intactas
        Assert.Contains("""<rect fill="#fff"/>""", mono);                         // máscara intacta
    }

    [Fact]
    public void RootFillIsNotDuplicated()
    {
        string mono = SvgRecolor.Monochrome("""<svg fill="red"><path d="M0 0"/></svg>""", "#1B1B1B");
        Assert.Equal("""<svg fill="#1B1B1B"><path d="M0 0"/></svg>""", mono);
    }
}
