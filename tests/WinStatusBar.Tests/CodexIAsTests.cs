using System.Text.Json;
using System.Text.Json.Nodes;
using CodexIAs;
using CodexIAs.Providers;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

// IAs da barra no Codex (componente WinStatusBar.CodexIAs, antes o app avulso "Codex IAs").

public class CodexIAsConversationTests
{
    private static JsonNode Payload(string key = "conv-1") => JsonNode.Parse($$"""
        {
          "model": "codexias-claude-opus-5-5", "stream": true, "prompt_cache_key": "{{key}}",
          "reasoning": { "effort": "high" },
          "input": [
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "# AGENTS.md instructions" } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "<environment_context>\n<cwd>C:\\Projetos\\X</cwd>\n</environment_context>" } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "Primeira pergunta" } ] },
            { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "Primeira resposta" } ] },
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "Segunda pergunta" } ] }
          ]
        }
        """)!;

    [Fact]
    public void Le_pedido_historico_pasta_e_chave()
    {
        var c = ConversationReader.Read(Payload());
        Assert.Equal("Segunda pergunta", c.Prompt);
        Assert.Equal(2, c.History.Count);
        Assert.Equal(("user", "Primeira pergunta"), c.History[0]);
        Assert.Equal(("assistant", "Primeira resposta"), c.History[1]);
        Assert.Equal(@"C:\Projetos\X", c.Cwd);
        Assert.Equal("conv-1", c.Key);
    }

    [Fact]
    public void Esforco_do_seletor_e_none_vira_padrao()
    {
        Assert.Equal("high", BridgeServer.EffortOf(Payload()));
        Assert.Null(BridgeServer.EffortOf(JsonNode.Parse("""{"reasoning":{"effort":"none"}}""")!));
        Assert.Null(BridgeServer.EffortOf(JsonNode.Parse("""{}""")!));
    }

    [Fact]
    public void Historico_vai_como_contexto_quando_ha_conversa_anterior()
    {
        string text = ConversationReader.WithTranscript("Agora", [("user", "Oi"), ("assistant", "Olá")]);
        Assert.Contains("Usuário: Oi", text);
        Assert.Contains("Assistente: Olá", text);
        Assert.EndsWith("Agora", text);
        Assert.Equal("Só isto", ConversationReader.WithTranscript("Só isto", []));
    }

    [Fact]
    public void Compactacao_guarda_so_as_ultimas_falas()
    {
        var output = BridgeServer.Compact(Payload())["output"]!.AsArray();
        Assert.Equal(5, output.Count);
    }
}

public class CodexIAsCatalogTests
{
    private static string Catalog(params string[] slugsAndPriorities)
    {
        var models = new JsonArray();
        foreach (var item in slugsAndPriorities)
        {
            int cut = item.LastIndexOf(':');
            models.Add(new JsonObject { ["slug"] = item[..cut], ["priority"] = int.Parse(item[(cut + 1)..]), ["display_name"] = item[..cut], ["visibility"] = "list" });
        }
        return new JsonObject { ["models"] = models }.ToJsonString();
    }

    private static readonly AiModel Opus = new("codexias-claude-opus-5-5", "claude", "claude-opus-5-5", "Claude Opus 5.5", "d", ["low", "medium", "max"], "medium", -6);
    private static readonly AiModel Qira = new("codexias-qira", "qira", "hybrid", "Lenovo Qira — híbrido", "d", [], null, -3);

    private static List<string> Slugs(string json) => JsonNode.Parse(json)!["models"]!.AsArray().Select(n => n!["slug"]!.GetValue<string>()).ToList();

    private static string Apply(string json, params AiModel[] models)
    {
        OpenAiCatalog.State? state = null;
        return CodexCatalog.Apply(json, models, null, ref state);
    }

    private static List<int> Priorities(string json) => JsonNode.Parse(json)!["models"]!.AsArray().Select(n => n!["priority"]!.GetValue<int>()).ToList();

