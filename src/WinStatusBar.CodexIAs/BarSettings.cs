using System.Text.Json;
using WinStatusBar.Core.Settings;

namespace CodexIAs;

/// <summary>
/// Preferências da barra lidas pelo componente a cada pedido (ele roda mesmo com a barra fechada), com cache pela data e
/// pelo tamanho do settings.json: uma troca nas Configurações vale no pedido seguinte, sem reiniciar o componente.
/// Arquivo ausente, ilegível ou inválido vale como as preferências padrão (comandos do Claude desligados).
/// </summary>
internal static class BarSettings
{
    internal static string FilePath { get; set; } = SettingsService.SettingsPath;

    private static readonly object Gate = new();
    private static (string Path, DateTime Write, long Length)? _key;
    private static AppSettings _cached = new();

    public static bool ClaudeCommands => Load().CodexClaudeCommands;

    internal static AppSettings Load()
    {
        lock (Gate)
        {
            string path = FilePath;
            (string, DateTime, long) key;
            try
            {
                var info = new FileInfo(path);
                key = info.Exists ? (path, info.LastWriteTimeUtc, info.Length) : (path, DateTime.MinValue, -1);
            }
            catch (Exception) { key = (path, DateTime.MinValue, -1); }
            if (_key == key) return _cached;
            _key = key;
            _cached = Read(path, key.Item3 >= 0);
            return _cached;
        }
    }

    private static AppSettings Read(string path, bool exists)
    {
        if (!exists) return new AppSettings();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, AppSettingsContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e)
        {
            Log.Write($"Configurações da barra não lidas ({e.GetType().Name}); comandos do Claude desligados");
            return new AppSettings();
        }
    }
}
