using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace WinStatusBar.Core.Ai;

/// <summary>Per-process delivery memo, not model/provider configuration or user-message history.</summary>
public static class ProviderInstructionMemo
{
    private static readonly ConcurrentDictionary<string, string> Sent = new(StringComparer.Ordinal);
    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool ShouldSend(string provider, string? session, string instructions) =>
        string.IsNullOrEmpty(session) || !Sent.TryGetValue(provider + "/" + session, out var hash) || hash != Fingerprint(instructions);
    public static void Acknowledge(string provider, string? session, string instructions)
    {
        if (string.IsNullOrEmpty(session)) return;
        if (Sent.Count > 4096) Sent.Clear();
        Sent[provider + "/" + session] = Fingerprint(instructions);
    }
}