    [Fact]
    public void OpenAI_primeiro_na_ordem_dela_depois_minha_API_e_o_resto_em_ordem_alfabetica()
    {
        string json = Catalog("phi4-mini:-7", "winstatusbar-claude:-6", "gpt-6.1-terra:1", "gpt-6.1-sol:0", "openai-minha-api-gpt-6.2-sol:-8",
            "openai-minha-api-astra:-8", "openai-minha-api-sol:-9", "gemma4:-7");
        string ordered = Apply(json, Opus, Qira);
        Assert.Equal(["gpt-6.1-sol", "gpt-6.1-terra", "openai-minha-api-sol", "openai-minha-api-astra", "openai-minha-api-gpt-6.2-sol",
            "codexias-claude-opus-5-5", "gemma4", "codexias-qira", "phi4-mini"], Slugs(ordered));
        // A conta mantém as prioridades da OpenAI; os demais vêm em sequência logo depois.
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], Priorities(ordered));
    }

    [Fact]
    public void Entradas_ocultas_ficam_no_fim_sem_mudar_a_prioridade()
    {
        var root = JsonNode.Parse(Catalog("gpt-6.1-sol:0", "phi4:-7"))!;
        root["models"]!.AsArray().Insert(0, new JsonObject { ["slug"] = "codex-auto-review", ["priority"] = 50, ["visibility"] = "hide", ["display_name"] = "x" });
        string ordered = Apply(root.ToJsonString());
        Assert.Equal(["gpt-6.1-sol", "phi4", "codex-auto-review"], Slugs(ordered));
        Assert.Equal(50, Priorities(ordered)[2]);
    }

    [Fact]
    public void Aplicar_de_novo_nao_muda_nada_e_tira_o_que_saiu()
    {
        string once = Apply(Catalog("phi4:-7", "gpt-6.1-sol:0", "gpt-6-luna:5"), Opus, Qira);
        Assert.Equal(once, Apply(once, Opus, Qira));
        Assert.Equal(["gpt-6.1-sol", "gpt-6-luna", "codexias-claude-opus-5-5", "codexias-qira", "phi4"], Slugs(once));
        Assert.Equal(["gpt-6.1-sol", "gpt-6-luna", "codexias-claude-opus-5-5", "phi4"], Slugs(Apply(once, Opus)));
    }

    [Fact]
    public void Entrada_tem_niveis_do_modelo_e_padrao()
    {
        var entry = CodexCatalog.Entry(Opus);
        Assert.Equal("medium", entry["default_reasoning_level"]!.GetValue<string>());
        Assert.Equal(["low", "medium", "max"], entry["supported_reasoning_levels"]!.AsArray().Select(l => l!["effort"]!.GetValue<string>()).ToArray());
        var none = CodexCatalog.Entry(Qira);
        Assert.Equal("none", none["default_reasoning_level"]!.GetValue<string>());
        Assert.Single(none["supported_reasoning_levels"]!.AsArray());
    }
}

public class CodexIAsOpenAiTests
{
    public CodexIAsOpenAiTests() => CodexIAs.Log.Enabled = false;

    private static JsonArray Bundled(params (string Slug, int Priority, string Visibility, bool Api)[] models)
        => new(models.Select(m => (JsonNode)new JsonObject
        {
            ["slug"] = m.Slug, ["display_name"] = m.Slug.ToUpperInvariant(), ["priority"] = m.Priority, ["visibility"] = m.Visibility,
            ["supported_in_api"] = m.Api, ["supported_reasoning_levels"] = new JsonArray(new JsonObject { ["effort"] = "medium", ["description"] = "m" }),
        }).ToArray());

    private static JsonArray Catalog(params (string Slug, int Priority)[] models)
        => new(models.Select(m => (JsonNode)new JsonObject { ["slug"] = m.Slug, ["priority"] = m.Priority, ["visibility"] = "list", ["display_name"] = "conta " + m.Slug }).ToArray());

    private static List<string> Slugs(JsonArray list) => list.Select(n => n!["slug"]!.GetValue<string>()).ToList();

    [Fact]
    public void Primeira_vez_so_registra_o_que_existe_sem_acrescentar_modelos_antigos()
    {
        OpenAiCatalog.State? state = null;
        var list = Catalog(("gpt-6.1-sol", 0), ("phi4", -7));
        Assert.False(OpenAiCatalog.MergeInto(list, Bundled(("gpt-6.1-sol", 2, "list", true), ("gpt-5.5", 13, "list", true)), ref state));
        Assert.Equal(["gpt-6.1-sol", "phi4"], Slugs(list));
        Assert.NotNull(state);
        Assert.Contains("gpt-5.5", state!.Conhecidos);
        Assert.Empty(state.Adicionados);
    }

