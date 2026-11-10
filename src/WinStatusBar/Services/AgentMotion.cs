using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace WinStatusBar.Services;

/// <summary>
/// Animação discreta do logotipo de uma IA enquanto ela trabalha, sem deformar o desenho: nada de esticar ou encolher.
/// "turn": um quarto de volta suave e uma pausa (logos com simetria de 90°, como a estrela do Gemini, voltam ao mesmo
/// desenho); "breathe": a opacidade desce e sobe devagar. Anda pelo relógio único da barra (FrameClock, ~32 quadros/s)
/// e só com alguém vendo a tela e as animações do Windows ligadas (AnimationGate).
/// Codex, Claude e IA local usam as animações próprias (OpenAiLogo, ClaudeWorkingDots, OllamaLogo).
/// </summary>
public static class AgentMotion
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(AgentMotion), new PropertyMetadata("", (d, _) => Hook(d)));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(AgentMotion), new PropertyMetadata(false, (d, _) => Hook(d)));
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(AgentMotion), new PropertyMetadata(false));
    private static readonly DependencyProperty FrameProperty = DependencyProperty.RegisterAttached(
        "Frame", typeof(Action<double>), typeof(AgentMotion), new PropertyMetadata(null));

    public static string GetKind(DependencyObject d) => (string)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, string value) => d.SetValue(KindProperty, value);
    public static bool GetActive(DependencyObject d) => (bool)d.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject d, bool value) => d.SetValue(ActiveProperty, value);

    /// <summary>Gemini gira um quarto de volta; as demais IAs respiram.</summary>
    public static string KindFor(string key) => key is "gemini" or "ai:gemini" ? "turn" : "breathe";


    private static void Hook(DependencyObject d)
    {
        if (d is not FrameworkElement fe) return;
        if (!(bool)fe.GetValue(HookedProperty))
        {
            fe.SetValue(HookedProperty, true);
            fe.Loaded += (_, _) => Apply(fe);
            fe.SizeChanged += (_, _) => Apply(fe);
            fe.Unloaded += (_, _) => Stop(fe);
            Views.AnimationGate.Watch(fe, () => Apply(fe));
        }
        Apply(fe);
    }

    private static void Stop(FrameworkElement fe)
    {
        if (fe.GetValue(FrameProperty) is Action<double> frame) { Views.FrameClock.Remove(frame); fe.ClearValue(FrameProperty); }
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        visual.RotationAngleInDegrees = 0; visual.Opacity = 1;
    }

    private static void Apply(FrameworkElement fe)
    {
        Stop(fe);
        if (!GetActive(fe) || !fe.IsLoaded || fe.ActualWidth <= 0 || !Views.AnimationGate.Allowed) return;
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        Action<double> frame;
        if (GetKind(fe) == "turn")
        {
            // Um quarto de volta suave em 45% do ciclo de 2,2 s e uma pausa (a volta a 0° é igual a 90° no desenho).
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);
            frame = t => { double p = Views.FrameClock.Phase(t, 2.2); visual.RotationAngleInDegrees = p < 0.45 ? 90f * Ease(p / 0.45) : 90f; };
        }
        else
        {
            // Opacidade 100% → 45% → 100% em 2,2 s.
            frame = t =>
            {
                double p = Views.FrameClock.Phase(t, 2.2);
                visual.Opacity = p < 0.5 ? 1f + (0.45f - 1f) * Ease(p / 0.5) : 0.45f + (1f - 0.45f) * Ease((p - 0.5) / 0.5);
            };
        }
        fe.SetValue(FrameProperty, frame);
        Views.FrameClock.Add(frame);
    }

    /// <summary>Curva cubic-bezier(0.65, 0, 0.35, 1) das animações anteriores do compositor: x (0 a 1) → y.</summary>
    internal static float Ease(double x)
    {
        x = Math.Clamp(x, 0, 1);
        double lo = 0, hi = 1, s = x;
        for (int i = 0; i < 24; i++)
        {
            double bx = Bezier(s, 0.65, 0.35);
            if (Math.Abs(bx - x) < 1e-5) break;
            if (bx < x) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return (float)Bezier(s, 0, 1);
    }

    private static double Bezier(double s, double p1, double p2) => 3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;
}
