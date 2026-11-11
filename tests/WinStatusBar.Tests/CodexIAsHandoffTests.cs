using System.Text;
using System.Text.Json.Nodes;
using CodexIAs;
using Xunit;

namespace WinStatusBar.Tests;

// Troca de IA no meio da conversa do Codex: pacote de passagem, ações pelo Codex para IAs que só conversam, compactação
// e a opção "OpenAI — minha API" numa conversa que começou num modelo da conta.

public class CodexIAsHandoffReaderTests
{
    /// <summary>Conversa real do Codex (formato capturado em 10/11/2026): regras, pedido, comando do Sol, resultado, parada pela cota.</summary>
    internal static JsonObject SolStoppedByQuota(bool tools = true)
    {
        var payload = JsonNode.Parse("""
        {
          "model": "codexias-gemini-3-6-flash", "stream": true, "prompt_cache_key": "thread-1",
          "input": [
            { "type": "message", "role": "developer", "content": [ { "type": "input_text", "text": "Responda ao pedido da pessoa em texto, no idioma dela." } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "# AGENTS.md instructions\n\n<INSTRUCTIONS>\nUse português.\n</INSTRUCTIONS>" } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "<environment_context>\n<cwd>C:\\Relatorios</cwd>\n</environment_context>" } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "Escreva o relatório do processo X" } ] },
            { "type": "reasoning", "id": "rs_1", "summary": [], "encrypted_content": "gAAAA" },
            { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "Vou ler os dados." } ] },
            { "type": "function_call", "name": "exec_command", "arguments": "{\"cmd\":\"Get-Content dados.txt\",\"workdir\":\"C:\\\\Relatorios\"}", "call_id": "c1" },
            { "type": "function_call_output", "call_id": "c1", "output": "área: 120 m²" },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "<turn_aborted>\nusage limit\n</turn_aborted>" } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "continue de onde parou" } ] }
          ],
          "tools": [ { "type": "function", "name": "exec_command", "parameters": {} }, { "type": "function", "name": "write_stdin", "parameters": {} } ]
        }
        """)!.AsObject();
        if (!tools) payload.Remove("tools");
        return payload;
    }

    [Fact]
    public void Reads_the_work_done_by_the_previous_model()
    {
        var c = ConversationReader.Read(SolStoppedByQuota());
        Assert.Equal("continue de onde parou", c.Prompt);
        Assert.False(c.PromptIsResult);
        Assert.True(c.Actions);
        Assert.Equal(@"C:\Relatorios", c.Cwd);
        Assert.Equal("Use português.", c.Rules);
        Assert.Equal(["user", ConversationReader.Action, ConversationReader.Result, ConversationReader.Notice], c.History.Select(h => h.Role));
        Assert.Contains("Vou ler os dados.", c.History[1].Content);
        Assert.Contains("Get-Content dados.txt", c.History[1].Content);
        Assert.Equal("área: 120 m²", c.History[2].Content);
    }

    [Fact]
    public void Without_the_command_tool_there_are_no_actions()
        => Assert.False(ConversationReader.Read(SolStoppedByQuota(tools: false)).Actions);

    [Fact]
    public void A_result_after_an_action_becomes_the_prompt()
    {
        var payload = SolStoppedByQuota();
        var input = payload["input"]!.AsArray();
        input.RemoveAt(input.Count - 1); input.RemoveAt(input.Count - 1); // sem a parada: a sequência ainda está em andamento
        var c = ConversationReader.Read(payload);
        Assert.True(c.PromptIsResult);
        Assert.StartsWith("Resultado da ação:", c.Prompt);
        Assert.Contains("120 m²", c.Prompt);
        Assert.Equal(ConversationReader.Action, c.History[^1].Role);
    }

    [Fact]
    public void Transcript_keeps_the_rules_the_original_request_and_the_actions()
    {
        var c = ConversationReader.Read(SolStoppedByQuota());
        string text = ConversationReader.WithTranscript(c, 12000);
        Assert.Contains("[Regras do projeto (AGENTS.md)", text);
        Assert.Contains("Usuário: Escreva o relatório do processo X", text);
        Assert.Contains("Ação executada:", text);
        Assert.Contains("Resultado: área: 120 m²", text);
        Assert.Contains("interrompida", text);
        Assert.Contains("[Como agir no computador]", text);
        Assert.EndsWith("Pedido atual: continue de onde parou", text);
    }

    [Fact]
    public void Long_conversations_keep_the_original_request()
    {
        var history = new List<(string, string)> { ("user", "Pedido original importante") };
        for (int i = 0; i < 50; i++) { history.Add((ConversationReader.Action, "Comando:\n" + new string('x', 500))); history.Add((ConversationReader.Result, new string('y', 500))); }
        string text = ConversationReader.Transcript(history, 5000);
        Assert.Contains("Pedido original da pessoa: Pedido original importante", text);
        Assert.True(text.Length < 9000);
    }

    [Fact]
    public void Long_results_keep_the_start_and_the_end()
    {
        string cut = ConversationReader.Cut("INICIO" + new string('-', 10000) + "FIM", 300);
        Assert.StartsWith("INICIO", cut);
        Assert.EndsWith("FIM", cut);
        Assert.Contains("omitidos", cut);
    }