    [Fact]
    public void Modelo_novo_entra_com_a_opcao_da_minha_API_e_nao_mexe_nas_entradas_da_conta()
    {
        OpenAiCatalog.State? state = new() { Conhecidos = ["gpt-6.1-sol", "gpt-5.5", "phi4"] };
        var list = Catalog(("phi4", -7), ("gpt-6.1-sol", 0), ("gpt-6-luna", 5));
        var bundled = Bundled(("gpt-6.1-sol", 2, "list", true), ("gpt-6.2-sol", 1, "list", true), ("gpt-interno", 0, "hide", true));
        var changes = new OpenAiCatalog.Changes();
        Assert.True(OpenAiCatalog.MergeInto(list, bundled, ref state, changes));
        Assert.Equal(["phi4", "gpt-6.1-sol", "gpt-6.2-sol", "gpt-6-luna", "openai-minha-api-gpt-6.2-sol"], Slugs(list));
        Assert.Equal("conta gpt-6.1-sol", list[1]!["display_name"]!.GetValue<string>()); // entrada da conta intacta
        Assert.Equal("OpenAI — minha API — GPT-6.2-SOL", list[4]!["display_name"]!.GetValue<string>());
        Assert.Equal(["gpt-6.2-sol"], state!.Adicionados);
        Assert.Equal(["GPT-6.2-SOL"], changes.Novos);
        Assert.False(OpenAiCatalog.MergeInto(list, bundled, ref state)); // de novo: nada muda
    }

    [Fact]
    public void Modelo_que_o_app_acrescentou_e_sumiu_do_Codex_sai_com_a_opcao_da_API()
    {
        OpenAiCatalog.State? state = new() { Conhecidos = ["gpt-6.1-sol", "gpt-6.2-sol"], Adicionados = ["gpt-6.2-sol"] };
        var list = Catalog(("gpt-6.1-sol", 0), ("gpt-6.2-sol", 1), ("openai-minha-api-gpt-6.2-sol", -8), ("gpt-reserve", 9));
        var changes = new OpenAiCatalog.Changes();
        Assert.True(OpenAiCatalog.MergeInto(list, Bundled(("gpt-6.1-sol", 2, "list", true)), ref state, changes));
        Assert.Equal(["gpt-6.1-sol", "gpt-reserve"], Slugs(list)); // gpt-reserve (da conta, nome sem versão) fica
        Assert.Empty(state!.Adicionados);
        Assert.Equal(["conta gpt-6.2-sol"], changes.Sairam);
    }

    [Fact]
    public void Modelo_da_conta_fora_da_lista_do_Codex_so_sai_quando_pedido()
    {
        OpenAiCatalog.State? state = new() { Conhecidos = ["gpt-6.1-sol", "gpt-5.6-luna"] };
        var list = Catalog(("gpt-6.1-sol", 0), ("gpt-5.6-luna", 8), ("gpt-oss:20b", 20), ("openai-minha-api-sol", 9));
        var bundled = Bundled(("gpt-6.1-sol", 2, "list", true));
        var changes = new OpenAiCatalog.Changes();
        Assert.False(OpenAiCatalog.MergeInto(list, bundled, ref state, changes));
        Assert.Equal(["conta gpt-5.6-luna"], changes.PossiveisObsoletos); // modelo do Ollama (":") e atalho da API não contam
        Assert.Equal(4, list.Count);

        changes = new OpenAiCatalog.Changes();
        Assert.True(OpenAiCatalog.MergeInto(list, bundled, ref state, changes, removeObsolete: true));
        Assert.Equal(["gpt-6.1-sol", "gpt-oss:20b", "openai-minha-api-sol"], Slugs(list));
        Assert.Equal(["conta gpt-5.6-luna"], changes.ObsoletosRemovidos);
    }

    [Fact]
    public void Sem_a_lista_do_Codex_nada_e_apontado_como_obsoleto()
    {
        OpenAiCatalog.State? state = new() { Conhecidos = ["gpt-6.1-sol"] };
        var list = Catalog(("gpt-6.1-sol", 0));
        var changes = new OpenAiCatalog.Changes();
        OpenAiCatalog.MergeInto(list, new JsonArray(), ref state, changes, removeObsolete: true);
        Assert.Empty(changes.PossiveisObsoletos);
        Assert.Single(list);
    }

    [Fact]
    public void Modelo_sem_API_nao_ganha_opcao_da_minha_API()
    {
        OpenAiCatalog.State? state = new() { Conhecidos = ["gpt-6.1-sol"] };
        var list = Catalog(("gpt-6.1-sol", 0));
        Assert.True(OpenAiCatalog.MergeInto(list, Bundled(("gpt-6.1-sol", 2, "list", true), ("gpt-7-codex", 1, "list", false)), ref state));
        Assert.Equal(["gpt-6.1-sol", "gpt-7-codex"], Slugs(list));
    }

