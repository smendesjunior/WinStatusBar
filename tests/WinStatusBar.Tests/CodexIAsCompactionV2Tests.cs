using System.Text;
using System.Text.Json.Nodes;
using CodexIAs;
using CodexIAs.Providers;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Troca de modelo no meio de uma conversa longa (11/11/2026): o Codex compactava antes de enviar à IA da barra, e o
/// encaminhador respondia como a uma pergunta comum ("expected exactly one compaction output item").
/// </summary>
public class CodexIAsCompactionV2Tests
{
    private static JsonObject Payload(bool trigger, bool stream = true)
    {
        var input = new JsonArray
        {
            new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Analise o processo do Fulano." }) },
            new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Consolidei 12 itens." }) },
            new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "pode dar como finalizado" }) },
        };
        if (trigger) input.Add(new JsonObject { ["type"] = "compaction_trigger" });
        return new JsonObject { ["model"] = "codexias-gemini-ajuda-para-tudo", ["input"] = input, ["stream"] = stream };
    }

    [Fact]
    public void Reconhece_so_o_pedido_com_compaction_trigger()
    {
        Assert.True(CompactionV2.Requested(Payload(trigger: true)));
        Assert.False(CompactionV2.Requested(Payload(trigger: false)));
    }

    [Fact]
    public async Task Streaming_tem_exatamente_um_item_compaction()
    {
        using var ms = new MemoryStream();
        await CompactionV2.RespondAsync(ms, "codexias-gemini-ajuda-para-tudo", Payload(trigger: true));
        string text = Encoding.UTF8.GetString(ms.ToArray());
        Assert.StartsWith("HTTP/1.1 200 OK", text);
        var events = text.Split("\n\n").Where(b => b.Contains("data: ")).Select(b => JsonNode.Parse(b[(b.IndexOf("data: ") + 6)..])!).ToList();
        var done = events.Where(e => e["type"]!.GetValue<string>() == "response.output_item.done").ToList();
        Assert.Single(done);
        Assert.Equal("compaction", done[0]["item"]!["type"]!.GetValue<string>());
        var completed = events.Single(e => e["type"]!.GetValue<string>() == "response.completed");
        Assert.Equal("completed", completed["response"]!["status"]!.GetValue<string>());
        string summary = CompactionV2.SummaryOf(done[0]["item"])!;
        Assert.Contains("Analise o processo do Fulano.", summary);
        Assert.Contains("Consolidei 12 itens.", summary);
    }

    [Fact]
    public async Task Sem_streaming_responde_json_com_um_item()
    {
        using var ms = new MemoryStream();
        await CompactionV2.RespondAsync(ms, "codexias-x", Payload(trigger: true, stream: false));
        string text = Encoding.UTF8.GetString(ms.ToArray());
        var body = JsonNode.Parse(text[(text.IndexOf("\r\n\r\n") + 4)..])!;
        Assert.Equal("compaction", body["output"]!.AsArray().Single()!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Resumo_volta_como_historico_no_pedido_seguinte()
    {
        var payload = new JsonObject
        {
            ["input"] = new JsonArray
            {
                CompactionV2.Item("Usuário: pediu o despacho.\nAssistente: entregou o PDF."),
                new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "e agora?" }) },
            },
        };
        var c = ConversationReader.Read(payload);
        Assert.Equal("e agora?", c.Prompt);
        Assert.Contains(c.History, h => h.Role == ConversationReader.Summary && h.Content.Contains("entregou o PDF"));
        Assert.Contains("Resumo da conversa anterior", ConversationReader.Transcript(c.History, 4000));
    }

    [Fact]
    public void Compactacao_cifrada_pela_OpenAI_e_ignorada_e_nao_e_alterada()
    {
        var real = new JsonObject { ["type"] = "compaction", ["encrypted_content"] = "gAAAAB-cifrado" };
        Assert.Null(CompactionV2.SummaryOf(real));
        var payload = new JsonObject { ["input"] = new JsonArray(real) };
        Assert.False(CompactionV2.Unwrap(payload));
        Assert.Equal("compaction", payload["input"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Resumo_vira_mensagem_comum_antes_de_ir_para_a_OpenAI()
    {
        var payload = new JsonObject { ["input"] = new JsonArray(CompactionV2.Item("resumo X")) };
        Assert.True(CompactionV2.Unwrap(payload));
        var item = payload["input"]![0]!;
        Assert.Equal("message", item["type"]!.GetValue<string>());
        Assert.Contains("resumo X", item["content"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain(CompactionV2.Marker, payload.ToJsonString());
    }

    [Fact]
    public void Catalogo_declara_janela_grande_para_nao_compactar_na_troca()
    {
        var entry = CodexCatalog.Entry(new AiModel("codexias-x", "p", "x", "X", "d", [], null, 0));
        Assert.Equal(CodexCatalog.ContextWindow, entry["context_window"]!.GetValue<int>());
        Assert.Equal(CodexCatalog.ContextWindow, entry["max_context_window"]!.GetValue<int>());
        Assert.True(CodexCatalog.ContextWindow > 872_000); // maior que a maior janela da OpenAI no catálogo: sem "downshift" na troca
        // e pequena o bastante para o pedido inteiro (~4 bytes por token, com folga) caber no limite do encaminhador (64 MB)
        Assert.True((long)CodexCatalog.ContextWindow * 8 < 64L * 1024 * 1024);
    }

    [Fact]
    public void Pedido_grande_demais_e_distinguido_de_pedido_invalido()
    {
        using var compressor = new ZstdSharp.Compressor(1);
        byte[] big = compressor.Wrap(new byte[65 * 1024 * 1024]).ToArray();
        Assert.Null(Gateway.Unzstd(big, out bool tooBig));
        Assert.True(tooBig);
        Assert.Null(Gateway.Unzstd([1, 2, 3], out bool invalidTooBig));
        Assert.False(invalidTooBig);
        byte[] small = compressor.Wrap(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(Gateway.Unzstd(small, out _)!));
    }

    [Fact]
    public void Nome_no_menu_comeca_pelo_modelo()
    {
        var gemini = new ApiProvider(new("gemini", "Google Gemini (API)", "openai", "https://x", "r"));
        Assert.Equal("Gemini 3 Flash Preview · Google API", gemini.MenuName("Gemini 3 Flash Preview"));
        Assert.Equal("Grok 4 · Grok API", new ApiProvider(new("grok", "Grok", "openai", "https://x", "r")).MenuName("Grok 4"));
        Assert.Equal("m · Minha API", new ApiProvider(new("propria-minha-api", "Minha API", "openai", "https://x", "r")).MenuName("m"));
    }
}
