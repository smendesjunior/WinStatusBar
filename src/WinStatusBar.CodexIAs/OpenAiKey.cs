using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Settings;

namespace CodexIAs;

/// <summary>
/// Opções "OpenAI — minha API" do seletor do Codex: o pedido vai à API pública da OpenAI pela chave da pessoa (cobrança
/// por uso), nunca pela conta do ChatGPT. A chave é lida na hora do pedido e nunca é gravada nem registrada.
/// Fontes: o arquivo protegido pelo Windows (DPAPI do usuário) do encaminhador antigo ou uma API própria da OpenAI
/// cadastrada na barra (Cofre de Credenciais do Windows).
/// </summary>
internal static class OpenAiKey
{
    /// <summary>Atalhos fixos (modelo e nível definidos aqui).</summary>
    public static readonly Dictionary<string, (string Model, string Effort)> Fixed = new(StringComparer.Ordinal)
    {
        [OpenAiCatalog.ApiPrefix + "sol"] = ("gpt-6.1-sol", "medium"),
        [OpenAiCatalog.ApiPrefix + "astra"] = ("gpt-6-astra", "low"),
    };

    /// <summary>Campos que a API pública aceita (o resto do pedido do Codex é próprio da conta do ChatGPT).</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "model", "input", "instructions", "tools", "tool_choice", "parallel_tool_calls", "reasoning", "text", "stream", "store",
        "include", "max_output_tokens", "previous_response_id", "metadata", "prompt_cache_key", "prompt_cache_retention",
        "temperature", "top_p", "truncation", "context_management", "service_tier",
    };

    public static bool IsAlias(string model)
        => Fixed.ContainsKey(model) || (model.StartsWith(OpenAiCatalog.ApiPrefix, StringComparison.Ordinal) && model.Length > OpenAiCatalog.ApiPrefix.Length);

    /// <summary>Pedido do Codex → pedido da API pública (modelo de verdade, nível do atalho, só os campos aceitos).</summary>
    public static JsonObject Rewrite(JsonObject payload)
    {
        string alias = payload["model"]!.GetValue<string>();
        var result = new JsonObject();
        foreach (var (k, v) in payload) if (Allowed.Contains(k)) result[k] = v?.DeepClone();
        // Conversa que começou num modelo da conta do ChatGPT: o raciocínio dela vem cifrado pela conta (a API pública não
        // consegue ler e recusa) e os itens trazem campos internos do Codex. Fica o resto: falas, comandos e resultados.
        if (result["input"] is JsonArray input)
        {
            for (int i = input.Count - 1; i >= 0; i--)
            {
                if (input[i] is not JsonObject item) continue;
                if (item["type"]?.GetValue<string>() == "reasoning" && item["encrypted_content"] is not null) { input.RemoveAt(i); continue; }
                item.Remove("internal_chat_message_metadata_passthrough");
            }
        }
        if (Fixed.TryGetValue(alias, out var fixedModel))
        {
            result["model"] = fixedModel.Model;
            var reasoning = result["reasoning"] as JsonObject ?? new JsonObject();
            reasoning["effort"] = fixedModel.Effort;
            result["reasoning"] = reasoning;
        }
        else result["model"] = alias[OpenAiCatalog.ApiPrefix.Length..];
        return result;
    }

    private static string DpapiFile => Path.Combine(Paths.LocalAppData, "OpenAI", "CodexApiGateway", "openai-key.dpapi");

    /// <summary>Chave da API OpenAI, ou null quando não há nenhuma cadastrada.</summary>
    public static string? Get()
    {
        try
        {
            if (File.Exists(DpapiFile))
            {
                // ConvertFrom-SecureString do PowerShell: DPAPI do usuário, texto UTF-16LE, gravado em hexadecimal.
                byte[] raw = Convert.FromHexString(File.ReadAllText(DpapiFile).Trim().TrimStart('﻿'));
                byte[] plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                try { if (Encoding.Unicode.GetString(plain) is { Length: > 0 } key) return key; }
                finally { Array.Clear(plain); }
            }
        }
        catch (Exception ex) { Log.Write("OpenAI — minha API: chave protegida não lida: " + ex.GetType().Name); }
        try
        {
            // BarSettings abre o settings.json sem travar a gravação da barra e guarda a leitura enquanto o arquivo não muda.
            foreach (var api in BarSettings.Load().CustomApis)
                if (Uri.TryCreate(api.BaseUrl.Trim(), UriKind.Absolute, out var u) && u.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
                    && Providers.ApiProvider.Key(api.Resource) is { Length: > 0 } key)
                    return key;
        }
        catch { }
        return null;
    }
}