    [Fact]
    public void Modelo_de_geracao_mais_antiga_que_a_da_conta_nao_entra()
    {
        OpenAiCatalog.State? state = new() { Conhecidos = ["gpt-6.1-sol"] };
        var list = Catalog(("gpt-6.1-sol", 0), ("gpt-5.6-luna", 8));
        var bundled = Bundled(("gpt-6.1-sol", 2, "list", true), ("gpt-5.5", 13, "list", true), ("gpt-6.1-nova", 3, "list", true), ("gpt-7", 1, "list", true));
        Assert.True(OpenAiCatalog.MergeInto(list, bundled, ref state));
        Assert.DoesNotContain("gpt-5.5", Slugs(list));
        Assert.Contains("gpt-6.1-nova", Slugs(list)); // mesma geração da mais nova da conta
        Assert.Contains("gpt-7", Slugs(list));
        Assert.Contains("gpt-5.5", state!.Conhecidos); // registrado: não é avaliado de novo
        Assert.Equal(new Version(6, 1), OpenAiCatalog.VersionOf("gpt-6.1-sol"));
        Assert.Null(OpenAiCatalog.VersionOf("o4-mini"));
    }

    [Fact]
    public void Atalhos_fixos_da_minha_API_sao_reconhecidos()
    {
        Assert.True(OpenAiCatalog.IsFixedAlias("openai-minha-api-sol"));
        Assert.True(OpenAiCatalog.IsFixedAlias("openai-minha-api-astra"));
        Assert.False(OpenAiCatalog.IsFixedAlias("openai-minha-api-gpt-6.2-sol"));
        Assert.True(CodexCatalog.IsOpenAi("gpt-6.2-sol"));
        Assert.True(CodexCatalog.IsOpenAi("o4-mini"));
        Assert.False(CodexCatalog.IsOpenAi("codexias-copilot-gpt-gpt-5-6-sol-think-deeper"));
        Assert.False(CodexCatalog.IsOpenAi("gemma4:26b-a4b-it-qat"));
    }

    [Fact]
    public void Pedido_da_minha_API_vai_com_o_modelo_de_verdade_e_so_os_campos_da_API_publica()
    {
        var fixedAlias = OpenAiKey.Rewrite(JsonNode.Parse("""{"model":"openai-minha-api-sol","stream":true,"reasoning":{"effort":"high","summary":"auto"},"client_metadata":{"x":1},"input":[]}""")!.AsObject());
        Assert.Equal("gpt-6.1-sol", fixedAlias["model"]!.GetValue<string>());
        Assert.Equal("medium", fixedAlias["reasoning"]!["effort"]!.GetValue<string>()); // nível do atalho
        Assert.Equal("auto", fixedAlias["reasoning"]!["summary"]!.GetValue<string>());
        Assert.Null(fixedAlias["client_metadata"]); // campo próprio da conta do ChatGPT fica de fora
        var generic = OpenAiKey.Rewrite(JsonNode.Parse("""{"model":"openai-minha-api-gpt-6.2-sol","reasoning":{"effort":"xhigh"}}""")!.AsObject());
        Assert.Equal("gpt-6.2-sol", generic["model"]!.GetValue<string>());
        Assert.Equal("xhigh", generic["reasoning"]!["effort"]!.GetValue<string>()); // nível do seletor
        Assert.True(OpenAiKey.IsAlias("openai-minha-api-gpt-7"));
        Assert.False(OpenAiKey.IsAlias("openai-minha-api-"));
        Assert.False(OpenAiKey.IsAlias("gpt-6.1-sol"));
    }
}

public class CodexIAsConfigTests
{
    private const string Toml = "model = \"gpt-6.1-sol\"\r\nopenai_base_url = \"http://127.0.0.1:11434/api/codex/v1\"\r\n\r\n[profiles.x]\r\nopenai_base_url = \"https://outro\"\r\n";

    [Fact]
    public void Le_so_as_chaves_do_nivel_superior()
    {
        Assert.Equal("http://127.0.0.1:11434/api/codex/v1", CodexConfig.Get(Toml, "openai_base_url"));
        Assert.Null(CodexConfig.Get(Toml, "model_catalog_json"));
        Assert.Equal(@"C:\Users\x\.codex\m.json", CodexConfig.Get("model_catalog_json = 'C:\\Users\\x\\.codex\\m.json'".Replace("\\\\", "\\"), "model_catalog_json"));
        Assert.Equal(@"C:\Users\x\m.json", CodexConfig.Get("model_catalog_json = \"C:\\\\Users\\\\x\\\\m.json\"", "model_catalog_json"));
    }

