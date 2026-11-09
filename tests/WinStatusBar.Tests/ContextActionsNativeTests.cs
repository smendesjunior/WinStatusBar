using System.Runtime.InteropServices;
using System.Text;
using WinStatusBar.Services;
using Xunit;
namespace WinStatusBar.Tests;

public class ContextActionsNativeTests
{
    [InteractiveDesktopFact]
    public async Task FillsCapturedEditOrSafelyRefusesWhenWindowsDeniesFocus()
    {
        var ready = new TaskCompletionSource<(IntPtr Window, IntPtr Edit, IntPtr Check)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            var window = CreateWindowExW(0, "STATIC", "WinStatusBar · teste de ações", 0x10000000 | 0x80000000, 40, 40, 320, 150, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var edit = CreateWindowExW(0, "EDIT", "Hello world", 0x50000000 | 0x00800000, 10, 10, 280, 30, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var check = CreateWindowExW(0, "BUTTON", "Confirmar", 0x50000003, 10, 50, 160, 25, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "EDIT", "secret-test", 0x50800020, 10, 85, 280, 25, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            ready.SetResult((window, edit, check));
            while (!stop.IsCancellationRequested)
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
                Thread.Sleep(5);
            }
            DestroyWindow(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var (window, edit, check) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = await Task.Run(() => ContextActions.Capture(window));
            var field = Assert.Single(snapshot.Controls, c => c.Actions.Contains("fill"));
            string applied = await Task.Run(() => ContextActions.Execute(snapshot, [new(field.Id, "fill", "Olá", "Hello")], CancellationToken.None));
            var text = new StringBuilder(100); GetWindowTextW(edit, text, 100);
            Assert.DoesNotContain(snapshot.Controls, c => c.Value == "secret-test");
            // Interactive desktops may refuse foreground activation while the user works.
            // That is a supported safety outcome, and must leave every control untouched.
            if (applied.Contains("Ative a janela do contexto") || applied.Contains("A janela mudou"))
            {
                Assert.StartsWith("0 ação", applied);
                Assert.Equal("Hello world", text.ToString());
                Assert.Equal(0, SendMessageW(check, 0x00f0, 0, 0).ToInt32());
                return;
            }
            Assert.True(text.ToString() == "Olá world", applied + " Text: " + text);
            Assert.DoesNotContain(snapshot.Controls, c => c.Value == "secret-test");
            var toggle = Assert.Single(snapshot.Controls, c => c.Actions.Contains("toggle"));
            string toggled = await Task.Run(() => ContextActions.Execute(snapshot, [new(toggle.Id, "toggle", null, null)], CancellationToken.None));
            Assert.Contains("1 ação", toggled);
            Assert.Equal(1, SendMessageW(check, 0x00f0, 0, 0).ToInt32());
            Assert.Contains("1 ação", applied);
            string stale = await Task.Run(() => ContextActions.Execute(snapshot, [new(field.Id, "fill", "Wrong", null)], CancellationToken.None));
            Assert.Contains("0 ação", stale);
            GetWindowTextW(edit, text.Clear(), 100); Assert.Equal("Olá world", text.ToString());
        }
        finally { stop.Cancel(); thread.Join(5000); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string text, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint message, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg msg, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
}

// This integration test deliberately activates a real window and edits its controls.
// Release builds and ordinary test runs must never interrupt the user's desktop.
internal sealed class InteractiveDesktopFactAttribute : FactAttribute
{
    public InteractiveDesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_INTERACTIVE_TESTS") != "1")
            Skip = "Teste de desktop interativo: executar explicitamente com WINSTATUSBAR_INTERACTIVE_TESTS=1.";
    }
}