    [Fact]
    public void Compaction_keeps_a_summary_of_the_work()
    {
        var output = BridgeServer.Compact(SolStoppedByQuota())["output"]!.AsArray();
        string summary = output[0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith("[Resumo da conversa até aqui", summary);
        Assert.Contains("Get-Content dados.txt", summary);
        Assert.Contains("área: 120 m²", summary);
        Assert.DoesNotContain(output, i => i!["type"]!.GetValue<string>() != "message");
        Assert.DoesNotContain(output.Skip(1), i => i!["content"]![0]!["text"]!.GetValue<string>().StartsWith("# AGENTS.md"));
    }
}

public class CodexIAsActionProtocolTests
{
    public CodexIAsActionProtocolTests() => CodexIAs.Log.Enabled = false; // não grava no registro real do componente

    [Fact]
    public void Parses_a_command()
    {
        var (text, action) = ActionProtocol.Parse("Vou ler o arquivo.\n<<<COMANDO>>>\nGet-Content -LiteralPath 'a.md'\n<<<FIM>>>")!.Value;
        Assert.Equal("Vou ler o arquivo.", text);
        Assert.Equal(ActionProtocol.Kind.Command, action.Kind);
        Assert.Equal("Get-Content -LiteralPath 'a.md'", action.Text);
    }

    [Fact]
    public void Parses_a_file_keeping_blank_lines_and_without_the_end_marker()
    {
        var (_, action) = ActionProtocol.Parse("<<<ARQUIVO docs/relatorio.md>>>\n# Título\n\nTexto\n")!.Value;
        Assert.Equal(ActionProtocol.Kind.File, action.Kind);
        Assert.Equal("docs/relatorio.md", action.Path);
        Assert.Equal("# Título\n\nTexto", action.Text);
    }

    [Fact]
    public void Removes_code_fences_around_the_block_content()
    {
        var (_, action) = ActionProtocol.Parse("<<<COMANDO>>>\n```powershell\ndir\n```\n<<<FIM>>>")!.Value;
        Assert.Equal("dir", action.Text);
    }

    [Fact]
    public void Bracket_markers_survive_the_web_pages()
    {
        // Como o Gemini pela conta devolve (o texto da página com o bloco de código refeito em ```).
        var (text, action) = ActionProtocol.Parse("Vou ler.\n[[COMANDO]]\n```powershell\nGet-Content -LiteralPath 'dados.txt'\n```\n[[FIM]]")!.Value;
        Assert.Equal("Vou ler.", text);
        Assert.Equal("Get-Content -LiteralPath 'dados.txt'", action.Text);

        var (_, file) = ActionProtocol.Parse("[[ARQUIVO notas.md]]\n~~~~\n# Notas\n```\ncodigo\n```\n~~~~\n[[FIM]]")!.Value;
        Assert.Equal("notas.md", file.Path);
        Assert.Equal("# Notas\n```\ncodigo\n```", file.Text);
        Assert.Equal(4, ActionProtocol.Parse("[[AGUARDAR 4]]")!.Value.Action.Session);
    }

    [Fact]
    public void Instructions_show_the_bracket_format()
    {
        string text = ActionProtocol.Instructions(@"C:\Pasta");
        Assert.Contains("[[COMANDO]]", text);
        Assert.Contains("[[FIM]]", text);
        Assert.Contains(@"na pasta C:\Pasta", text);
        Assert.DoesNotContain("<<<", text);
    }

    [Fact]
    public void Parses_wait_and_ignores_text_without_action()
    {
        Assert.Equal(7, ActionProtocol.Parse("Aguardando.\n<<<AGUARDAR 7>>>")!.Value.Action.Session);
        Assert.Null(ActionProtocol.Parse("Pronto, o relatório está em relatorio.md."));
        Assert.Null(ActionProtocol.Parse("<<<COMANDO>>>\n\n<<<FIM>>>"));
    }

    [Fact]
    public void Visible_text_stops_before_the_action()
    {
        Assert.Equal("Vou ler.", ActionProtocol.Visible("Vou ler.\n<<<COMANDO>>>\ndir"));
        Assert.Equal("Vou ler.\n", ActionProtocol.Visible("Vou ler.\n<<"));
        Assert.Equal("a < b", ActionProtocol.Visible("a < b"));
        Assert.Equal("Vou ler.", ActionProtocol.Visible("Vou ler.\n[[COMAN"));
    }

    [Fact]
    public void File_command_writes_utf8_without_bom_and_falls_back_to_base64()
    {
        string plain = ActionProtocol.FileCommand("r.md", "linha 1\nlinha 2");
        Assert.Contains("@'\r\nlinha 1\r\nlinha 2\r\n'@", plain);
        Assert.Contains("UTF8Encoding $false", plain);
        string tricky = ActionProtocol.FileCommand("r.md", "x\n'@\ny");
        Assert.Contains("FromBase64String", tricky);
        Assert.DoesNotContain("@'", tricky);
    }

