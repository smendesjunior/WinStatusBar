using Windows.UI;
using Color = Windows.UI.Color;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.Services;

/// <summary>
/// Decisões do fundo "Adaptativo" (a barra como extensão da barra de título), sem Win32 nem XAML: testáveis.
/// A leitura de tela fica em <see cref="TitleBarColorSampler"/>; a pintura, em BarWindow.Adaptive.
/// </summary>
internal static class AdaptiveLogic
{
    /// <summary>Cores de reserva do Mica/Mica Alt quando a janela está inativa (o Windows pinta sólido): escuro e claro.</summary>
    public static readonly Color DarkFallback = Color.FromArgb(255, 0x20, 0x20, 0x20), LightFallback = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);

    /// <summary>A cor lida é a de reserva do material (janela inativa)? Assim tanto "material inativo" quanto "cor sólida" ficam iguais.</summary>
    public static bool IsFallbackColor(Color c) => Distance(c, DarkFallback) <= 9 || Distance(c, LightFallback) <= 9;

    /// <summary>Cor com matiz (Mica/Acrílico ativos mostram o tom do papel de parede); cinza neutro é cor sólida.</summary>
    public static bool IsTinted(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) >= 4;

    public static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>Luminância relativa (0 = preto, 1 = branco), como no cálculo de contraste de acessibilidade (WCAG).</summary>
    public static double RelativeLuminance(Color c)
    {
        static double Linear(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>
    /// Barra transparente sobre o papel de parede: texto e ícones escuros (true) ou claros (false), o que der mais contraste
    /// (WCAG: preto (Y+0,05)/0,05 contra branco 1,05/(Y+0,05)). Só troca com folga de 15% sobre o atual, para não piscar num
    /// fundo de brilho médio. Sem leitura: null (vale o tema do Windows).
    /// </summary>
    public static bool? DarkTextFor(double? luminance, bool? currentDarkText)
    {
        if (luminance is not { } y) return null;
        double withBlack = (y + 0.05) / 0.05, withWhite = 1.05 / (y + 0.05);
        const double Margin = 1.15;
        if (withBlack > withWhite * Margin) return true;
        if (withWhite > withBlack * Margin) return false;
        return currentDarkText ?? withBlack >= withWhite;
    }

    /// <summary>
    /// Como reproduzir a barra de título de uma janela maximizada:
    /// • cor de legenda definida pelo app (DWMWA_CAPTION_COLOR) → sólida exata;
    /// • material declarado ao Windows (Mica, Mica Alt, Acrílico):
    ///   – inativa e na cor de reserva → o mesmo material inativo (o Windows mostra a reserva; a barra também);
    ///   – ativa, com degradê ou matiz → o mesmo material ativo;
    ///   – ativa com "cor de destaque nas barras de título" ligada → a barra de título é a cor de destaque: sólida;
    /// • o resto → cor sólida lida na tela (ou nenhuma).
    /// </summary>
    public static (AdaptiveState State, bool Active) Classify(BackdropKind? declared, bool active, bool accentOnTitleBars,
                                                             Color? color, bool gradient, Color? captionColor)
    {
        if (captionColor is not null) return (AdaptiveState.Maximized, active);
        if (declared is not null)
        {
            if (!active && color is { } inactive && IsFallbackColor(inactive)) return (AdaptiveState.Material, false);
            if (active && !accentOnTitleBars && (gradient || color is { } tinted && IsTinted(tinted))) return (AdaptiveState.Material, true);
        }
        else if (gradient) return (AdaptiveState.Material, active); // degradê sem declaração: material pintado; Mica Alt aproxima
        return (color is null ? AdaptiveState.Material : AdaptiveState.Maximized, active);
    }

    /// <summary>
    /// Troca entre "material" e "cor sólida" na MESMA janela só com duas leituras seguidas concordando: a primeira leitura
    /// depois de um evento pode pegar a janela no meio da animação de ativação. Na cor de reserva (janela inativa) os dois
    /// modos ficam idênticos: mantém o modo atual. Outra janela, ou modo que não seja material/sólido, aplica na hora.
    /// </summary>
    public static ModeDecision DecideMode(AdaptiveState? current, IntPtr currentTarget, AdaptiveState next, IntPtr nextTarget,
                                          bool fallbackLike, AdaptiveState? pending, IntPtr pendingTarget)
    {
        bool switching = current is AdaptiveState.Material or AdaptiveState.Maximized && next is AdaptiveState.Material or AdaptiveState.Maximized
                         && current != next && currentTarget == nextTarget && nextTarget != IntPtr.Zero;
        if (!switching) return ModeDecision.Apply;
        if (fallbackLike) return ModeDecision.KeepCurrentMode;
        return pending == next && pendingTarget == nextTarget ? ModeDecision.Apply : ModeDecision.WaitForConfirmation;
    }

    /// <summary>
    /// Trechos da linha da barra de título que cada janela realmente mostra, em ordem Z (da frente para trás): cada janela
    /// encostada no topo fica com o que as da frente não cobrem; a janela maximizada atrás (se houver) fica com o resto.
    /// Devolve (início, fim, índice da janela em <paramref name="topWindows"/>, ou -1 para a maximizada).
    /// </summary>
    public static List<(int Left, int Right, int Owner)> Segments(IReadOnlyList<(int Left, int Right)> topWindows, bool fillBehind,
                                                                  int workLeft, int workRight)
    {
        var claimed = new List<(int L, int R)>();
        var result = new List<(int, int, int)>();
        void Take(int left, int right, int owner)
        {
            left = Math.Max(left, workLeft); right = Math.Min(right, workRight);
            var free = new List<(int L, int R)> { (left, right) };
            foreach (var (cl, cr) in claimed)
            {
                var next = new List<(int L, int R)>();
                foreach (var (fl, fr) in free)
                {
                    if (cr <= fl || cl >= fr) { next.Add((fl, fr)); continue; }
                    if (cl > fl) next.Add((fl, cl));
                    if (cr < fr) next.Add((cr, fr));
                }
                free = next;
            }
            foreach (var (fl, fr) in free) if (fr - fl >= 8) { result.Add((fl, fr, owner)); claimed.Add((fl, fr)); }
        }
        for (int i = 0; i < topWindows.Count; i++) Take(topWindows[i].Left, topWindows[i].Right, i);
        if (fillBehind) Take(workLeft, workRight, -1);
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    /// <summary>Faixas [esquerda, direita) da linha y cobertas pelas janelas da lista (as que estão na frente da decisora).</summary>
    public static List<(int Left, int Right)> CoveredAt(IEnumerable<(int Left, int Top, int Right, int Bottom)> windows, int y)
        => windows.Where(r => r.Top <= y && r.Bottom > y && r.Right > r.Left).Select(r => (r.Left, r.Right)).ToList();
}

/// <summary>Resultado da histerese entre material e cor sólida.</summary>
internal enum ModeDecision { Apply, KeepCurrentMode, WaitForConfirmation }

/// <summary>
/// Desktop: só a área de trabalho · Windowed: janela menor na frente · Maximized: barra de título de cor sólida ·
/// Material: barra de título com material do sistema (Mica, Mica Alt ou Acrílico), que a barra reproduz ·
/// Painted: material desenhado pelo próprio app (Office), estendido como degradê ·
/// Segments: janela encaixada (Snap) ou lado a lado no topo — a barra fica na cor sólida do tema do Windows.
/// </summary>
internal enum AdaptiveState { Desktop, Windowed, Maximized, Material, Painted, Segments }
