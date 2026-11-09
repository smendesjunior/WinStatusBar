using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Integration;

/// <summary>
/// Mescla segura das integrações do WinStatusBar em ~/.claude/settings.json.
///  • Nunca reescreve o arquivo inteiro a partir de um modelo: altera apenas "hooks" e "statusLine".
///  • Hooks próprios são identificados pelo marcador do executável do coletor e removidos individualmente.
///  • Uma statusLine existente é preservada: o coletor a executa em cadeia, e o objeto original é guardado
///    (Base64) no próprio comando, permitindo restauração exata na desinstalação.
/// </summary>
public static class ClaudeSettingsMerger
{
    public const string Marker = "WinStatusBar.Collector.exe";

    public static readonly string[] HookEvents =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
        "Notification", "SubagentStart", "SubagentStop", "TaskCreated", "TaskCompleted", "Stop", "StopFailure", "SessionEnd",
    ];

    /// <summary>
    /// Forma do executável que funciona em qualquer shell usado pelo Claude Code (Git Bash, PowerShell ou cmd).
    /// Em PowerShell, <c>"C:/x.exe" arg</c> é erro de sintaxe; por isso o caminho vai sem aspas
    /// (ou no formato curto 8.3, se tiver espaços). As aspas só são usadas como último recurso.
    /// </summary>
    public static string Quote(string collectorPath)
    {
        string path = collectorPath;
        if (path.Contains(' '))
        {
            var sb = new StringBuilder(1024);
            if (GetShortPathNameW(path, sb, sb.Capacity) > 0 && !sb.ToString().Contains(' ')) path = sb.ToString();
        }
        path = path.Replace('\\', '/');
        return path.Contains(' ') ? "\"" + path + "\"" : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);

    public static JsonObject Install(JsonObject settings, string collectorPath)
    {
        Uninstall(settings); // idempotente: remove versões anteriores antes de reinstalar
        var hooks = settings["hooks"] as JsonObject;
        if (hooks is null) { hooks = new JsonObject(); settings["hooks"] = hooks; }
        foreach (var ev in HookEvents)
        {
            var arr = hooks[ev] as JsonArray;
            if (arr is null) { arr = new JsonArray(); hooks[ev] = arr; }
            arr.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = Quote(collectorPath), // sem argumentos: o modo padrão do coletor é "hook"
                    ["async"] = true,
                    ["timeout"] = 10,
                }),
            });
        }

        var original = settings["statusLine"] as JsonObject;
        // statusLine anterior cujo executável não existe mais (ex.: TrafficMonitor desinstalado) não é preservada:
        // restaurá-la depois só traria de volta uma statusLine quebrada.
        if (original?["command"] is JsonValue ov && ov.TryGetValue<string>(out var oc) && CommandTargetMissing(oc)) original = null;
        string cmd = $"{Quote(collectorPath)} statusline";
        if (original is not null)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(original.ToJsonString()));
            cmd += $" --orig {b64}";
        }
        var statusLine = new JsonObject { ["type"] = "command", ["command"] = cmd };
        if (original?["padding"] is JsonNode pad) statusLine["padding"] = pad.DeepClone();
        settings["statusLine"] = statusLine;
        return settings;
    }

    public static JsonObject Uninstall(JsonObject settings)
    {
        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var ev in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[ev] is not JsonArray groups) continue;
                for (int i = groups.Count - 1; i >= 0; i--)
                {
                    if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray inner) continue;
                    for (int j = inner.Count - 1; j >= 0; j--)
                        if (IsOurs(inner[j])) inner.RemoveAt(j);
                    if (inner.Count == 0) groups.RemoveAt(i);
                }
                if (groups.Count == 0) hooks.Remove(ev);
            }
            if (hooks.Count == 0) settings.Remove("hooks");
        }

        if (settings["statusLine"] is JsonObject sl && IsOurs(sl))
        {
            var restored = DecodeOriginal(sl["command"]?.GetValue<string>());
            if (restored is not null) settings["statusLine"] = restored;
            else settings.Remove("statusLine");
        }
        return settings;
    }

    /// <summary>
    /// O comando aponta para um executável por caminho absoluto que não existe mais?
    /// Comandos sem caminho (ex.: "node script.js") são considerados válidos.
    /// </summary>
    public static bool CommandTargetMissing(string command)
    {
        string c = command.Trim();
        if (c.Length == 0) return true;
        string target;
        if (c[0] == '"') { int end = c.IndexOf('"', 1); target = end > 1 ? c[1..end] : c.Trim('"'); }
        else if (c.StartsWith("& \"", StringComparison.Ordinal)) { int end = c.IndexOf('"', 3); target = end > 3 ? c[3..end] : c[3..]; }
        else { int sp = c.IndexOf(' '); target = sp > 0 ? c[..sp] : c; }
        bool absolute = target.Length > 2 && (target[1] == ':' || target.StartsWith(@"\\", StringComparison.Ordinal));
        if (!absolute) return false;
        try { return !File.Exists(Environment.ExpandEnvironmentVariables(target.Replace('/', '\\'))); }
        catch { return false; }
    }

    /// <summary>A integração instalada encadeia uma statusLine original cujo executável sumiu?</summary>
    public static bool HasBrokenOriginal(JsonObject settings)
    {
        if (settings["statusLine"] is not JsonObject sl || !IsOurs(sl)) return false;
        var orig = DecodeOriginal(sl["command"]?.GetValue<string>());
        return orig?["command"] is JsonValue v && v.TryGetValue<string>(out var c) && CommandTargetMissing(c);
    }

    public static bool IsInstalled(JsonObject settings)
    {
        bool statusOk = settings["statusLine"] is JsonObject sl && IsOurs(sl);
        bool hooksOk = settings["hooks"] is JsonObject h && h.Any(kv => kv.Value is JsonArray a && a.OfType<JsonObject>()
            .Any(g => g["hooks"] is JsonArray inner && inner.Any(IsOurs)));
        return statusOk && hooksOk;
    }

    public static JsonObject? DecodeOriginal(string? command)
    {
        if (command is null) return null;
        int i = command.IndexOf("--orig ", StringComparison.Ordinal);
        if (i < 0) return null;
        string b64 = command[(i + 7)..].Trim().Trim('"');
        int space = b64.IndexOf(' ');
        if (space > 0) b64 = b64[..space];
        try { return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))) as JsonObject; }
        catch { return null; }
    }

    private static bool IsOurs(JsonNode? node) =>
        node is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Contains(Marker, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Aplica a mescla ao arquivo real com backup e gravação atômica.</summary>
public static class ClaudeIntegrationInstaller
{
    public static string ClaudeConfigDir()
    {
        string? env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public static string SettingsFile => Path.Combine(ClaudeConfigDir(), "settings.json");

    public static string Install(string collectorPath) => Modify(s => ClaudeSettingsMerger.Install(s, collectorPath));
    public static string Uninstall() => Modify(ClaudeSettingsMerger.Uninstall);

    /// <summary>Statusline original encadeada aponta para um programa removido (ex.: TrafficMonitor desinstalado).</summary>
    public static bool HasBrokenOriginal()
    {
        try { return File.Exists(SettingsFile) && JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject o && ClaudeSettingsMerger.HasBrokenOriginal(o); }
        catch { return false; }
    }

    public static bool IsInstalled()
    {
        try { return File.Exists(SettingsFile) && JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject o && ClaudeSettingsMerger.IsInstalled(o); }
        catch { return false; }
    }

    private static string Modify(Func<JsonObject, JsonObject> change)
    {
        string file = SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonObject settings;
        string? originalText = File.Exists(file) ? File.ReadAllText(file) : null;
        if (originalText is null || string.IsNullOrWhiteSpace(originalText)) settings = new JsonObject();
        else
        {
            // Se o JSON estiver inválido, não arrisca: aborta sem alterar nada.
            settings = JsonNode.Parse(originalText, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new InvalidDataException(global::WinStatusBar.Core.LocalizedText.T("settings.json do Claude não é um objeto JSON."));
        }
        string before = settings.ToJsonString();
        change(settings);
        string after = settings.ToJsonString();
        if (before == after) return global::WinStatusBar.Core.LocalizedText.T("Sem alterações necessárias.");

        string backup = string.Empty;
        if (originalText is not null)
        {
            backup = file + $".winstatusbar-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.WriteAllText(backup, originalText);
        }
        string tmp = file + ".winstatusbar.tmp";
        File.WriteAllText(tmp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
        return backup.Length > 0 ? global::WinStatusBar.Core.LocalizedText.F("Atualizado. Backup: {0}", new object?[] {backup}) : global::WinStatusBar.Core.LocalizedText.T("Criado.");
    }
}