    [Fact]
    public void Troca_acrescenta_e_tira_sem_mexer_nas_tabelas()
    {
        string changed = CodexConfig.Set(Toml, "openai_base_url", "http://127.0.0.1:11435/v1");
        Assert.Equal("http://127.0.0.1:11435/v1", CodexConfig.Get(changed, "openai_base_url"));
        Assert.Contains("[profiles.x]\r\nopenai_base_url = \"https://outro\"", changed); // tabela intacta, quebras de linha mantidas
        string added = CodexConfig.Set(changed, "model_catalog_json", @"C:\Users\x\.codex\winstatusbar-modelos.json");
        Assert.StartsWith("model_catalog_json = \"C:\\\\Users\\\\x\\\\.codex\\\\winstatusbar-modelos.json\"", added);
        Assert.Equal(@"C:\Users\x\.codex\winstatusbar-modelos.json", CodexConfig.Get(added, "model_catalog_json"));
        string removed = CodexConfig.Set(added, "openai_base_url", null);
        Assert.Null(CodexConfig.Get(removed, "openai_base_url"));
        Assert.Contains("openai_base_url = \"https://outro\"", removed);
    }

    [Fact]
    public void Reconhece_o_endereco_do_encaminhador()
    {
        Assert.True(CodexConfig.IsGateway("http://127.0.0.1:11435/api/codex/v1"));
        Assert.True(CodexConfig.IsGateway("http://localhost:11435/v1"));
        Assert.False(CodexConfig.IsGateway("http://127.0.0.1:11434/api/codex/v1"));
        Assert.False(CodexConfig.IsGateway(null));
    }

    [Fact]
    public void Caminho_depois_do_endereco_base()
    {
        Assert.Equal("/responses", Gateway.Rest("/v1/responses", "/v1"));
        Assert.Equal("/responses/compact", Gateway.Rest("/api/codex/v1/responses/compact", "/api/codex/v1"));
        Assert.Equal("/responses", Gateway.Rest("/api/codex/v1/responses", "/v1")); // endereço antigo do Ollama também vale
        Assert.Null(Gateway.Rest("/health", "/v1"));
    }

    [Fact]
    public void Relatorio_vai_e_volta_igual()
    {
        var report = new CodexIntegration.Report(new DateTime(2026, 11, 8, 20, 0, 0), "1.3.0",
            [new("Claude", 5, null, false, ["Claude Opus 5.6"], []), new("DeepSeek", 0, "sem chave da API na barra", false, [], [])],
            new(true, true, ["GPT-6.2-Sol"], [], ["gpt-5.6-luna"], [], null));
        var back = CodexIntegration.ParseReport(CodexIntegration.Serialize(report))!;
        Assert.Equal("Claude Opus 5.6", back.Servicos[0].Novos.Single());
        Assert.Equal("sem chave da API na barra", back.Servicos[1].Motivo);
        Assert.Equal(["gpt-5.6-luna"], back.Codex.PossiveisObsoletos);
    }

    [Fact]
    public void Ia_fora_do_Codex_tem_motivo_e_falha_passageira_mantem_as_opcoes()
    {
        Assert.False(Listing.Off("sem login").Failed);
        Assert.True(Listing.Fail("a página não carregou").Failed);
        Assert.Empty(Listing.Off("x").Models);
    }
}

public class CodexIAsClaudeTests
{
    private const string Models = """
        [
          {"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","description":"Opus 5.5 · Best for everyday, complex tasks","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"]},
          {"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5","description":"Best for everyday, complex tasks","supportedEffortLevels":["low","medium","high","xhigh","max"]},
          {"value":"claude-haiku-4-5-20251001","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5","description":"Fastest for quick answers"},
          {"value":"claude-opus-4-6","resolvedModel":"claude-opus-4-6","displayName":"Opus 4.6","description":"Best for everyday, complex tasks","supportedEffortLevels":["low","medium","high","max"]}
        ]
        """;

    [Fact]
    public void Espelha_a_lista_do_Claude_Code_com_os_niveis_de_cada_modelo()
    {
        using var doc = JsonDocument.Parse(Models);
        var list = ClaudeProvider.ToModels(ClaudeModelList.Parse(doc.RootElement));
        Assert.Equal(["codexias-claude-opus-5-5", "codexias-claude-haiku-4-5-20251001", "codexias-claude-opus-4-6"], list.Select(m => m.Slug).ToArray());
        Assert.Equal("Claude Opus 5.5", list[0].DisplayName);
        Assert.Equal("claude-opus-5-5", list[0].Id); // apelido vira o modelo de verdade
        Assert.Empty(list[1].Efforts);
        Assert.Null(list[1].DefaultEffort);
        Assert.Equal(["low", "medium", "high", "max"], list[2].Efforts);
        Assert.Equal("medium", list[2].DefaultEffort);
    }

