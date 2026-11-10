using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Lê um arquivo do código da barra (testes de regressão de partes que dependem do WinUI).</summary>
internal static class RepoSource
{
    public static string Read(params string[] relative) => File.ReadAllText(Path.Combine([Root(), .. relative]));

    /// <summary>Pasta do repositório (a que tem o WinStatusBar.sln).</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinStatusBar.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
