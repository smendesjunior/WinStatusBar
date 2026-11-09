using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WinStatusBar.Core.Ai.Codex;
public static class CodexComposer
{
    public static async Task<IReadOnlyList<CodexPluginMention>> PluginsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var rpc = await CodexComposerConnection.OpenAsync(timeout.Token);
        return CodexComposerProtocol.ReadPlugins(await rpc.CallAsync("plugin/list", new JsonObject(), timeout.Token));
    }
    /// <summary>
    /// Nome da conversa no app Codex pelo método oficial do servidor (thread/name/set): as conversas da barra ficam no
    /// histórico local do Codex e, com o nome, aparecem legíveis na barra lateral do app.
    /// </summary>
    public static async Task<bool> SetThreadNameAsync(string threadId, string name, CancellationToken ct)
    {
        try
        {
            await using var connection = await CodexComposerConnection.OpenAsync(ct);
            await connection.CallAsync("thread/name/set", new System.Text.Json.Nodes.JsonObject { ["threadId"] = threadId, ["name"] = name }, ct);
            return true;
        }
        catch { return false; }
    }

    public static async Task<AgentPrompt.Result> RunAsync(string prompt, string? model, string? effort,
        AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
    {
        string? session = options.ResumeSession;
        // Thread nova no meio de uma conversa da barra (veio de outra IA): a conversa vai como contexto.
        if (session is null) prompt = ConversationHandoff.WithTranscript(prompt, options.History);
        var text = new StringBuilder();
        var media = new List<string>(); // imagens geradas pela ferramenta de imagem do Codex (item imageGeneration)
        string Visible() => media.Count == 0 ? text.ToString() : (text.Length > 0 ? text + "\n\n" : "") + string.Join("\n\n", media);
        try
        {
            await using var rpc = await CodexComposerConnection.OpenAsync(ct);
            rpc.Request = options.CodexRequest;
            var completed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            rpc.Notification += (method, p) =>
            {
                if (method == "item/agentMessage/delta" && p.TryGetProperty("delta", out var delta))
                { text.Append(delta.GetString()); onText(Visible()); }
                else if (method == "item/completed" && p.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var type) && type.GetString() == "agentMessage")
                {
                    if (item.TryGetProperty("text", out var final)) { text.Clear(); text.Append(final.GetString()); onText(Visible()); }
                }
                else if (method == "item/completed" && p.TryGetProperty("item", out var imageItem)
                    && imageItem.TryGetProperty("type", out var imageType) && imageType.GetString() == "imageGeneration"
                    && GeneratedImage.FromCodexItem(imageItem) is { } image)
                { media.Add(image); onText(Visible()); }
                else if (method == "turn/completed")
                {
                    var turn = p.GetProperty("turn");
                    string? error = turn.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null ? e.ToString() : null;
                    if (turn.TryGetProperty("status", out var status) && status.GetString() == "interrupted") error = "Interrompido";
                    completed.TrySetResult(error);
                }
                else if (method == "connection/closed") completed.TrySetException(new IOException("A conexão com o Codex foi encerrada."));
            };
            string cwd = options.WorkDir is { } dir && Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var config = new JsonObject { ["web_search"] = options.Web ? "live" : "disabled" };
            var settings = new JsonObject { ["cwd"] = cwd, ["model"] = model, ["approvalPolicy"] = options.Unrestricted ? "never" : "on-request",
                ["sandbox"] = options.Unrestricted ? "danger-full-access" : options.AllowEdits ? "workspace-write" : "read-only", ["config"] = config };
            // Instruções do executor como desenvolvedor: o app Codex mostra só o pedido do usuário no histórico.
            if (options.SystemPrompt is { Length: > 0 } system) settings["developerInstructions"] = system;
            if (session is not null) settings["threadId"] = session;
            else settings["ephemeral"] = options.Ephemeral;
            var opened = await rpc.CallAsync(session is null ? "thread/start" : "thread/resume", settings, ct);
            session = opened.GetProperty("thread").GetProperty("id").GetString();
            IReadOnlyList<CodexPluginMention> plugins = [];
            if (prompt.Contains('@')) plugins = CodexComposerProtocol.ReadPlugins(await rpc.CallAsync("plugin/list", new JsonObject(), ct));
            string withFiles = options.Attachments.Count == 0 ? prompt : prompt + "\n\nAnexos:\n" + string.Join("\n", options.Attachments);
            var turnArgs = new JsonObject { ["threadId"] = session, ["input"] = CodexComposerProtocol.BuildInput(withFiles, options.Attachments, plugins) };
            if (model is not null) turnArgs["model"] = model;
            if (effort is not null) turnArgs["effort"] = effort;
            var started = await rpc.CallAsync("turn/start", turnArgs, ct);
            string? turnId = started.TryGetProperty("turn", out var turnInfo) && turnInfo.TryGetProperty("id", out var tid) ? tid.GetString() : null;
            string? failure;
            try { failure = await completed.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (turnId is not null)
            {
                // Cancelado (Parar, fechar ou atualizar a barra): avisa o Codex antes de fechar o servidor; senão a conversa
                // fica com o turno aberto e aparece como "Executando" para sempre.
                try
                {
                    using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await rpc.CallAsync("turn/interrupt", new JsonObject { ["threadId"] = session, ["turnId"] = turnId }, quick.Token);
                }
                catch { }
                throw;
            }
            return new(Visible(), session, failure);
        }
        catch (OperationCanceledException) { return new(Visible(), session, "Interrompido"); }
        catch (Exception ex) { return new(text.ToString(), session, ex.Message); }
    }
}
