using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>O que a continuação passa ao codex exec resume: sempre o modelo, o nível e o acesso do turno que parou.</summary>
public sealed record ResumeSpec(string ThreadId, string Cwd, IReadOnlyList<string> ExtraDirs, string Model, string Effort,
    string SandboxMode, string ApprovalPolicy, bool? NetworkAccess);

/// <summary>O que importa do config.toml do Codex: as exclusões de ambiente dos comandos e o login obrigatório pelo ChatGPT.</summary>
public sealed record CodexConfigFacts(IReadOnlyList<string> EnvExcludes, bool ForcedChatGptLogin);

/// <summary>
/// Monta o codex exec resume na ordem verificada no teste pago:
/// exec --json --skip-git-repo-check -C &lt;pasta&gt; [--add-dir &lt;extra&gt;…] resume &lt;id&gt; -c … -- continue de onde parou.
/// A chave nunca entra nos argumentos (vai só no ambiente do filho) e é excluída do ambiente dos comandos.
/// </summary>
public static class CodexResumeCommand
{
    public const string Message = "continue de onde parou";
    public const string ApiBaseUrl = "https://api.openai.com/v1";
    public static readonly IReadOnlyList<string> BarModelPrefixes = ["codexias-", "openai-minha-api-", "winstatusbar-"];
    public static readonly IReadOnlyList<string> SandboxModes = ["read-only", "workspace-write", "danger-full-access"];
    public static readonly IReadOnlyList<string> ApprovalPolicies = ["never", "on-request", "on-failure", "untrusted"];

    private static readonly Regex ModelPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex EffortPattern = new(@"^[a-z]{1,16}$", RegexOptions.CultureInvariant);
    private const string KeyVariable = "CODEX_API_KEY";

    /// <summary>Problema que impede continuar este turno, ou null. Nunca cai para o nível padrão do modelo.</summary>
    public static ApiResumeProblem? Check(QuotaStop stop, CodexModelLevels.Levels? levels)
    {
        if (string.IsNullOrEmpty(stop.Model) || string.IsNullOrEmpty(stop.Effort)
            || string.IsNullOrEmpty(stop.SandboxMode) || string.IsNullOrEmpty(stop.ApprovalPolicy))
            return ApiResumeProblem.MissingTurnData;
        return Problem(stop.Model, stop.Effort, stop.SandboxMode, stop.ApprovalPolicy, levels);
    }

    private static ApiResumeProblem? Problem(string model, string effort, string sandbox, string approval, CodexModelLevels.Levels? levels)
    {
        if (BarModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return ApiResumeProblem.BarModel;
        if (!ModelPattern.IsMatch(model)) return ApiResumeProblem.MissingTurnData;
        if (!EffortPattern.IsMatch(effort)) return ApiResumeProblem.EffortNotAccepted;
        if (levels is { Efforts.Count: > 0 } && !levels.Efforts.Contains(effort, StringComparer.Ordinal)) return ApiResumeProblem.EffortNotAccepted;
        if (!SandboxModes.Contains(sandbox) || !ApprovalPolicies.Contains(approval)) return ApiResumeProblem.MissingTurnData;
        return null;
    }

    /// <summary>Sempre o turno parado (nunca a marcação atual). Pastas extras = raízes do workspace ∪ graváveis, sem a pasta
    /// principal; as que não existem agora vão para <paramref name="missingExtraDirs"/>.</summary>
    public static ResumeSpec ToSpec(QuotaStop stop, Func<string, bool> dirExists, out IReadOnlyList<string> missingExtraDirs)
    {
        static string Key(string p) => p.TrimEnd('\\', '/');
        string cwd = stop.Cwd ?? "";
        var extra = new List<string>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(cwd) };
        foreach (var dir in stop.ExtraRoots.Concat(stop.WritableRoots))
        {
            if (string.IsNullOrWhiteSpace(dir) || !seen.Add(Key(dir))) continue;
            (dirExists(dir) ? extra : missing).Add(dir);
        }
        missingExtraDirs = missing;
        return new ResumeSpec(stop.ThreadId, cwd, extra, stop.Model ?? "", stop.Effort ?? "", stop.SandboxMode ?? "",
            stop.ApprovalPolicy ?? "", stop.NetworkAccess);
    }