    [Fact]
    public void Call_items_use_the_codex_tools()
    {
        var call = ActionProtocol.CallItem(new ActionProtocol.Request(ActionProtocol.Kind.Command, "dir"), @"C:\Pasta");
        Assert.Equal("function_call", call["type"]!.GetValue<string>());
        Assert.Equal("exec_command", call["name"]!.GetValue<string>());
        var args = JsonNode.Parse(call["arguments"]!.GetValue<string>())!;
        Assert.Equal("dir", args["cmd"]!.GetValue<string>());
        Assert.Equal(@"C:\Pasta", args["workdir"]!.GetValue<string>());
        Assert.StartsWith("call_", call["call_id"]!.GetValue<string>());

        var wait = ActionProtocol.CallItem(new ActionProtocol.Request(ActionProtocol.Kind.Wait, "", null, 3), null);
        Assert.Equal("write_stdin", wait["name"]!.GetValue<string>());
        Assert.Equal(3, JsonNode.Parse(wait["arguments"]!.GetValue<string>())!["session_id"]!.GetValue<int>());
    }

    [Fact]
    public void Steps_count_since_the_last_user_message()
    {
        var c = new Conversation("r", [("user", "a"), (ConversationReader.Action, "1"), (ConversationReader.Result, "x"), ("user", "b"),
            (ConversationReader.Action, "2"), (ConversationReader.Result, "y"), (ConversationReader.Action, "3")], null, null);
        Assert.Equal(2, ActionProtocol.StepsSinceUser(c));
    }

    private static async Task<List<JsonObject>> RunAsync(string answer, JsonObject payload)
    {
        using var ms = new MemoryStream();
        using var run = new CancellationTokenSource();
        var provider = new ScriptedProvider((text, _, _) => { text(answer); return Task.FromResult(new Answer(answer, null)); });
        var model = new AiModel("codexias-teste", "teste", "teste", "Teste", "", [], null, 0);
        await BridgeServer.RespondCoreAsync(ms, model.Slug, (provider, model), payload, false, run, new BridgeConnection(run, false));
        string body = Encoding.UTF8.GetString(ms.ToArray());
        body = body[(body.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        return body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal)))
            .Select(l => JsonNode.Parse(l["data: ".Length..])!.AsObject()).ToList();
    }

    [Fact]
    public async Task An_action_reaches_codex_as_a_tool_call_and_never_as_text()
    {
        var events = await RunAsync("Vou ler os dados.\n[[COMANDO]]\n```powershell\nGet-Content dados.txt\n```\n[[FIM]]", CodexIAsHandoffReaderTests.SolStoppedByQuota());
        var output = events.Single(e => e["type"]!.GetValue<string>() == "response.completed")["response"]!["output"]!.AsArray();
        Assert.Equal(2, output.Count);
        Assert.Equal("Vou ler os dados.", output[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("exec_command", output[1]!["name"]!.GetValue<string>());
        string streamed = string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "response.output_text.delta").Select(e => e["delta"]!.GetValue<string>()));
        Assert.DoesNotContain("[[", streamed);
        Assert.Equal("Get-Content dados.txt", JsonNode.Parse(output[1]!["arguments"]!.GetValue<string>())!["cmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_the_command_tool_the_answer_stays_text()
    {
        var events = await RunAsync("Pronto.\n<<<COMANDO>>>\ndir\n<<<FIM>>>", CodexIAsHandoffReaderTests.SolStoppedByQuota(tools: false));
        var output = events.Single(e => e["type"]!.GetValue<string>() == "response.completed")["response"]!["output"]!.AsArray();
        Assert.Single(output);
    }

    [Fact]
    public void Qira_and_claude_with_commands_act_on_their_own()
    {
        var model = new AiModel("codexias-x", "x", "x", "X", "", [], null, 0);
        Assert.True(BridgeServer.UsesActions((new ScriptedProvider((_, _, _) => Task.FromResult(new Answer("", null))), model), commands: true));
        Assert.False(BridgeServer.UsesActions((new CodexIAs.Providers.ClaudeProvider(), model), commands: true));
        Assert.True(BridgeServer.UsesActions((new CodexIAs.Providers.ClaudeProvider(), model), commands: false));
        Assert.False(BridgeServer.UsesActions(null, commands: false));
    }
}

public class CodexIAsApiAliasHistoryTests
{
    [Fact]
    public void Api_alias_drops_account_reasoning_and_internal_fields()
    {
        var payload = JsonNode.Parse("""
        { "model": "openai-minha-api-sol", "input": [
          { "type": "reasoning", "id": "rs_1", "encrypted_content": "gAAAA" },
          { "type": "message", "role": "user", "content": [], "internal_chat_message_metadata_passthrough": { "turn_id": "t" } } ] }
        """)!.AsObject();
        var api = OpenAiKey.Rewrite(payload);
        var input = api["input"]!.AsArray();
        Assert.Single(input);
        Assert.Null(input[0]!["internal_chat_message_metadata_passthrough"]);
        Assert.Equal("gpt-6.1-sol", api["model"]!.GetValue<string>());
        Assert.Equal(2, payload["input"]!.AsArray().Count); // o pedido original não muda
    }
}
