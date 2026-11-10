using Microsoft.UI.Xaml;

namespace WinStatusBar.Views;

/// <summary>
/// Animações da barra (estrela do Claude, logotipos girando, brilho do contorno, círculo da tarefa) só quando alguém pode
/// vê-las: animações do Windows ligadas, tela da sessão acesa e sessão desbloqueada (<see cref="Core.Presence"/>).
/// Com a tela apagada, as animações em XAML continuavam acordando a thread da interface ~200 vezes por segundo
/// (medido em 10/11/2026), sem nada para desenhar.
/// </summary>
internal static class AnimationGate
{
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    public static bool Allowed
    {
        get
        {
            if (Core.Presence.Away) return false;
            try { return Ui.AnimationsEnabled; } catch { return true; }
        }
    }

    /// <summary>Chama <paramref name="sync"/> na thread do elemento sempre que a presença mudar, enquanto ele estiver carregado.</summary>
    public static void Watch(FrameworkElement element, Action sync)
    {
        bool subscribed = element.IsLoaded;
        void OnChanged(bool _) => element.DispatcherQueue?.TryEnqueue(() => sync());
        if (subscribed) Core.Presence.Changed += OnChanged;
        element.Loaded += (_, _) => { if (!subscribed) { Core.Presence.Changed += OnChanged; subscribed = true; } };
        element.Unloaded += (_, _) => { if (subscribed) { Core.Presence.Changed -= OnChanged; subscribed = false; } };
    }
}