    /// <summary>Argumentos do codex.exe, um elemento por "-c", valores em TOML. Confere tudo de novo e lança ArgumentException.</summary>
    public static IReadOnlyList<string> Arguments(ResumeSpec spec, IReadOnlyList<string> configEnvExcludes)
    {
        if (string.IsNullOrWhiteSpace(spec.ThreadId) || string.IsNullOrWhiteSpace(spec.Cwd))
            throw new ArgumentException("Conversa ou pasta ausente.", nameof(spec));
        var problem = string.IsNullOrEmpty(spec.Model) || string.IsNullOrEmpty(spec.Effort) ? ApiResumeProblem.MissingTurnData
            : Problem(spec.Model, spec.Effort, spec.SandboxMode, spec.ApprovalPolicy, null);
        if (problem is not null) throw new ArgumentException($"Turno inválido para continuar ({problem}).", nameof(spec));

        bool workspaceWrite = spec.SandboxMode == "workspace-write";
        var args = new List<string> { "exec", "--json", "--skip-git-repo-check", "-C", spec.Cwd };
        if (workspaceWrite)
            foreach (var dir in spec.ExtraDirs) { args.Add("--add-dir"); args.Add(dir); }
        args.AddRange(["resume", spec.ThreadId,
            "-c", "openai_base_url=" + Toml(ApiBaseUrl),
            "-c", "model=" + Toml(spec.Model),
            "-c", "model_reasoning_effort=" + Toml(spec.Effort),
            "-c", "sandbox_mode=" + Toml(spec.SandboxMode),
            "-c", "approval_policy=" + Toml(spec.ApprovalPolicy)]);
        if (workspaceWrite && spec.NetworkAccess is { } network)
            args.AddRange(["-c", "sandbox_workspace_write.network_access=" + (network ? "true" : "false")]);
        var excludes = configEnvExcludes.Where(e => !string.IsNullOrEmpty(e)).ToList();
        if (!excludes.Contains(KeyVariable, StringComparer.OrdinalIgnoreCase)) excludes.Add(KeyVariable);
        args.AddRange(["-c", "shell_environment_policy.exclude=[" + string.Join(",", excludes.Select(Toml)) + "]"]);
        args.AddRange(["--", Message]);
        return args;
    }

    private static string Toml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Lê as exclusões de [shell_environment_policy] (ou da chave pontuada) e o forced_login_method da raiz.</summary>
    public static CodexConfigFacts ReadConfig(string? configToml)
    {
        if (string.IsNullOrWhiteSpace(configToml)) return new([], false);
        var excludes = new List<string>();
        bool forced = false;
        string? table = null;
        string? capturing = null; // texto do array de exclusões ainda aberto (multilinha)
        foreach (var rawLine in configToml.Replace("\r\n", "\n").Split('\n'))
        {
            string line = StripComment(rawLine).Trim();
            if (capturing is not null)
            {
                capturing += " " + line;
                if (ArrayClosed(capturing)) { excludes.AddRange(Strings(capturing)); capturing = null; }
                continue;
            }
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                table = line.Trim('[', ']', ' ').Replace(" ", "");
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().Replace("\"", "").Replace("'", "").Replace(" ", "");
            string value = line[(eq + 1)..].Trim();
            string full = table is null ? key : table + "." + key;
            if (full == "shell_environment_policy.exclude" && value.StartsWith('['))
            {
                if (ArrayClosed(value)) excludes.AddRange(Strings(value));
                else capturing = value;
            }
            else if (table is null && key == "forced_login_method" && Strings(value).FirstOrDefault() is { } method
                     && method.Equals("chatgpt", StringComparison.OrdinalIgnoreCase))
                forced = true;
        }
        return new CodexConfigFacts(excludes, forced);
    }

    private static readonly Regex TomlString = new("\"((?:[^\"\\\\]|\\\\.)*)\"|'([^']*)'", RegexOptions.CultureInvariant);

    private static IEnumerable<string> Strings(string value) =>
        TomlString.Matches(value).Select(m => m.Groups[1].Success ? Regex.Unescape(m.Groups[1].Value) : m.Groups[2].Value);

    private static bool ArrayClosed(string value)
    {
        int depth = 0; bool dq = false, sq = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (dq) { if (c == '\\') i++; else if (c == '"') dq = false; continue; }
            if (sq) { if (c == '\'') sq = false; continue; }
            if (c == '"') dq = true;
            else if (c == '\'') sq = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return true;
        }
        return false;
    }

    /// <summary>Tira o comentário "#" que não está dentro de um texto.</summary>
    private static string StripComment(string line)
    {
        bool dq = false, sq = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (dq) { if (c == '\\') i++; else if (c == '"') dq = false; continue; }
            if (sq) { if (c == '\'') sq = false; continue; }
            if (c == '"') dq = true;
            else if (c == '\'') sq = true;
            else if (c == '#') return line[..i];
        }
        return line;
    }
}
