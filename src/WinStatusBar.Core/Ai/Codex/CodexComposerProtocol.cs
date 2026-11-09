using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace WinStatusBar.Core.Ai.Codex;
public sealed record CodexPluginMention(string Id, string Name, string DisplayName, string? RemoteId)
{
    public string Path => "plugin://" + (RemoteId ?? Id);
}
public static class CodexComposerProtocol
{
    public static IReadOnlyList<CodexPluginMention> ReadPlugins(JsonElement result)
    {
        var plugins = new List<CodexPluginMention>();
        if (!result.TryGetProperty("marketplaces", out var markets)) return plugins;
        foreach (var market in markets.EnumerateArray())
        foreach (var p in market.GetProperty("plugins").EnumerateArray())
        {
            if (!p.GetProperty("installed").GetBoolean() || !p.GetProperty("enabled").GetBoolean()) continue;
            string name = p.GetProperty("name").GetString()!;
            string? display = p.TryGetProperty("interface", out var ui) && ui.ValueKind == JsonValueKind.Object && ui.TryGetProperty("displayName", out var d) ? d.GetString() : null;
            string? remote = p.TryGetProperty("remotePluginId", out var r) ? r.GetString() : null;
            plugins.Add(new(p.GetProperty("id").GetString()!, name, display ?? name, remote));
        }
        return plugins.DistinctBy(p => p.Id).OrderBy(p => p.DisplayName).ToArray();
    }
    public static JsonArray BuildInput(string prompt, IReadOnlyList<string> attachments, IReadOnlyList<CodexPluginMention> plugins)
    {
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt, ["text_elements"] = new JsonArray() });
        foreach (var p in plugins)
            if (Regex.IsMatch(prompt, @"(?<![\w@])@" + Regex.Escape(p.Name) + @"(?![\w.\-@])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                input.Add(new JsonObject { ["type"] = "mention", ["name"] = p.Name, ["path"] = p.Path });
        foreach (var file in attachments.Where(f => new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
            input.Add(new JsonObject { ["type"] = "localImage", ["path"] = file });
        return input;
    }
}
