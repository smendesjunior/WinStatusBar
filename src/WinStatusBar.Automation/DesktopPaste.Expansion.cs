using System.Runtime.InteropServices;
using WinStatusBar.Core.Ai;
using WinStatusBar.ExecutorExpansion;

namespace WinStatusBar.Automation;

internal sealed partial class DesktopRuntime
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private async Task<DesktopActionResult?> ExpandedPasteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        if (r.Action != "paste") return null;
        IntPtr hwnd = ResolveWindow(r);
        Activate(hwnd); await WaitForInputFocusAsync(hwnd, ct);
        ClipboardPreservation snapshot;
        try { snapshot = await RunStaAsync(ClipboardPreservation.Capture); }
        catch (Exception)
        {
            return new("Não foi possível preservar todos os formatos da área de transferência. Nada foi colado; use transferência direta/API ou outra ferramenta disponível.",
                Success: false, Status: DesktopStatus.Blocked, Method: "clipboard/preservation");
        }
        using (snapshot)
        {
            uint placed = 0;
            bool sent = false, restored = false;
            try
            {
                var prepared = await new ClipboardExpansion().ExecuteAsync(r with { Action = "clipboard_set" }, ct);
                if (!prepared.Success) return prepared;
                placed = GetClipboardSequenceNumber();
                ct.ThrowIfCancellationRequested(); EnsureForeground(hwnd);
                SendKeys("Ctrl+V"); sent = true;
                await Task.Delay(500, ct);
            }
            finally
            {
                // Never overwrite a newer clipboard value placed by the user or another application.
                if (placed != 0 && GetClipboardSequenceNumber() == placed)
                {
                    try { await RunStaAsync(() => { snapshot.Restore(); return true; }); restored = true; }
                    catch { }
                }
            }
            string restoredText = restored ? "Área de transferência anterior restaurada." : "Área de transferência não restaurada (mudou ou ficou indisponível).";
            // Conferência pela acessibilidade: o texto colado aparece no valor de algum controle da janela?
            if (sent && r.Text is { Length: > 0 } pastedText && PasteVisible(hwnd, pastedText))
                return new($"Colado e conferido em {Label(hwnd)} ({pastedText.Length} caracteres). {restoredText}",
                    Status: DesktopStatus.Done, Method: "clipboard+native-input/verified-uia");
            DesktopActionResult? observation = null;
            try { observation = Observe(hwnd, r.Image == true); } catch { }
            // Enviado mas não verificável (campo sem acessibilidade, arquivos, imagem): o lote segue, marcado como parcial;
            // a IA confere antes de repetir.
            return new((sent ? $"Ctrl+V enviado à janela correta ({Label(hwnd)}); o efeito não pôde ser lido pela acessibilidade. " : "Comando de colar não confirmado. ") +
                restoredText + " Confira antes de repetir (não duplique)." +
                (observation is null ? " Observação indisponível." : "\nObservação automática:\n" + observation.Text),
                ImagePath: observation?.ImagePath, Success: sent, Status: DesktopStatus.Partial, Method: "clipboard+native-input/unverified");
        }
    }

    /// <summary>Lê os controles da janela (até 2 s) e procura o início do texto colado, sem espaços e quebras.</summary>
    private static bool PasteVisible(IntPtr hwnd, string text)
    {
        static string Norm(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && c is not ('​' or '﻿')).ToArray());
        string probe = Norm(text);
        if (probe.Length == 0) return false;
        if (probe.Length > 60) probe = probe[..60];
        try
        {
            var capture = Task.Run(() => WinStatusBar.Services.ContextActions.Capture(hwnd));
            if (!capture.Wait(TimeSpan.FromSeconds(2))) return false;
            return capture.Result.Controls.Any(c => Norm(c.Value).Contains(probe, StringComparison.Ordinal) || Norm(c.Name).Contains(probe, StringComparison.Ordinal));
        }
        catch { return false; }
    }
}
