using System.Text.RegularExpressions;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Partes da continuação pela API que dependem do WinUI ou do Windows: confere o código-fonte.</summary>
public class ApiResumeSourceTests
{
    internal static string Body(string src, string sig)
    {
        var match = Regex.Match(src, Regex.Escape(sig) + @"[^{]*\{(?<b>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, $"não achei: {sig}");
        return match.Groups["b"].Value;
    }

    private static string Src(params string[] path) => RepoSource.Read(["src", "WinStatusBar", .. path]);

    [Fact]
    public void BalloonClickComesFromNinBalloonUserClick()
    {
        string tray = Src("Services", "TrayIconService.cs");
        Assert.Contains("NIN_BALLOONUSERCLICK = WM_USER + 5", tray);
        Assert.Contains("0x405", tray);
        string callback = tray[tray.IndexOf("if (msg == CallbackMsg)", StringComparison.Ordinal)..];
        callback = callback[..callback.IndexOf("return IntPtr.Zero;", StringComparison.Ordinal)];
        Assert.Contains("ev == NIN_BALLOONUSERCLICK", callback);
        Assert.Contains("BalloonClicked?.Invoke()", callback);
    }
}
