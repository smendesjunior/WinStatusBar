using System.Text.Json;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>Interpretação das respostas JSON-RPC do app-server e das linhas de rollout (~/.codex/sessions/**/*.jsonl).</summary>
public static class CodexParsers
{
    /// <summary>Resultado de account/rateLimits/read (camelCase) ou rate_limits do rollout (snake_case).
    /// <paramref name="observedAt"/> é o horário do evento (linha do registro); sem ele, agora.</summary>
    public static QuotaSnapshot? ParseRateLimits(JsonElement rl, string source, DateTimeOffset? observedAt = null)
    {
        if (rl.ValueKind != JsonValueKind.Object) return null;
        var outer = rl;
        bool official = rl.TryGetProperty("rateLimits", out var inner) && inner.ValueKind == JsonValueKind.Object;
        if (official) rl = inner;
        var now = observedAt ?? DateTimeOffset.Now;
        QuotaWindow? Window(string name, string label) => WindowOf(rl, name, label);
        QuotaWindow? WindowOf(JsonElement container, string name, string label)
        {
            if (!container.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
            double? used = Num(w, "usedPercent") ?? Num(w, "used_percent");
            if (used is null) return null;
            long? resets = Long(w, "resetsAt") ?? Long(w, "resets_at");
            long? mins = Long(w, "windowDurationMins") ?? Long(w, "window_minutes");
            string lbl = mins switch { 300 => "5h", 10080 => "7d", null => label, _ => mins < 1440 ? $"{mins / 60}h" : $"{mins / 1440}d" };
            return new QuotaWindow(AgentKind.Codex, lbl, used.Value,
                resets is null ? null : DateTimeOffset.FromUnixTimeSeconds(resets.Value), (int?)mins, now, source);
        }
        var primary = Window("primary", "5h");
        var secondary = Window("secondary", "7d");
        var access = AccessOf(outer, rl, official, now);
        if (primary is null && secondary is null && access is null) return null;
        string? plan = Str(rl, "planType") ?? Str(rl, "plan_type");
        string? mainId = Str(rl, "limitId") ?? Str(rl, "limit_id");

        // Outros limites da conta (ex.: por modelo), além do principal
        var extra = new List<QuotaWindow>();
        if (outer.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in byId.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || p.Name == mainId) continue;
                string name = Str(p.Value, "limitName") ?? p.Name;
                foreach (var (key, suffix) in new[] { ("primary", "curto"), ("secondary", "longo") })
                    if (WindowOf(p.Value, key, suffix) is { } w) extra.Add(w with { Label = $"{name} · {w.Label}" });
            }
        }

