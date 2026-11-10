using System.Runtime.InteropServices;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexResumeCommandTests
{
    // Dados sintéticos das Partes 2 e 3 (o repositório é público).
    internal const string Id1 = "01aa0000-0000-7000-8000-000000000001", Id2 = "01aa0000-0000-7000-8000-000000000002";
    internal const string Title1 = "Analisar processo de Fulano de Tal";
    internal const string Cwd = @"C:\Users\fulano\OneDrive\Área de Trabalho\PROJEÇÃO";
    internal static readonly DateTimeOffset At = new(new DateTime(2026, 11, 10, 0, 31, 0, DateTimeKind.Local));

    internal static QuotaStop Stop(string? model = "gpt-5.6-sol", string? effort = "low", string? sandbox = "workspace-write",
        string? approval = "never", string id = Id1, string title = Title1, string turn = "turn-parado-1") =>
        new(id, title, @"C:\c\rollout.jsonl", 0, turn, At, model, effort, false, sandbox, true, [], approval, Cwd, [@"Z:\"],
            "gpt-5.6-sol", "medium", 79000, "workspace_owner_credits_depleted");

    [Fact]
    public void ArgumentsFollowTheVerifiedOrderWithoutTheKey()
    {
        var args = CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(), _ => true, out var missing), []);
        Assert.Empty(missing);
        Assert.Equal(new[]
        {
            "exec", "--json", "--skip-git-repo-check", "-C", Cwd, "--add-dir", @"Z:\", "resume", Id1,
            "-c", "openai_base_url=\"https://api.openai.com/v1\"", "-c", "model_provider=\"openai\"", "-c", "model=\"gpt-5.6-sol\"", "-c", "model_reasoning_effort=\"low\"",
            "-c", "sandbox_mode=\"workspace-write\"", "-c", "approval_policy=\"never\"", "-c", "sandbox_workspace_write.network_access=true",
            "-c", "shell_environment_policy.exclude=[\"CODEX_API_KEY\"]", "--", "continue de onde parou",
        }, args);
        Assert.DoesNotContain(args, a => a.Contains("sk-") || a.Contains("medium"));
    }

    [Fact]
    public void ExtraDirsAndNetworkOnlyForWorkspaceWrite()
    {
        var readOnly = CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(sandbox: "read-only"), _ => true, out _), []);
        Assert.DoesNotContain("--add-dir", readOnly);
        Assert.DoesNotContain(readOnly, a => a.Contains("network_access"));

        var spec = CodexResumeCommand.ToSpec(Stop(), d => d != @"Z:\", out var missing);
        Assert.Equal([@"Z:\"], missing);
        Assert.Empty(spec.ExtraDirs);
        Assert.Equal(("gpt-5.6-sol", "low"), (spec.Model, spec.Effort));
    }

    [Theory]
    [InlineData("codexias-claude-opus", "low", "workspace-write", ApiResumeProblem.BarModel)]
    [InlineData("openai-minha-api-sol", "low", "workspace-write", ApiResumeProblem.BarModel)]
    [InlineData("winstatusbar-x", "low", "workspace-write", ApiResumeProblem.BarModel)]
    [InlineData("gpt 5", "low", "workspace-write", ApiResumeProblem.MissingTurnData)]
    [InlineData("-c", "low", "workspace-write", ApiResumeProblem.MissingTurnData)]
    [InlineData(null, "low", "workspace-write", ApiResumeProblem.MissingTurnData)]
    [InlineData("gpt-5.6-sol", null, "workspace-write", ApiResumeProblem.MissingTurnData)]
    [InlineData("gpt-5.6-sol", "low", "weird", ApiResumeProblem.MissingTurnData)]
    [InlineData("gpt-5.6-sol", "LOW", "workspace-write", ApiResumeProblem.EffortNotAccepted)]
    public void RejectsInvalidAndBarModels(string? model, string? effort, string sandbox, ApiResumeProblem expected) =>
        Assert.Equal(expected, CodexResumeCommand.Check(Stop(model, effort, sandbox), null));

    [Fact]
    public void EffortIsCheckedAgainstTheKnownLevelsAndTheValidStopPasses()
    {
        var levels = new CodexModelLevels.Levels(["low", "medium", "high"], "medium");
        Assert.Equal(ApiResumeProblem.EffortNotAccepted, CodexResumeCommand.Check(Stop(effort: "xhigh"), levels));
        Assert.Null(CodexResumeCommand.Check(Stop(), levels));
        Assert.Null(CodexResumeCommand.Check(Stop(), null));
        Assert.Equal(ApiResumeProblem.MissingTurnData, CodexResumeCommand.Check(Stop(approval: "sometimes"), null));
        Assert.Throws<ArgumentException>(() => CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(model: "gpt 5"), _ => true, out _), []));
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static string[] Split(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(argv); }
    }

    [Theory]
    [InlineData(Cwd)]
    [InlineData(@"Z:\")]
    [InlineData("a \"b\" c")]
    [InlineData("")]
    [InlineData(@"trailing\\")]
    [InlineData("model=\"x\"")]
    [InlineData(@"C:\pasta com espaço\")]
    public void QuoteRoundTripsThroughCommandLineToArgvW(string arg)
    {
        var parts = Split(WindowsCommandLine.Join(@"C:\Program Files\Codex\codex.exe", ["exec", arg]));
        Assert.Equal(3, parts.Length);
        Assert.Equal(@"C:\Program Files\Codex\codex.exe", parts[0]);
        Assert.Equal(arg, parts[2]);
    }

    [Fact]
    public void ConfigExcludesAreAddedToCodexApiKey()
    {
        var facts = CodexResumeCommand.ReadConfig("forced_login_method = \"chatgpt\"\n[shell_environment_policy]\nexclude = [\"AWS_*\", # c\n 'AZURE_*']\n");
        Assert.Equal(["AWS_*", "AZURE_*"], facts.EnvExcludes);
        Assert.True(facts.ForcedChatGptLogin);
        var args = CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(), _ => true, out _), facts.EnvExcludes);
        Assert.Equal("shell_environment_policy.exclude=[\"AWS_*\",\"AZURE_*\",\"CODEX_API_KEY\"]", args[^3]);

        var dotted = CodexResumeCommand.ReadConfig("model = \"gpt-5.6-sol\"\nshell_environment_policy.exclude = [\"CODEX_API_KEY\", \"X\"]\n[profiles.a]\nforced_login_method = \"chatgpt\"\n");
        Assert.Equal(["CODEX_API_KEY", "X"], dotted.EnvExcludes);
        Assert.False(dotted.ForcedChatGptLogin);
        Assert.Equal("shell_environment_policy.exclude=[\"CODEX_API_KEY\",\"X\"]",
            CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(), _ => true, out _), dotted.EnvExcludes)[^3]);

        var withProfile = CodexResumeCommand.ReadConfig("profile = \"rapido\"\nmodel_provider = \"gateway\"\n[profiles.rapido]\nmodel = \"gpt-5-mini\"\n");
        Assert.Equal(("rapido", "gateway"), (withProfile.Profile, withProfile.ModelProvider));
        var args2 = CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(), _ => true, out _), withProfile.EnvExcludes, withProfile.Profile);
        Assert.Contains("profiles.rapido.model=\"gpt-5.6-sol\"", args2);
        Assert.Contains("profiles.rapido.model_reasoning_effort=\"low\"", args2);
        Assert.Contains("profiles.rapido.model_provider=\"openai\"", args2);
        Assert.Contains("profiles.\"meu perfil\".model=\"gpt-5.6-sol\"",
            CodexResumeCommand.Arguments(CodexResumeCommand.ToSpec(Stop(), _ => true, out _), [], "meu perfil"));

        var empty = CodexResumeCommand.ReadConfig(null);
        Assert.Empty(empty.EnvExcludes);
        Assert.False(empty.ForcedChatGptLogin);
    }
}
