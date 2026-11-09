using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai.Claude;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Ipc;

// Coletor do WinStatusBar para o Claude Code.
//   hook        → lê o JSON do hook no stdin e encaminha metadados mínimos ao WinStatusBar (Named Pipe). Nada é impresso.
//   statusline  → encaminha os dados da statusLine e imprime a linha de status
//                 (executando em cadeia a statusLine original, se existia — --orig <base64>).
// Nunca falha de forma a atrapalhar o Claude Code: qualquer erro termina com código 0.

string mode = args.Length > 0 ? args[0] : "hook";
string input;
try
{
    using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    input = stdin.ReadToEnd();
}
catch { input = string.Empty; }

try
{
    if (mode.Equals("statusline", StringComparison.OrdinalIgnoreCase))
    {
        var msg = ClaudeEventExtractor.FromStatusLine(input);
        var send = msg is null ? Task.CompletedTask : Task.Run(() => Send(msg));
        string? origCommand = OriginalCommand(args);
        using var stdout = Console.OpenStandardOutput();
        // Repasse byte a byte da statusLine original; se ela não existir mais (ex.: app desinstalado) ou falhar,
        // usa a linha própria do WinStatusBar em vez de deixar a statusLine vazia.
        byte[]? chained = origCommand is not null ? RunChained(origCommand, input) : null;
        var bytes = chained is { Length: > 0 } ? chained : new UTF8Encoding(false).GetBytes(OwnStatusLine(msg));
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
        send.Wait(400);
    }
    else
    {
        var msg = ClaudeEventExtractor.FromHook(input);
        if (msg is not null) Send(msg);
    }
}
catch { }
return 0;

static void Send(EventMessage msg)
{
    try
    {
        using var client = new NamedPipeClientStream(".", PipeNames.Events, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect(250); // se a barra não estiver aberta, desiste rapidamente
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, EventMessageContext.Default.EventMessage);
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
    }
    catch { }
}

static string? OriginalCommand(string[] args)
{
    int i = Array.FindIndex(args, a => a == "--orig");
    if (i < 0 || i + 1 >= args.Length) return null;
    var original = ClaudeSettingsMerger.DecodeOriginal("--orig " + args[i + 1]);
    return original?["command"] is JsonValue v && v.TryGetValue<string>(out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;
}

/// <summary>Executa a statusLine original com o mesmo stdin e devolve sua saída (preserva o comportamento anterior).</summary>
static byte[]? RunChained(string command, string input)
{
    try
    {
        if (ClaudeSettingsMerger.CommandTargetMissing(command)) return null;
        // Sem CreateNoWindow: o processo herda o console do coletor (o mesmo contexto em que o Claude Code o executaria).
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // /s remove apenas as aspas externas; o comando original (com suas próprias aspas) é executado como estava.
        psi.Arguments = "/d /s /c \"" + command + "\"";
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return null; }
        copy.Wait(1000);
        return p.ExitCode == 0 ? ms.ToArray() : null;
    }
    catch { return null; }
}

static string OwnStatusLine(EventMessage? m)
{
    if (m is null) return string.Empty;
    var parts = new List<string>();
    if (m.Model is not null) parts.Add(m.Model);
    if (m.ContextUsedPercent is not null) parts.Add($"ctx {Math.Round(m.ContextUsedPercent.Value)}%");
    if (m.FiveHourUsed is not null) parts.Add($"5h {Math.Round(m.FiveHourUsed.Value)}%");
    if (m.SevenDayUsed is not null) parts.Add($"7d {Math.Round(m.SevenDayUsed.Value)}%");
    return string.Join(" · ", parts);
}