    [Fact]
    public void Conta_conectada_pelo_initialize()
    {
        using var on = JsonDocument.Parse("""{"account":{"email":"a@b.c","subscriptionType":"Claude Pro"}}""");
        using var plan = JsonDocument.Parse("""{"account":{"subscriptionType":"Claude Max"}}""");
        using var off = JsonDocument.Parse("""{"account":{"email":"","apiProvider":"firstParty"}}""");
        using var none = JsonDocument.Parse("""{"models":[]}""");
        Assert.True(ClaudeModelList.SignedIn(on.RootElement));
        Assert.True(ClaudeModelList.SignedIn(plan.RootElement));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            Assert.False(ClaudeModelList.SignedIn(off.RootElement));
            Assert.False(ClaudeModelList.SignedIn(none.RootElement));
        }
    }
}

public class CodexIAsQiraTests
{
    [Fact]
    public void Le_o_texto_acumulado_do_fluxo()
    {
        var acc = new QiraProtocol.Accumulator();
        acc.Add(QiraProtocol.Parse("""{"jobId":1,"status":"in_progress","data":{"text":"{\"type\":\"text\",\"response\":\"Olá\"}"}}""")!);
        acc.Add(QiraProtocol.Parse("""{"jobId":1,"status":"complete","data":{"text":"{\"type\":\"text\",\"response\":\"Olá, tudo bem?\"}"}}""")!);
        Assert.True(acc.Done);
        Assert.Equal("Olá, tudo bem?", acc.Final());
    }

    [Fact]
    public void Historico_no_formato_do_Qira()
    {
        string text = QiraProtocol.WithHistory("Agora", [("user", "Oi"), ("assistant", "Olá")]);
        Assert.StartsWith("[Earlier in this conversation]", text);
        Assert.EndsWith("Agora", text);
    }
}

public class CodexIAsWebModesTests
{
    [Fact]
    public void Cada_modo_do_site_vira_uma_opcao_e_sem_modos_fica_uma_so()
    {
        var p = new WebChatProvider(WebSite.Gemini);
        var none = p.ToModels(null);
        Assert.Single(none);
        Assert.Equal("codexias-gemini", none[0].Slug);
        var modes = p.ToModels([new(["Rápido"], "Responde rápido", false), new(["Raciocínio"], "", false), new(["Pro"], "", true)]);
        Assert.Equal(["codexias-gemini-rapido", "codexias-gemini-raciocinio"], modes.Select(m => m.Slug).ToArray());
        Assert.Equal("Gemini — Rápido", modes[0].DisplayName);
        Assert.Equal("Rápido", modes[0].Id);
    }

    [Fact]
    public void Modo_de_submenu_vira_opcao_com_o_caminho_no_id()
    {
        var p = new WebChatProvider(WebSite.Copilot);
        var m = p.ToModels([new(["GPT", "GPT-5.6 Sol Think Deeper"], "OpenAI", false)]).Single();
        Assert.Equal("codexias-copilot-gpt-gpt-5-6-sol-think-deeper", m.Slug);
        Assert.Equal("Microsoft Copilot — GPT-5.6 Sol Think Deeper", m.DisplayName);
        Assert.Equal(["GPT", "GPT-5.6 Sol Think Deeper"], WebModePicker.Mode.ParseId(m.Id));
    }

    [Fact]
    public void Modo_atual_salvo_volta_com_o_mesmo_caminho()
    {
        var mode = new WebModePicker.Mode(["GPT", "GPT-5.6 Sol Resposta rápida"], "OpenAI", false);
        Assert.Equal("GPT › GPT-5.6 Sol Resposta rápida", mode.Id);
        Assert.Equal(mode.Path, WebModePicker.Mode.ParseId(mode.Id));
        Assert.Equal("GPT-5.6 Sol Resposta rápida", mode.Label);
    }

    [Fact]
    public void Modos_guardados_sao_lidos_pela_barra()
    {
        var modes = CodexIntegration.ParseSiteModes("""{"copilot":[{"path":["GPT","GPT-5.6 Sol Think Deeper"],"description":"OpenAI"},{"path":[]}],"gemini":[]}""", "copilot");
        Assert.Equal("GPT › GPT-5.6 Sol Think Deeper", modes.Single().Id);
        Assert.Equal("GPT-5.6 Sol Think Deeper", modes.Single().Label);
        Assert.Empty(CodexIntegration.ParseSiteModes("""{"gemini":[]}""", "copilot"));
        Assert.Empty(CodexIntegration.ParseSiteModes("isto não é json", "copilot"));
    }

    [Fact]
    public void Aviso_provisorio_da_pagina_nao_e_resposta()
    {
        Assert.True(WebChatProvider.LooksLikeStatus("Alinhando tudo..."));
        Assert.True(WebChatProvider.LooksLikeStatus("Pensando…"));
        Assert.False(WebChatProvider.LooksLikeStatus("Um número primo tem exatamente dois divisores."));
        Assert.False(WebChatProvider.LooksLikeStatus("Linha um...\nLinha dois..."));
        Assert.True(BridgeServer.IsStatusLine("*Alinhando tudo...*"));
    }

