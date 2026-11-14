using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Área de Trabalho redirecionada pelo OneDrive (caso real de 14/11/2026).</summary>
public class KnownFolderPathTests
{
    private const string Profile = @"C:\Users\pessoa";
    private static string Real(Environment.SpecialFolder f) => f switch
    {
        Environment.SpecialFolder.DesktopDirectory => @"C:\Users\pessoa\OneDrive\Desktop",
        Environment.SpecialFolder.MyDocuments => @"C:\Users\pessoa\Documents",
        _ => "",
    };

    [Theory]
    [InlineData(@"C:\Users\pessoa\Desktop\Novo.txt", @"C:\Users\pessoa\OneDrive\Desktop\Novo.txt")]
    [InlineData(@"C:/Users/pessoa/desktop/a/b.txt", @"C:\Users\pessoa\OneDrive\Desktop\a\b.txt")]
    [InlineData(@"C:\Users\pessoa\Área de Trabalho\x.txt", @"C:\Users\pessoa\OneDrive\Desktop\x.txt")]
    [InlineData(@"C:\Users\pessoa\Desktop", @"C:\Users\pessoa\OneDrive\Desktop")]
    [InlineData(@"C:\Users\pessoa\Documents\r.docx", @"C:\Users\pessoa\Documents\r.docx")]
    [InlineData(@"D:\Desktop\x.txt", @"D:\Desktop\x.txt")]
    [InlineData(@"C:\Users\pessoa\DesktopOld\x.txt", @"C:\Users\pessoa\DesktopOld\x.txt")]
    public void Leva_para_a_pasta_real(string asked, string expected)
        => Assert.Equal(expected, KnownFolderPath.Resolve(asked, Profile, Real));
}
