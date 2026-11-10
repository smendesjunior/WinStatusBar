using System.Runtime.InteropServices;
using System.Text;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>
/// Chave da API da OpenAI guardada pelo encaminhador do Codex em %LOCALAPPDATA%\OpenAI\CodexApiGateway\openai-key.dpapi:
/// texto UTF-16LE protegido pelo Windows para o usuário (DPAPI), gravado em hexadecimal (ConvertFrom-SecureString).
/// Lida na hora, só para o ambiente do processo filho; nunca é registrada nem gravada. Usa crypt32 direto (sem pacote novo).
/// </summary>
public static class OpenAiApiKey
{
    public const string FileSourceName = "arquivo protegido do Windows (openai-key.dpapi)";

    public static string DefaultFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "CodexApiGateway", "openai-key.dpapi");

    /// <summary>Só confere se o arquivo existe (não carrega a chave).</summary>
    public static bool Exists(string? file = null)
    {
        try { return File.Exists(file ?? DefaultFile); }
        catch { return false; }
    }

    /// <summary>A chave, ou null se o arquivo faltar ou não puder ser lido. Nunca lança nem registra nada.</summary>
    public static string? Read(string? file = null)
    {
        byte[]? raw = null, plain = null;
        try
        {
            string path = file ?? DefaultFile;
            if (!File.Exists(path)) return null;
            string hex = File.ReadAllText(path).Trim().TrimStart('﻿').Trim();
            if (hex.Length == 0 || hex.Length % 2 != 0) return null;
            raw = Convert.FromHexString(hex);
            plain = Unprotect(raw);
            if (plain is null || plain.Length == 0) return null;
            string key = Encoding.Unicode.GetString(plain);
            return key.Length > 0 ? key : null;
        }
        catch { return null; }
        finally
        {
            if (raw is not null) Array.Clear(raw);
            if (plain is not null) Array.Clear(plain);
        }
    }

    /// <summary>Formato do arquivo (para os testes): hex do DPAPI do usuário sobre o texto UTF-16LE.</summary>
    internal static string ToProtectedHex(string key)
    {
        byte[] plain = Encoding.Unicode.GetBytes(key);
        try { return Convert.ToHexString(Protect(plain)).ToLowerInvariant(); }
        finally { Array.Clear(plain); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[]? Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true) ?? throw new InvalidOperationException("DPAPI falhou.");

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var output = default(DataBlob);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok || output.Data == IntPtr.Zero) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // Zera o buffer nativo antes de liberar: o texto da chave não fica na memória solta.
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            handle.Free();
        }
    }
}