    [Fact]
    public void Slug_sem_acentos_e_pontos_viram_hifen()
    {
        Assert.Equal("opus-4-6", Slug.From("Opus 4.6"));
        Assert.Equal("pensar-a-fundo", Slug.From("Pensar a fundo"));
        Assert.Equal("raciocinio", Slug.From("Raciocínio"));
    }

    [Fact]
    public void Resposta_estavel_so_conclui_depois_de_parar_de_gerar()
    {
        var s = new ResponseState();
        Assert.False(s.Observe("abc", true, TimeSpan.FromSeconds(0)));
        Assert.False(s.Observe("abc", false, TimeSpan.FromSeconds(1)));
        Assert.False(s.Observe("abc", false, TimeSpan.FromSeconds(2)));
        Assert.False(s.Observe("abc", false, TimeSpan.FromSeconds(3)));
        Assert.True(s.Observe("abc", false, TimeSpan.FromSeconds(4.5)));
    }
}

public class CodexIAsLocalAndApiTests
{
    public CodexIAsLocalAndApiTests() => CodexIAs.Log.Enabled = false;

    [Fact]
    public void So_modelos_de_conversa_das_APIs_entram_no_Codex()
    {
        Assert.True(ApiProvider.IsChatModel("models/gemini-2.5-flash", "Gemini 2.5 Flash"));
        Assert.True(ApiProvider.IsChatModel("models/gemma-4-26b-a4b-it", "Gemma 4 26B A4B IT"));
        Assert.True(ApiProvider.IsChatModel("deepseek-chat", "deepseek-chat"));
        Assert.True(ApiProvider.IsChatModel("grok-3", "grok-3"));
        Assert.False(ApiProvider.IsChatModel("models/gemini-2.5-flash-preview-tts", "Gemini 2.5 Flash Preview TTS"));
        Assert.False(ApiProvider.IsChatModel("models/gemini-embedding-001", "Gemini Embedding 001"));
        Assert.False(ApiProvider.IsChatModel("models/gemini-3.1-flash-live-preview", "Gemini 3.1 Flash Live Preview"));
        Assert.False(ApiProvider.IsChatModel("models/gemini-3-pro-image-preview", "Nano Banana Pro"));
        Assert.False(ApiProvider.IsChatModel("models/aqa", "Model that performs Attributed Question Answering."));
        Assert.False(ApiProvider.IsChatModel("grok-2-image", "grok-2-image"));
        Assert.False(ApiProvider.IsChatModel("models/deep-research-pro-preview", "Deep Research Pro Preview"));
    }

    private static JsonArray Catalog() => new(
        new JsonObject { ["slug"] = "gpt-6.1-sol", ["display_name"] = "GPT-6.1 Sol", ["visibility"] = "list", ["base_instructions"] = "conta" },
        new JsonObject { ["slug"] = "phi4-mini:latest", ["display_name"] = "Phi-4 mini — local", ["visibility"] = "list", ["base_instructions"] = "codex", ["context_window"] = 131072 },
        new JsonObject { ["slug"] = "velho:7b", ["display_name"] = "Velho 7B", ["visibility"] = "list", ["base_instructions"] = "codex" });

    private static JsonObject Routing() => new()
    {
        ["models"] = new JsonArray(new JsonObject { ["slug"] = "phi4-mini:latest" }, new JsonObject { ["slug"] = "velho:7b" }),
        ["auto_review_model"] = "selected",
    };

