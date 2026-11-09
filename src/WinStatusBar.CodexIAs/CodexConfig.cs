using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai.Codex;
using State = WinStatusBar.Core.Ai.Codex.CodexIntegration.State;

namespace CodexIAs;

/// <summary>
/// Ligação com o Codex (config.toml), feita só quando a pessoa pede (Configurações › Modelos na barra), com cópia de
/// segurança e como desfazer:
/// - openai_base_url passa a ser o encaminhador (127.0.0.1:11435), que entrega à OpenAI (ou ao endereço que já estava
///   configurado, como o do Ollama) tudo o que não é das IAs da barra;
/// - model_catalog_json: o catálogo que já existia; sem ele, um novo, com os modelos da conta.
/// Desligar devolve os dois valores, tira as entradas da barra do catálogo e o início automático.
/// </summary>
internal static partial class CodexConfig
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WinStatusBar.CodexIAs";
    public static string ConfigFile => Path.Combine(Paths.CodexHome, "config.toml");
    public static string GatewayBase => $"http://127.0.0.1:{Paths.GatewayPort}";
    /// <summary>Catálogo criado ao ligar, quando o Codex não tinha um.</summary>
    public static string OwnCatalog => Path.Combine(Paths.CodexHome, "winstatusbar-modelos.json");

    /// <summary>Codex neste PC: o programa ou a pasta de configuração dele.</summary>
    public static bool CodexFound => OpenAiCatalog.FindCodex() is not null || File.Exists(ConfigFile);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static State Load() => CodexIntegration.LoadState();
    public static void Save(State state) => CodexIntegration.SaveState(state);

    // ------------------------------------------------------------------ TOML (só chaves de nível superior)
    [GeneratedRegex(@"^\s*\[")] private static partial Regex TableLine();

    private static int TopEnd(List<string> lines)
    {
        int i = lines.FindIndex(l => TableLine().IsMatch(l));
        return i < 0 ? lines.Count : i;
    }

    private static Regex KeyLine(string key) => new($@"^\s*{Regex.Escape(key)}\s*=\s*(.*)$");

    /// <summary>Valor de uma chave de texto do nível superior ("..." ou '...'); null se não há.</summary>
    public static string? Get(string toml, string key)
    {
        var lines = toml.Replace("\r\n", "\n").Split('\n').ToList();
        int end = TopEnd(lines);
        var re = KeyLine(key);
        for (int i = 0; i < end; i++)
            if (re.Match(lines[i]) is { Success: true } m) return ParseString(m.Groups[1].Value);
        return null;
    }

    private static string? ParseString(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith('\'') && raw.IndexOf('\'', 1) is int end and > 0) return raw[1..end];
        if (!raw.StartsWith('"')) return null;
        var sb = new StringBuilder();
        for (int i = 1; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i + 1 < raw.Length)
            {
                char n = raw[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
            }
            else sb.Append(c);
        }
        return null;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Troca (ou acrescenta no início) uma chave de texto do nível superior; null tira a chave.</summary>
    public static string Set(string toml, string key, string? value)
    {
        string newline = toml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = toml.Replace("\r\n", "\n").Split('\n').ToList();
        int end = TopEnd(lines);
        var re = KeyLine(key);
        int at = -1;
        for (int i = 0; i < end; i++) if (re.IsMatch(lines[i])) { at = i; break; }
        if (value is null) { if (at >= 0) lines.RemoveAt(at); }
        else if (at >= 0) lines[at] = $"{key} = {Quote(value)}";
        else lines.Insert(0, $"{key} = {Quote(value)}");
        return string.Join(newline, lines);
    }

    /// <summary>Endereço que já aponta para este encaminhador (127.0.0.1 ou localhost, porta 11435).</summary>
    public static bool IsGateway(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port == Paths.GatewayPort && (u.Host is "127.0.0.1" or "localhost");

    private static void WriteConfig(string text)
    {
        Directory.CreateDirectory(Paths.CodexHome);
        string temp = ConfigFile + ".winstatusbar-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, ConfigFile, overwrite: true);
    }

    // ------------------------------------------------------------------ ligar e desligar
    /// <summary>Liga as IAs da barra no Codex. Devolve null quando deu certo; senão, o motivo (nada muda).</summary>
    public static async Task<string?> EnableAsync(CancellationToken ct)
    {
        if (!CodexFound) return "Codex não encontrado neste PC.";
        var state = Load();
        string toml = File.Exists(ConfigFile) ? await File.ReadAllTextAsync(ConfigFile, ct) : "";
        string original = toml;
        string? copy = null;
        string? baseUrl = Get(toml, "openai_base_url"), catalog = Get(toml, "model_catalog_json");

        if (IsGateway(baseUrl))
        {
            // Já aponta para a porta do encaminhador (ex.: o encaminhador antigo, em Python): o endereço fica; o que não é
            // da barra segue para o Ollama quando o caminho é o dele ("/api/codex/v1"), senão direto à OpenAI.
            var u = new Uri(baseUrl!);
            state.BasePath = u.AbsolutePath.TrimEnd('/') is { Length: > 0 } p ? p : "/v1";
            state.Upstream = state.Ligado && state.Upstream is not null ? state.Upstream
                : state.BasePath.StartsWith("/api/codex", StringComparison.Ordinal) ? "http://127.0.0.1:11434" + state.BasePath : null;
            if (!state.Ligado) state.BaseUrlAnterior = baseUrl;
        }
        else
        {
            state.BaseUrlAnterior = baseUrl;
            state.Upstream = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl!.TrimEnd('/');
            state.BasePath = "/v1";
            toml = Set(toml, "openai_base_url", GatewayBase + "/v1");
        }

        if (!state.Ligado) { state.CatalogoAnterior = catalog; state.CatalogoCriado = false; }
        if (catalog is null || !File.Exists(catalog))
        {
            // Sem catálogo próprio, o Codex usa o da conta; para as IAs da barra entrarem no seletor, ele passa a usar um
            // arquivo, começando pelos mesmos modelos da conta.
            var models = await OpenAiCatalog.InitialModelsAsync(ct);
            if (models.Count == 0) return "Não foi possível ler a lista de modelos do Codex (abra o Codex uma vez e tente de novo).";
            if (!File.Exists(OwnCatalog))
                await File.WriteAllTextAsync(OwnCatalog, new JsonObject { ["models"] = models }.ToJsonString(Pretty), new UTF8Encoding(false), ct);
            toml = Set(toml, "model_catalog_json", OwnCatalog);
            state.CatalogoCriado = true;
        }

        // Cópia de segurança só quando o config.toml muda de fato (religar ou corrigir sem mudança não acumula cópias).
        if (toml != original)
        {
            if (original.Length > 0)
            {
                copy = ConfigFile + $".antes-winstatusbar-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(ConfigFile, copy, overwrite: true);
            }
            WriteConfig(toml);
        }
        // Opções "minha API" guardadas ao desligar voltam (o encaminhador está de volta).
        if (state.MinhaApiGuardadas is { Count: > 0 } saved && CodexCatalog.CatalogPath() is { } path && File.Exists(path))
        {
            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject;
                if (root?["models"] is JsonArray list)
                {
                    var present = list.Select(CodexCatalog.SlugOf).ToHashSet(StringComparer.Ordinal);
                    foreach (var n in saved.Where(n => !present.Contains(CodexCatalog.SlugOf(n)))) list.Add(n!.DeepClone());
                    CodexCatalog.WriteAtomic(path, root.ToJsonString(Pretty));
                }
            }
            catch (Exception ex) { Log.Write("Ligar: opções da minha API não voltaram: " + ex.Message); }
        }
        state.MinhaApiGuardadas = null;
        state.Ligado = true;
        state.LigadoEm = DateTime.Now;
        state.Copia = copy ?? state.Copia;
        Save(state);
        SetAutostart(true);
        Log.Write($"Integração com o Codex ligada (encaminhador {GatewayBase}{state.BasePath}; pedidos da OpenAI para {state.Upstream ?? "a OpenAI"})");
        return null;
    }

    /// <summary>Desliga: devolve o config.toml, tira as entradas da barra do catálogo e o início automático.</summary>
    public static async Task<string?> DisableAsync(CancellationToken ct)
    {
        var state = Load();
        SetAutostart(false);
        if (!state.Ligado) return null;
        string toml = File.Exists(ConfigFile) ? await File.ReadAllTextAsync(ConfigFile, ct) : "";
        string? path = CodexCatalog.CatalogPath();
        if (path is not null && File.Exists(path))
        {
            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject;
                if (root?["models"] is JsonArray list)
                {
                    var saved = new JsonArray();
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        string slug = CodexCatalog.SlugOf(list[i]);
                        if (slug.StartsWith(OpenAiCatalog.ApiPrefix, StringComparison.Ordinal)) { var n = list[i]; list.RemoveAt(i); saved.Insert(0, n); }
                        else if (slug.StartsWith(CodexCatalog.Prefix, StringComparison.Ordinal)) list.RemoveAt(i);
                    }
                    state.MinhaApiGuardadas = saved.Count > 0 ? saved : null;
                    CodexCatalog.WriteAtomic(path, root.ToJsonString(Pretty));
                }
            }
            catch (Exception ex) { Log.Write("Desligar: catálogo não ajustado: " + ex.Message); }
        }

        // Só devolve o endereço se ainda for o do encaminhador (a pessoa pode tê-lo trocado depois).
        if (IsGateway(Get(toml, "openai_base_url")))
        {
            string? back = IsGateway(state.BaseUrlAnterior) ? state.Upstream : state.BaseUrlAnterior;
            toml = Set(toml, "openai_base_url", back);
        }
        if (state.CatalogoCriado && string.Equals(Get(toml, "model_catalog_json"), OwnCatalog, StringComparison.OrdinalIgnoreCase))
        {
            toml = Set(toml, "model_catalog_json", state.CatalogoAnterior);
            try { File.Delete(OwnCatalog); } catch { }
        }
        if (File.Exists(ConfigFile)) WriteConfig(toml);
        state.Ligado = false;
        state.CatalogoCriado = false;
        Save(state);
        Log.Write("Integração com o Codex desligada (config.toml devolvido)");
        return null;
    }

    /// <summary>Início com o Windows (só com a integração ligada): o Codex funciona mesmo com a barra fechada.</summary>
    public static void SetAutostart(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on && Environment.ProcessPath is { } exe) key.SetValue(RunValue, $"\"{exe}\"");
            else if (key.GetValue(RunValue) is not null) key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Write("Início automático: " + ex.Message); }
    }

    public static bool AutostartOn()
    {
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(RunValue) is string; } catch { return false; }
    }
}
