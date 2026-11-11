using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>Read-only index snapshot. Failed/unstable reads must not poison the provider's cache.</summary>
public static class SessionTitleIndex
{
    /// <summary>Size and last write of the index file at one moment.</summary>
    public readonly record struct Stamp(long Length, DateTime LastWriteUtc);

    /// <summary>Current stamp of the index; null when it is missing or cannot be queried.</summary>
    public static Stamp? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new Stamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Polling rule: read again only when the file differs from the one last read successfully. Without a successful
    /// read to compare with, or without a current stamp, the answer is always "read" (the read itself decides).
    /// </summary>
    public static bool NeedsRead(Stamp? lastRead, Stamp? current) => lastRead is null || current is null || lastRead != current;

    public static IReadOnlyDictionary<string, string>? Read(string path)
    {
        try
        {
            var initial = new FileInfo(path);
            if (!initial.Exists || initial.Length > 64 * 1024 * 1024) return null;
            long length = initial.Length; DateTime stamp = initial.LastWriteTimeUtc;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var titles = new Dictionary<string, (string Name, DateTimeOffset? Updated)>(StringComparer.Ordinal);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("thread_name", out var name) || name.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(id.GetString()) || string.IsNullOrWhiteSpace(name.GetString())) continue;
                    DateTimeOffset? updated = root.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                    string key = id.GetString()!;
                    if (titles.TryGetValue(key, out var previous) && previous.Updated is not null && updated is not null && previous.Updated > updated) continue;
                    titles[key] = (name.GetString()!, updated);
                }
                catch (JsonException) { /* partially appended/unrecognized line, never an instruction */ }
            }
            var final = new FileInfo(path);
            if (!final.Exists || final.Length != length || final.LastWriteTimeUtc != stamp) return null;
            return titles.ToDictionary(x => x.Key, x => x.Value.Name, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