    [Fact]
    public void Modelo_novo_do_Ollama_entra_e_o_apagado_sai()
    {
        var catalog = Catalog();
        var routing = Routing();
        var installed = new[] { new OllamaCatalog.Local("phi4-mini:latest", false, false, 131072), new OllamaCatalog.Local("qwen3:8b", true, true, 40960) };
        var (added, removed) = OllamaCatalog.MergeInto(catalog, routing, installed);
        Assert.Equal(["qwen3:8b — local"], added);
        Assert.Equal(["Velho 7B"], removed);
        var slugs = catalog.Select(n => n!["slug"]!.GetValue<string>()).ToList();
        Assert.Equal(["gpt-6.1-sol", "phi4-mini:latest", "qwen3:8b"], slugs);
        var qwen = catalog[2]!;
        Assert.Equal("codex", qwen["base_instructions"]!.GetValue<string>()); // mesmas instruções das outras entradas do Ollama
        Assert.Equal(40960, qwen["context_window"]!.GetValue<int>());
        Assert.Equal("high", qwen["default_reasoning_level"]!.GetValue<string>());
        Assert.Equal(["text", "image"], qwen["input_modalities"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var routes = routing["models"]!.AsArray();
        Assert.Equal(["phi4-mini:latest", "qwen3:8b"], routes.Select(r => r!["slug"]!.GetValue<string>()).ToArray());
        // Pensamento liga/desliga como Baixo/Alto (o Codex não mostra "none"; com só "Alto" não havia o que escolher).
        Assert.Equal(["low", "high"], routes[1]!["thinking"]!["levels"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(["low", "high"], qwen["supported_reasoning_levels"]!.AsArray().Select(x => x!["effort"]!.GetValue<string>()).ToArray());
        Assert.Equal("selected", routing["auto_review_model"]!.GetValue<string>()); // o resto do roteamento fica igual
        Assert.Equal(2, OllamaCatalog.Count(routing));

        var again = OllamaCatalog.MergeInto(catalog, routing, installed); // de novo: nada muda
        Assert.Empty(again.Added);
        Assert.Empty(again.Removed);
    }

    [Fact]
    public void Modelo_do_Ollama_que_pensa_passa_de_none_high_para_Baixo_Alto()
    {
        // Como o "ollama launch codex" grava (09/11/2026): o Codex só mostrava "Alto" para o qwen3.5:4b.
        static JsonObject Thinking(params string[] levels) => new()
        {
            ["supported"] = true, ["levels"] = new JsonArray(levels.Select(l => (JsonNode)l).ToArray()),
            ["values"] = new JsonObject(levels.Select(l => KeyValuePair.Create(l, (JsonNode?)JsonValue.Create(l))).ToArray()),
        };
        static JsonObject Entry(string slug, string def, params string[] levels) => new()
        {
            ["slug"] = slug, ["display_name"] = slug, ["visibility"] = "list", ["default_reasoning_level"] = def,
            ["supported_reasoning_levels"] = new JsonArray(levels.Select(l => (JsonNode)new JsonObject { ["effort"] = l }).ToArray()),
        };
        var catalog = new JsonArray(Entry("qwen3.5:4b", "none", "none", "high"), Entry("phi4-mini:latest", "none", "none"), Entry("gpt-oss:20b", "medium", "low", "medium", "high"));
        var routing = new JsonObject
        {
            ["models"] = new JsonArray(
                new JsonObject { ["slug"] = "qwen3.5:4b", ["thinking"] = Thinking("none", "high") },
                new JsonObject { ["slug"] = "phi4-mini:latest", ["thinking"] = Thinking("none") },
                new JsonObject { ["slug"] = "gpt-oss:20b", ["thinking"] = Thinking("low", "medium", "high") }),
        };
        var installed = new[] { new OllamaCatalog.Local("qwen3.5:4b", true, true, 32768), new OllamaCatalog.Local("phi4-mini:latest", false, false, 131072), new OllamaCatalog.Local("gpt-oss:20b", true, false, 131072) };
        OllamaCatalog.MergeInto(catalog, routing, installed);

        static string[] Levels(JsonNode? n) => n!.AsArray().Select(x => (x is JsonObject o ? o["effort"] : x)!.GetValue<string>()).ToArray();
        var routes = routing["models"]!.AsArray();
        Assert.Equal(["low", "high"], Levels(routes[0]!["thinking"]!["levels"]));
        var values = routes[0]!["thinking"]!["values"]!;
        Assert.False(values["low"]!.GetValue<bool>()); // Baixo = sem raciocínio (mais rápido)
        Assert.False(values["none"]!.GetValue<bool>()); // pedidos antigos com "none" continuam sem raciocínio
        Assert.True(values["high"]!.GetValue<bool>());
        Assert.Equal(["low", "high"], Levels(catalog[0]!["supported_reasoning_levels"]));
        Assert.Equal("low", catalog[0]!["default_reasoning_level"]!.GetValue<string>()); // "none" vira o equivalente
        // Quem não pensa e quem já tem níveis de verdade (gpt-oss) não mudam.
        Assert.Equal(["none"], Levels(routes[1]!["thinking"]!["levels"]));
        Assert.Equal(["low", "medium", "high"], Levels(routes[2]!["thinking"]!["levels"]));
        Assert.Equal("medium", catalog[2]!["default_reasoning_level"]!.GetValue<string>());

        string before = catalog.ToJsonString() + routing.ToJsonString();
        OllamaCatalog.MergeInto(catalog, routing, installed); // de novo: nada muda
        Assert.Equal(before, catalog.ToJsonString() + routing.ToJsonString());
    }
}