        // Créditos e resets avulsos (somente a consulta oficial do app-server informa)
        string? creditsText = null;
        var cr = rl.TryGetProperty("credits", out var c1) ? c1 : default;
        if (cr.ValueKind == JsonValueKind.Object)
        {
            bool unlimited = cr.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
            bool has = (cr.TryGetProperty("hasCredits", out var h) || cr.TryGetProperty("has_credits", out h)) && h.ValueKind == JsonValueKind.True;
            string? balance = Str(cr, "balance");
            creditsText = unlimited ? global::WinStatusBar.Core.LocalizedText.T("Créditos ilimitados") : has ? global::WinStatusBar.Core.LocalizedText.F("Saldo de créditos: {0}", new object?[] {balance ?? global::WinStatusBar.Core.LocalizedText.T("disponível")}) : global::WinStatusBar.Core.LocalizedText.T("Sem créditos extras");
        }
        int? resetCount = null;
        var credits = new List<ResetCredit>();
        if (outer.TryGetProperty("rateLimitResetCredits", out var rc) && rc.ValueKind == JsonValueKind.Object)
        {
            if (rc.TryGetProperty("availableCount", out var ac) && ac.TryGetInt32(out var n)) resetCount = n;
            if (rc.TryGetProperty("credits", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                {
                    long? exp = Long(item, "expiresAt");
                    credits.Add(new ResetCredit(Str(item, "title") ?? global::WinStatusBar.Core.LocalizedText.T("Reset de limite"), Str(item, "description"),
                        exp is null ? null : DateTimeOffset.FromUnixTimeSeconds(exp.Value), Str(item, "status") ?? "unknown") { Id = Str(item, "id") });
                }
        }
        return new QuotaSnapshot
        {
            Primary = primary, Secondary = secondary, PlanType = plan, ObservedAt = now,
            AdditionalWindows = extra, ResetCreditsAvailable = resetCount, ResetCredits = credits, CreditsText = creditsText,
            Access = access,
        };
    }

    /// <summary>Campos de bloqueio, lidos de dentro (rateLimits) para fora, em camelCase e snake_case.
    /// Só existe acesso quando a leitura é oficial ou traz um motivo/limite de gasto.</summary>
    private static CodexAccess? AccessOf(JsonElement outer, JsonElement rl, bool official, DateTimeOffset observedAt)
    {
        bool? B(string camel, string snake) => Bool(rl, camel) ?? Bool(rl, snake) ?? Bool(outer, camel) ?? Bool(outer, snake);
        string? S(string camel, string snake) => Str(rl, camel) ?? Str(rl, snake) ?? Str(outer, camel) ?? Str(outer, snake);
        bool? allowed = B("ordinaryUsageAllowed", "ordinary_usage_allowed");
        string? reached = S("rateLimitReachedType", "rate_limit_reached_type");
        bool? spend = B("spendControlReached", "spend_control_reached");
        if (!official && reached is null && spend != true) return null;

        var cr = rl.TryGetProperty("credits", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        bool? hasCredits = Bool(cr, "hasCredits") ?? Bool(cr, "has_credits");
        string? balance = Str(cr, "balance");

        var upsell = outer.TryGetProperty("rateLimitUpsell", out var u) && u.ValueKind == JsonValueKind.Object ? u
            : outer.TryGetProperty("rate_limit_upsell", out u) && u.ValueKind == JsonValueKind.Object ? u : default;
        string? title = Str(upsell, "title");
        DateTimeOffset? resetAt = null;
        foreach (var name in new[] { "reset_at", "resetAt" })
        {
            if (upsell.ValueKind != JsonValueKind.Object || !upsell.TryGetProperty(name, out var r)) continue;
            if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var secs)) resetAt = DateTimeOffset.FromUnixTimeSeconds(secs);
            else if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) resetAt = parsed;
            if (resetAt is not null) break;
        }
        return new CodexAccess(allowed, reached, spend, hasCredits, balance, title, resetAt, observedAt, official);
    }

    /// <summary>Aplica uma linha do rollout à sessão. Retorna uma cota se a linha trouxer rate_limits.</summary>
    public static QuotaSnapshot? ApplyRolloutLine(AgentSession session, string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            string? type = Str(root, "type");
            if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return null;
            DateTimeOffset ts = root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(t.GetString(), out var parsed) ? parsed : DateTimeOffset.Now;
            string? ptype = Str(p, "type");

            switch (type)
            {
                case "session_meta":
                    session.ProjectDir ??= Str(p, "cwd");
                    session.Originator ??= Str(p, "originator");
                    return null;
                case "turn_context":
                    session.Model = Str(p, "model") ?? session.Model;
                    session.ProjectDir = Str(p, "cwd") ?? session.ProjectDir;
                    return null;
                case "event_msg":
                    return ApplyEvent(session, ptype, p, ts);
                case "response_item":
                    ApplyResponseItem(session, ptype, p, ts);
                    return null;
                case "token_usage_record":
                    if (Str(p, "thread_id") is { } owner && owner != session.Id) return null;
                    if (Str(p, "turn_id") is { } turnId && turnId == session.TurnId
                        && p.TryGetProperty("turn_token_usage", out var usage) && Tokens(usage) is { } tokens)
                        session.TurnTokens = tokens;
                    return null;
            }
            return null;
        }
    }

    /// <summary>Código de erro com que o Codex encerra um turno quando a cota acaba.</summary>
    public const string UsageLimitExceeded = "usage_limit_exceeded";

    /// <summary>Lê input/cached_input/output/reasoning_output_tokens; null se não for um objeto de uso.</summary>
    internal static TokenUsage? Tokens(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        long? input = Long(usage, "input_tokens");
        long? output = Long(usage, "output_tokens");
        if (input is null && output is null) return null;
        return new TokenUsage(input ?? 0, Long(usage, "cached_input_tokens") ?? 0, output ?? 0, Long(usage, "reasoning_output_tokens") ?? 0);
    }

    /// <summary>Fim de turno: com o código de cota vira Interrompida; com outro erro, Erro com o rótulo do explicador; sem erro, Concluída.</summary>
    private static void FinishTurn(AgentSession s, JsonElement p)
    {
        if (!p.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            ExecutionProgress.FinishResponse(s);
            return;
        }
        string code = "error";
        if (error.TryGetProperty("codex_error_info", out var info))
        {
            if (info.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(info.GetString())) code = info.GetString()!;
            else if (info.ValueKind == JsonValueKind.Object)
                foreach (var prop in info.EnumerateObject()) { code = prop.Name; break; }
        }
        s.ExecutionEnded = true;
        s.StopReason = code;
        if (code == UsageLimitExceeded)
        {
            s.State = AgentState.Interrupted;
            s.Caption = CodexQuotaText.StoppedCaption(s.LastReachedType);
            return;
        }
        s.State = AgentState.Error;
        s.Caption = global::WinStatusBar.Core.LocalizedText.F("Falha na execução · {0}",
            AiErrorExplainer.Explain(AgentPrompt.Target.Codex, Str(error, "message") ?? code).Short);
    }

    private static QuotaSnapshot? ApplyEvent(AgentSession s, string? ptype, JsonElement p, DateTimeOffset ts)
    {
        if (Str(p, "thread_id") is { } thread && thread != s.Id) return null;
        string? turn = Str(p, "turn_id");
        if (ptype == "task_started" && turn is not null && turn == s.TurnId && s.StartedAt == ts) return null;
        if (ts < s.LastStateEvent) return null;
        if (ptype != "task_started" && turn is not null && s.TurnId is not null && turn != s.TurnId) return null;
        if (ptype != "token_count") s.LastStateEvent = ts;
        s.ObservationStale = false;
        switch (ptype)
        {
            case "task_started":
                s.ProgressIsPhaseEstimate = false;
                s.PendingPlans.Clear(); s.TurnId = turn; s.ExecutionEnded = false; s.ScopeChanged = false; s.PlanSteps = null;
                s.State = AgentState.Running; s.StartedAt = ts; s.LastActivity = ts;
                s.Caption = global::WinStatusBar.Core.LocalizedText.T("Processando a solicitação…"); s.ProgressPercent = null;
                s.TurnTrigger = p.TryGetProperty("turn_attribution", out var attribution) ? Str(attribution, "turn_trigger") : null;
                s.StopReason = null; s.TurnTokens = null; s.LastReachedType = null;
                break;
            case "task_complete":
                FinishTurn(s, p); s.LastActivity = ts;
                break;
            case "turn_aborted":
                s.State = AgentState.Interrupted; s.ExecutionEnded = true; s.LastActivity = ts; s.Caption = "Execução interrompida";
                break;
            case "stream_error":
                s.State = AgentState.WaitingResponse; s.Caption = "Falha de comunicação · aguardando recuperação"; s.LastActivity = ts;
                break;
            case "error":
                s.State = AgentState.Error; s.ExecutionEnded = true; s.LastActivity = ts; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Falha na execução");
                break;
            case "exec_approval_request":
            case "apply_patch_approval_request":
            case "request_permissions":
                s.State = AgentState.WaitingApproval; s.LastActivity = ts; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Aguardando sua autorização");
                break;
            case "request_user_input":
                s.State = AgentState.WaitingInput; s.LastActivity = ts; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Aguardando sua resposta");
                break;
            case "item_started":
            case "item_completed":
                if (p.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    string? caption = CaptionForItem(Str(item, "type"));
                    if (caption is not null && !s.ExecutionEnded && s.State is not (AgentState.WaitingApproval or AgentState.WaitingInput))
                    {
                        if (Str(item, "type") != "UserMessage") s.State = AgentState.Running;
                        s.Caption = caption;
                        string stage = Str(item, "type") switch
                        {
                            "Reasoning" or "WebSearch" or "ImageView" => "reading",
                            "FileChange" => "editing",
                            "AgentMessage" when Str(item, "phase") == "final_answer" => "finalizing",
                            "CommandExecution" => CommandStage(item),
                            "McpToolCall" or "DynamicToolCall" or "Extension" or "ImageGeneration" => "working",
                            _ => ""
                        };
                        ExecutionProgress.Observe(s, stage);
                    }
                    s.LastActivity = ts;
                }
                break;
            case "token_count":
                s.LastActivity = ts;
                if (p.TryGetProperty("rate_limits", out var rl))
                {
                    var quota = ParseRateLimits(rl, global::WinStatusBar.Core.LocalizedText.T("Codex rollout"), ts);
                    if (quota?.Access?.ReachedType is { Length: > 0 } reached) s.LastReachedType = reached;
                    return quota;
                }
                break;
        }
        return null;
    }

    private static void ApplyResponseItem(AgentSession s, string? ptype, JsonElement p, DateTimeOffset ts)
    {
        if (ptype is "function_call_output" or "custom_tool_call_output")
        {
            if (Str(p, "call_id") is { } call && s.PendingPlans.Remove(call, out var plan) && !s.ExecutionEnded && ts >= s.LastStateEvent)
            {
                bool failed = p.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
                string output = Str(p, "output") ?? "";
                failed |= output.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                if (!failed) ApplyPlan(s, plan);
            }
            return;
        }
        if (ptype is not ("function_call" or "custom_tool_call" or "local_shell_call" or "web_search_call")) return;
        if (ts < s.LastStateEvent || s.ExecutionEnded) return;
        s.LastActivity = ts; s.ObservationStale = false;
        s.State = AgentState.Running;
        string? name = Str(p, "name");
        if (name is "update_plan" or "functions.update_plan")
        {
            string plan = Str(p, "arguments") ?? Str(p, "input") ?? "";
            if (Str(p, "call_id") is { } call) s.PendingPlans[call] = plan;
            else ApplyPlan(s, plan); // historical records without a call id expose only a declared plan
            return;
        }
        ExecutionProgress.Tool(s, name);
        string args = (Str(p, "input") ?? Str(p, "arguments") ?? string.Empty);
        string? caption = ptype == "web_search_call" ? "Pesquisando na web…"
            : name is "apply_patch" || args.Contains("apply_patch", StringComparison.Ordinal) ? "Editando arquivos…"
            : args.Contains("mcp__", StringComparison.Ordinal) ? "Usando ferramentas…"
            : name is "exec" or "shell" or "exec_command" or "local_shell" || ptype == "local_shell_call" ? global::WinStatusBar.Core.LocalizedText.T("Executando comandos…")
            : name is not null ? "Usando ferramentas…" : null;
        if (caption is not null && s.State.IsActive()) s.Caption = caption;
    }

    private static void ApplyPlan(AgentSession s, string json)
    {
        var value = PlanProgress(json);
        if (value is null) return;
        s.ScopeChanged = s.ProgressPercent is { } old && value < old;
        s.ProgressPercent = value;
        s.ProgressIsPhaseEstimate = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var plan = doc.RootElement.GetProperty("plan");
            s.PlanSteps = new string(plan.EnumerateArray().Select(step => Str(step, "status") switch { "completed" => 'c', "in_progress" => 'i', _ => 'p' }).ToArray());
            s.ProgressBasis = plan.EnumerateArray().Any(step => Num(step, "weight") is > 0) ? "Pesos declarados no plano" : "Etapas de mesmo peso";
        }
        catch (JsonException) { }
    }

    private static string CommandStage(JsonElement item)
    {
        string command = Str(item, "command") ?? "";
        if (item.TryGetProperty("command", out var list) && list.ValueKind == JsonValueKind.Array)
            command = string.Join(" ", list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        bool checking = System.Text.RegularExpressions.Regex.IsMatch(command,
            @"(?:^|[\s;&|])(?:dotnet\s+test|npm\s+(?:run\s+)?test|pnpm\s+test|pytest|cargo\s+test|go\s+test)(?:\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return checking ? (Long(item, "exit_code") is { } code && code != 0 ? "retry" : "checking") : "working";
    }

    internal static string? CaptionForItem(string? itemType) => itemType switch
    {
        "CommandExecution" => global::WinStatusBar.Core.LocalizedText.T("Executando comandos…"),
        "FileChange" => "Editando arquivos…",
        "McpToolCall" or "DynamicToolCall" or "Extension" => "Usando ferramentas…",
        "WebSearch" => "Pesquisando na web…",
        "Reasoning" => "Analisando e planejando…",
        "AgentMessage" => "Redigindo resposta…",
        "Plan" => "Atualizando o plano…",
        "ImageView" or "ImageGeneration" => "Processando imagens…",
        "CollabAgentToolCall" or "SubAgent" => "Coordenando subagentes…",
        _ => null,
    };

    /// <summary>Percentual real a partir das etapas do plano (concluídas / total).</summary>
    internal static double? PlanProgress(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Array) return null;
            double total = 0, done = 0;
            foreach (var step in plan.EnumerateArray())
            {
                double weight = Num(step, "weight") ?? 1;
                if (!double.IsFinite(weight) || weight <= 0 || weight > 1000000) weight = 1;
                total += weight;
                if (Str(step, "status") == "completed") done += weight;
            }
            return total == 0 ? null : done * 100.0 / total;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Extrai o id da thread do nome do arquivo rollout-AAAA-MM-DDTHH-MM-SS-&lt;uuid&gt;.jsonl.</summary>
    public static string? ThreadIdFromRolloutPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("rollout-", StringComparison.Ordinal) || name.Length < 36) return null;
        string id = name[^36..];
        return Guid.TryParse(id, out _) ? id : null;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    internal static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
