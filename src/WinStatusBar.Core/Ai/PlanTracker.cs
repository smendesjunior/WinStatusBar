using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Progresso real pelas etapas do plano da sessão. Funciona com a lista inteira (TodoWrite, update_plan do Codex)
/// e com as atualizações parciais do plano do app Claude ("advance", "done", "start"), que não reenviam a lista.
/// Estado compacto: um caractere por etapa — c (concluída), i (em andamento), p (pendente).
/// </summary>
public static class PlanTracker
{
    /// <summary>Operação do plano contida na entrada da ferramenta, ou null: "set:cip…" · "advance" · "done:2,3;start:4".</summary>
    public static string? OperationOf(JsonElement input)
    {
        foreach (var name in new[] { "todos", "items", "plan" })
        {
            if (!input.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            var sb = new StringBuilder("set:");
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                sb.Append(status switch { "completed" => 'c', "in_progress" => 'i', _ => 'p' });
            }
            return sb.Length > 4 ? sb.ToString() : null;
        }
        var parts = new List<string>();
        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True) parts.Add("advance");
        if (input.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Array)
        {
            var steps = done.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32().ToString()).ToList();
            if (steps.Count > 0) parts.Add("done:" + string.Join(',', steps));
        }
        if (input.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number) parts.Add("start:" + start.GetInt32());
        return parts.Count > 0 ? string.Join(';', parts) : null;
    }

    /// <summary>Aplica a operação ao estado atual (null quando ainda não há plano conhecido).</summary>
    public static string? Apply(string? steps, string operation)
    {
        if (operation.StartsWith("set:", StringComparison.Ordinal)) return operation[4..];
        if (string.IsNullOrEmpty(steps)) return steps; // atualização parcial sem lista conhecida: nada a fazer
        var s = steps.ToCharArray();
        foreach (var part in operation.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "advance")
            {
                // Conclui a etapa em andamento e começa a próxima pendente.
                int cur = Array.IndexOf(s, 'i');
                if (cur >= 0) s[cur] = 'c';
                int next = Array.IndexOf(s, 'p');
                if (next >= 0) s[next] = 'i';
            }
            else if (part.StartsWith("done:", StringComparison.Ordinal))
            {
                foreach (var n in part[5..].Split(','))
                    if (int.TryParse(n, out int k) && k >= 1 && k <= s.Length) s[k - 1] = 'c';
            }
            else if (part.StartsWith("start:", StringComparison.Ordinal) && int.TryParse(part[6..], out int k) && k >= 1 && k <= s.Length)
            {
                for (int i = 0; i < s.Length; i++) if (s[i] == 'i') s[i] = 'p';
                s[k - 1] = 'i';
            }
        }
        return new string(s);
    }

    /// <summary>Porcentagem: somente etapas concluídas, sobre o total.</summary>
    public static double? Percent(string? steps)
    {
        if (string.IsNullOrEmpty(steps)) return null;
        double done = steps.Count(c => c == 'c');
        return Math.Round(done * 100.0 / steps.Length, 1);
    }
}
