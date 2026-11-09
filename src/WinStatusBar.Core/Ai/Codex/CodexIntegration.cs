using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>
/// O que a barra (Configurações › Modelos) e o componente das IAs no Codex (WinStatusBar.CodexIAs.exe, na pasta
/// Automation da barra) compartilham: o estado da ligação com o Codex, o relatório da última verificação de modelos e os
/// modos lidos dos sites (Gemini, Copilot). Tudo em %LOCALAPPDATA%\WinStatusBar\codex-ias.
/// </summary>
public static class CodexIntegration
{
    /// <summary>Porta do encaminhador do Codex.</summary>
    public const int GatewayPort = 11435;
    public const string ExeName = "WinStatusBar.CodexIAs.exe";
    /// <summary>Cabeçalho dos comandos internos do encaminhador (/codexias/…): navegadores não o enviam sem permissão.</summary>
    public const string CommandHeader = "X-WinStatusBar";
    public const string CommandValue = "codexias";
    /// <summary>Evento que encerra o encaminhador (o instalador usa antes de trocar os arquivos).</summary>
    public const string QuitEventName = @"Local\WinStatusBar.CodexIAs.Sair";
    public const string ProcessName = "WinStatusBar.CodexIAs";

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "codex-ias");
    public static string StateFile => Path.Combine(DataDir, "integracao.json");
    public static string ReportFile => Path.Combine(DataDir, "relatorio.json");
    public static string ModesFile => Path.Combine(DataDir, "modos.json");

    /// <summary>Estado da ligação com o Codex (gravado pelo componente).</summary>
    public sealed class State
    {
        public bool Ligado { get; set; }
        /// <summary>openai_base_url antes de ligar (null: não havia; o Codex ia direto à OpenAI).</summary>
        public string? BaseUrlAnterior { get; set; }
        /// <summary>model_catalog_json antes de ligar (null: não havia).</summary>
        public string? CatalogoAnterior { get; set; }
        /// <summary>O catálogo foi criado ao ligar (sai ao desligar).</summary>
        public bool CatalogoCriado { get; set; }
        /// <summary>Começo do caminho que o Codex usa no encaminhador ("/v1" ou, vindo do Ollama, "/api/codex/v1").</summary>
        public string BasePath { get; set; } = "/v1";
        /// <summary>Para onde vão os pedidos dos modelos da OpenAI (null: direto à OpenAI, pela conta ou pela chave do Codex).</summary>
        public string? Upstream { get; set; }
        public DateTime? LigadoEm { get; set; }
        /// <summary>Cópia do config.toml feita ao ligar.</summary>
        public string? Copia { get; set; }
        /// <summary>Opções "OpenAI — minha API" tiradas ao desligar (precisam do encaminhador); voltam ao ligar de novo.</summary>
        public JsonArray? MinhaApiGuardadas { get; set; }
    }

    /// <summary>
    /// Uma IA na verificação: quantas opções ficaram, o que entrou e saiu, ou por que ficou de fora. <see cref="Id"/> é o
    /// nome usado para verificar só ela de novo ("claude", "gemini", "copilot", "qira", "local", "api-deepseek"…;
    /// "openai", "openai-api" e "ollama" são do próprio seletor do Codex). <see cref="Login"/>: site que pede login.
    /// </summary>
    public sealed record ServiceReport(string Nome, int Total, string? Motivo, bool Falhou, List<string> Novos, List<string> Sairam,
        string? Id = null, string? Login = null);

    /// <summary>O seletor do Codex na verificação (só com a ligação ligada).</summary>
    public sealed record CodexReport(bool Ligado, bool CatalogoAtualizado, List<string> OpenAiNovos, List<string> OpenAiSairam,
        List<string> PossiveisObsoletos, List<string> Removidos, string? Aviso);

    public sealed record Report(DateTime VerificadoEm, string Versao, List<ServiceReport> Servicos, CodexReport Codex);

    /// <summary>Resultado do último comando do componente (ligar, desligar, verificar), para a barra mostrar.</summary>
    public sealed record CommandResult(string Comando, bool Ok, string Mensagem, DateTime Em);

    public static string CommandFile => Path.Combine(DataDir, "comando.json");

    public static void SaveCommand(CommandResult result) => WriteAtomic(CommandFile, JsonSerializer.Serialize(result, Pretty));

    public static CommandResult? LoadCommand()
    {
        try { return File.Exists(CommandFile) ? JsonSerializer.Deserialize<CommandResult>(File.ReadAllText(CommandFile)) : null; } catch { return null; }
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static State LoadState()
    {
        try { if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new(); } catch { }
        return new();
    }

    public static void SaveState(State state) => WriteAtomic(StateFile, JsonSerializer.Serialize(state, Pretty));

    public static Report? LoadReport()
    {
        try { return File.Exists(ReportFile) ? JsonSerializer.Deserialize<Report>(File.ReadAllText(ReportFile)) : null; } catch { return null; }
    }

    public static void SaveReport(Report report) => WriteAtomic(ReportFile, JsonSerializer.Serialize(report, Pretty));

    public static string Serialize(Report report) => JsonSerializer.Serialize(report, Pretty);
    public static Report? ParseReport(string json) { try { return JsonSerializer.Deserialize<Report>(json); } catch { return null; } }

    /// <summary>Modo de um site lido pelo componente: caminho no menu (ex.: ["GPT", "GPT-5.6 Sol Think Deeper"]) e descrição.</summary>
    public sealed record SiteMode(IReadOnlyList<string> Path, string Description)
    {
        public string Id => string.Join(" › ", Path);
        public string Label => Path[^1];
    }

    /// <summary>Modos lidos do site ("gemini" ou "copilot") na última verificação; vazio se nunca leu.</summary>
    public static IReadOnlyList<SiteMode> SiteModes(string site)
    {
        try { return File.Exists(ModesFile) ? ParseSiteModes(File.ReadAllText(ModesFile), site) : []; } catch { return []; }
    }

    /// <summary>{"copilot":[{"path":["GPT","GPT-5.6 Sol Think Deeper"],"description":"…"}], "gemini":[…]} → modos do site.</summary>
    public static IReadOnlyList<SiteMode> ParseSiteModes(string json, string site)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(site, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            return arr.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Object)
                .Select(m => new SiteMode(
                    m.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array
                        ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList() : [],
                    m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : ""))
                .Where(m => m.Path.Count > 0).ToList();
        }
        catch { return []; }
    }

    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
