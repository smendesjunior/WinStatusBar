using System.Text.Json;
using CodexIAs.Providers;
using WinStatusBar.Core.Ai.Antigravity;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Google Antigravity pelo agy 1.5.8 (saídas reais de 14/11/2026).</summary>
public class AntigravityCliTests
{
    [Fact]
    public void Lista_de_modelos_ignora_a_linha_de_andamento()
    {
        const string output = "Fetching available models...\r\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\r\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\r\n\r\ngpt-oss-120b-medium\tGPT-OSS 120B (Medium)\r\n";
        var models = AntigravityCli.ParseModels(output);
        Assert.Equal(["gemini-3.8-flash-high", "claude-sonnet-4-6", "gpt-oss-120b-medium"], models.Select(m => m.Id));
        Assert.Equal("Antigravity — Claude Sonnet 4.6 (Thinking)", AntigravityCli.DisplayName(models[1]));
    }

    [Fact]
    public void Eventos_do_stream_json()
    {
        var init = AntigravityCli.ParseEvent("""{"event":"init","conversation_id":"c1","init":{"cwd":"C:\\x","tools":[]}}""");
        Assert.Equal("c1", init.Conversation);
        var delta = AntigravityCli.ParseEvent("""{"event":"step_update","step_update":{"conversation_id":"c1","step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"OK1"}}""");
        Assert.Equal("OK1", delta.Delta);
        var user = AntigravityCli.ParseEvent("""{"event":"step_update","step_update":{"conversation_id":"c1","step_index":0,"state":"DONE","step_type":"user_input"}}""");
        Assert.Null(user.Delta);
        var ok = AntigravityCli.ParseEvent("""{"event":"result","result":{"conversation_id":"c1","status":"SUCCESS","response":"OK1\n"}}""");
        Assert.True(ok.Done); Assert.Null(ok.Error); Assert.Equal("OK1\n", ok.Final);
        var fail = AntigravityCli.ParseEvent("""{"event":"result","result":{"conversation_id":"c2","status":"ERROR","response":"","error":"stream input message is missing the \"event\" field"}}""");
        Assert.True(fail.Done); Assert.Contains("missing", fail.Error);
        Assert.False(AntigravityCli.ParseEvent("warning: algo").Done);
    }

    [Fact]
    public void Pedido_vai_pela_entrada_padrao_no_formato_do_agy()
    {
        using var doc = JsonDocument.Parse(AntigravityCli.InputLine("linha 1\n\"aspas\""));
        Assert.Equal("user", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("linha 1\n\"aspas\"", doc.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Argumentos_so_leitura_por_padrao_e_p_vazio_no_fim()
    {
        var args = AntigravityCli.Arguments("claude-sonnet-4-6", null, null, AntigravityCli.ReadOnlyDeny);
        Assert.Equal("-p=", args[^1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        string settings = args[args.IndexOf("--settings") + 1];
        var deny = JsonDocument.Parse(settings).RootElement.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("write_file(*)", deny);
        Assert.Contains("command(*)", deny);
        Assert.Equal("claude-sonnet-4-6", args[args.IndexOf("--model") + 1]);

        var full = AntigravityCli.Arguments("m", "high", "conv-1", null, [@"C:\pasta"]);
        Assert.Contains("--dangerously-skip-permissions", full);
        Assert.DoesNotContain("--settings", full);
        Assert.Equal("conv-1", full[full.IndexOf("--conversation") + 1]);
        Assert.Equal("high", full[full.IndexOf("--effort") + 1]);
        Assert.Equal(@"C:\pasta", full[full.IndexOf("--add-dir") + 1]);
    }

    [Fact]
    public void Erros_conhecidos_em_portugues()
    {
        Assert.Contains("sem login", AntigravityCli.Friendly("Error: not logged in"));
        Assert.Contains("sem cota", AntigravityCli.Friendly("429 RESOURCE_EXHAUSTED"));
        Assert.Equal("outro", AntigravityCli.Friendly("outro"));
        Assert.Contains("não faz mais parte do seu plano", AntigravityCli.Friendly("Third-party model access is not available on your current plan"));
        Assert.Contains("não faz mais parte do seu plano", AntigravityCli.Friendly("model claude-sonnet-4-6 is not available"));
        Assert.Contains("não faz mais parte do seu plano", AntigravityCli.Friendly("unknown model: gpt-oss-120b-medium"));
    }

    [Fact]
    public void Opcoes_no_seletor_do_Codex()
    {
        var models = AntigravityProvider.ToModels([new("gemini-3.8-flash-high", "Gemini 3.8 Flash (High)"), new("claude-opus-4-6-thinking", "Claude Opus 4.6 (Thinking)")]);
        Assert.Equal(["codexias-antigravity-gemini-3-8-flash-high", "codexias-antigravity-claude-opus-4-6-thinking"], models.Select(m => m.Slug));
        Assert.All(models, m => Assert.Equal("antigravity", m.Provider));
        Assert.Equal("claude-opus-4-6-thinking", models[1].Id);
        Assert.Equal("Antigravity — Claude Opus 4.6 (Thinking)", models[1].DisplayName);
    }
}
