using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Views;

/// <summary>
/// Fundo "Adaptativo": a barra parece uma extensão da barra de título da janela abaixo dela.
/// Camadas: (1) a base — o mesmo material do Windows (Mica, Mica Alt, Acrílico), que segue o estado ativo/inativo da
/// janela, ou transparente; (2) por cima, a cor sólida ou o degradê da barra de título, com transição suave.
/// A base só é trocada com a camada de cima cobrindo a barra: nunca aparece um quadro sem fundo.
/// Velocidade: leitura fora da thread da interface; memória por janela aplicada na hora ao trocar de janela;
/// eventos de outros monitores ignorados; ao arrastar, no máximo uma leitura a cada 100 ms.
/// </summary>
public sealed partial class BarWindow
{
    private DispatcherQueueTimer? _adaptivePoll, _adaptiveSoon, _adaptiveBurst, _adaptiveReveal;
    /// <summary>Até quando continuar lendo em rajada (a cada 80 ms) depois de um evento: acompanha as animações do Windows.</summary>
    private long _burstUntil;
    private bool _wallpaperHooked;
    private TintBackdrop? _tint;
    private AdaptiveState? _adaptiveState;
    private IntPtr _adaptiveTarget;
    private bool _adaptiveActive;
    private AdaptiveState? _pendingMode;
    private IntPtr _pendingTarget;
    private bool _adaptiveHooked, _sampling, _resample;
    private int _stableSamples, _foregroundGeneration;
    private string? _lastLogged;
    private Brush? _pendingOverlay;
    private BrushTransition? _overlayTransition;
    /// <summary>Última aparência de cada janela, ativa e inativa: aplicada na hora quando ela volta para a frente.</summary>
    private readonly Dictionary<(IntPtr Hwnd, bool Active), AdaptiveSample> _lookCache = new();
    private static readonly uint SelfPid = (uint)Environment.ProcessId;

    private void StartAdaptive()
    {
        _adaptiveState = null; _adaptiveTarget = IntPtr.Zero; _pendingMode = null; _lastLogged = null;
        if (_adaptivePoll is null)
        {
            _adaptivePoll = Timer(1000, repeating: true, AdaptiveTick);
            _adaptiveSoon = Timer(40, repeating: false, AdaptiveTick);
            _adaptiveBurst = Timer(80, repeating: true, BurstTick);
            _adaptiveReveal = Timer(40, repeating: false, RevealOverlay);
            Closed += (_, _) => StopAdaptive();
            // Tela apagada ou sessão bloqueada: não fotografa a tela (AdaptiveTick); ao acender, lê na hora.
            global::WinStatusBar.Core.Presence.Changed += OnPresenceChanged;
            Closed += (_, _) => global::WinStatusBar.Core.Presence.Changed -= OnPresenceChanged;
            Activated += OnBarActivationChanged;
        }
        _overlayTransition ??= new BrushTransition { Duration = TimeSpan.FromMilliseconds(150) };
        Root.BackgroundTransition = _overlayTransition;
        if (!_adaptiveHooked) { WindowChangeEvents.Changed += OnWindowChanged; _adaptiveHooked = true; }
        // Papel de parede trocado (apresentação de slides, Spotlight, manual): a transição dele leva ~1 s; lê em rajada.
        if (!_wallpaperHooked && AppBar is not null) { AppBar.WallpaperChanged += (_, _) => { if (_adaptiveHooked) Burst(1500); }; _wallpaperHooked = true; }
        _adaptivePoll.Interval = TimeSpan.FromMilliseconds(1000);
        _adaptivePoll.Start();
        AdaptiveTick();
    }

    private DispatcherQueueTimer Timer(int ms, bool repeating, Action tick)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.IsRepeating = repeating;
        t.Tick += (_, _) => tick();
        return t;
    }

    /// <summary>
    /// A barra passou a ser ativada (clique na barra de comando, num menu, Alt+Espaço). A cada ativação/desativação o
    /// Windows tende a voltar ao fundo padrão: reaplica o mesmo fundo, sem desconectá-lo.
    /// </summary>
    private void OnBarActivationChanged(object sender, WindowActivatedEventArgs e)
    {
        if (_tint is null) return;
        _tint.Reapply();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_tint is null || _closing) return;
            _tint.Reapply(); // de novo depois que o Windows terminar a troca de ativação
        });
    }

    private void StopAdaptive()
    {
        _adaptivePoll?.Stop(); _adaptiveSoon?.Stop(); _adaptiveBurst?.Stop(); _adaptiveReveal?.Stop();
        if (_adaptiveHooked) { WindowChangeEvents.Changed -= OnWindowChanged; _adaptiveHooked = false; }
        _adaptiveState = null; _adaptiveTarget = IntPtr.Zero; _pendingOverlay = null;
        _lookCache.Clear();
        Root.BackgroundTransition = null;
        ResetRegionContrast();
        UpdateDivider(null);
    }

    /// <summary>
    /// Evento de janela. Primeiro plano: aplica na hora a aparência memorizada da janela. Em seguida, leituras em rajada
    /// (a cada 80 ms, por meio segundo) acompanham a animação do Windows: minimizar, restaurar, mostrar a área de trabalho
    /// e trocar de janela atualizam fundo, texto e ícones em tempo real. Eventos de outros monitores são ignorados.
    /// </summary>
    private void OnWindowChanged(uint ev, IntPtr hwnd)
    {
        if (_closing || _adaptivePoll is null) return;
        if (ev == WindowChangeEvents.EVENT_SYSTEM_FOREGROUND)
        {
            _foregroundGeneration++;
            TryApplyCached(hwnd);
            Restart(_adaptiveSoon!, 40);
            Burst(500);
            return;
        }
        bool relevant = hwnd == _adaptiveTarget || OnThisMonitor(hwnd);
        if (!relevant && ev is not (WindowChangeEvents.EVENT_OBJECT_DESTROY or WindowChangeEvents.EVENT_OBJECT_HIDE)) return;
        // Arrastar gera dezenas de eventos: a rajada já limita a uma leitura a cada 80 ms.
        if (ev != WindowChangeEvents.EVENT_OBJECT_LOCATIONCHANGE) Restart(_adaptiveSoon!, ev == WindowChangeEvents.EVENT_SYSTEM_MOVESIZEEND ? 20 : 40);
        Burst(500);
    }

    /// <summary>Lê a cada 80 ms até <paramref name="ms"/> depois do último pedido; a última leitura confirma o estado final.</summary>
    private void Burst(int ms)
    {
        _burstUntil = Math.Max(_burstUntil, Environment.TickCount64 + ms);
        if (_adaptiveBurst is { IsRunning: false }) _adaptiveBurst.Start();
    }

    private void BurstTick()
    {
        if (Environment.TickCount64 > _burstUntil) _adaptiveBurst!.Stop();
        AdaptiveTick();
    }

    private static void Restart(DispatcherQueueTimer timer, int ms)
    {
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ms);
        timer.Start();
    }

    private bool OnThisMonitor(IntPtr hwnd)
    {
        if (CurrentMonitor() is not { } m || !GetWindowRect(hwnd, out var r)) return true; // sem dados: confere
        return r.Right > m.Bounds.Left && r.Left < m.Bounds.Right && r.Bottom > m.Bounds.Top && r.Top < m.Bounds.Bottom;
    }

    /// <summary>
    /// Troca imediata: a janela que veio para a frente já foi vista (maximizada neste monitor) → mesma aparência, na hora.
    /// Se a janela da barra de título continua a mesma mas perdeu o foco → a aparência inativa dela.
    /// </summary>
    private void TryApplyCached(IntPtr foreground)
    {
        if (_lookCache.TryGetValue((foreground, true), out var active) && AdaptiveNative.IsZoomed(foreground) && OnThisMonitor(foreground))
        { ApplySample(active, trusted: true); return; }
        if (_adaptiveTarget != IntPtr.Zero && foreground != _adaptiveTarget && !AdaptiveNative.IsZoomed(foreground)
            && _lookCache.TryGetValue((_adaptiveTarget, false), out var inactive))
            ApplySample(inactive, trusted: true);
    }

    private void OnPresenceChanged(bool away)
    {
        if (!away) DispatcherQueue.TryEnqueue(() => { if (_adaptiveHooked) AdaptiveTick(); });
    }

    /// <summary>Leitura fora da thread da interface (captura de tela ~6 ms); só uma por vez, com mais uma se pediram durante.</summary>
    private void AdaptiveTick()
    {
        if (_closing || global::WinStatusBar.Core.Presence.Away) return;
        if (_sampling) { _resample = true; return; }
        if (CurrentMonitor() is not { } monitor) return;
        _sampling = true;
        int generation = _foregroundGeneration;
        // Barra já transparente (sem cor por cima): a linha de cima dela mostra o papel de parede de verdade.
        bool barTransparent = _adaptiveState is AdaptiveState.Desktop or AdaptiveState.Windowed && _pendingOverlay is null
                              && Root.Background is SolidColorBrush { Color.A: 0 };
        Task.Run(() =>
        {
            try { return TitleBarColorSampler.Sample(monitor, SelfPid, barTransparent); }
            catch (Exception ex) { App.Log("Fundo adaptativo: leitura falhou: " + ex.Message); return null; }
        }).ContinueWith(t => DispatcherQueue.TryEnqueue(() =>
        {
            _sampling = false;
            if (_closing) return;
            // A janela da frente mudou durante a leitura: o resultado já é velho.
            if (generation != _foregroundGeneration) _resample = true;
            else if (t.Result is { } sample) ApplySample(sample, trusted: false);
            if (_resample) { _resample = false; AdaptiveTick(); }
        }));
    }

    private void ApplySample(AdaptiveSample sample, bool trusted)
    {
        // Linha coberta (menu/painel da barra, outra janela na frente): leitura inválida, mantém o fundo como está.
        if (sample.Covered && _adaptiveState is not null) return;
        // Janela já conhecida que a leitura manda para o outro modo (ex.: no meio da animação ao restaurar as janelas):
        // mantém a aparência conhecida até uma segunda leitura confirmar a troca.
        if (!trusted && sample.Target != IntPtr.Zero && _lookCache.TryGetValue((sample.Target, sample.Active), out var known)
            && known.State is AdaptiveState.Material or AdaptiveState.Maximized && sample.State is AdaptiveState.Material or AdaptiveState.Maximized
            && known.State != sample.State && !sample.FallbackLike && !(_pendingMode == sample.State && _pendingTarget == sample.Target))
        {
            _pendingMode = sample.State; _pendingTarget = sample.Target;
            if (_adaptiveTarget != sample.Target || _adaptiveState != known.State) ApplySample(known, trusted: true);
            Restart(_adaptiveSoon!, 150);
            return;
        }
        var decision = trusted ? ModeDecision.Apply
            : AdaptiveLogic.DecideMode(_adaptiveState, _adaptiveTarget, sample.State, sample.Target, sample.FallbackLike, _pendingMode, _pendingTarget);
        if (decision == ModeDecision.WaitForConfirmation)
        {
            // Primeira leitura pedindo outro modo na mesma janela: confere de novo antes de trocar.
            _pendingMode = sample.State; _pendingTarget = sample.Target;
            Restart(_adaptiveSoon!, 150);
            return;
        }
        _pendingMode = null;
        var state = decision == ModeDecision.KeepCurrentMode ? _adaptiveState!.Value : sample.State;
        bool changed = state != _adaptiveState || sample.Target != _adaptiveTarget || sample.Active != _adaptiveActive;
        Render(sample, state);
        if (!trusted && sample.Target != IntPtr.Zero && state is AdaptiveState.Material or AdaptiveState.Maximized or AdaptiveState.Painted)
        {
            if (_lookCache.Count > 64) foreach (var key in _lookCache.Keys.Where(k => !AdaptiveNative.IsWindow(k.Hwnd)).ToList()) _lookCache.Remove(key);
            _lookCache[(sample.Target, sample.Active)] = sample with { State = state, Covered = false };
        }
        string summary = $"{state}{(state == AdaptiveState.Material ? " " + sample.Material + (sample.Active ? " ativo" : " inativo") : string.Empty)} · {sample.Target}";
        if (summary != _lastLogged && !trusted)
        {
            _lastLogged = summary;
            App.Log($"Fundo adaptativo ({MonitorDevice}): {state}{(state == AdaptiveState.Material ? " " + sample.Material : string.Empty)}"
                    + $"{(decision == ModeDecision.KeepCurrentMode ? " (mantido: cor de reserva)" : string.Empty)} · {sample.Describe()}"
                    + (sample.Info.Length > 0 ? " · " + sample.Info : string.Empty));
        }
        _adaptiveState = state; _adaptiveTarget = sample.Target; _adaptiveActive = sample.Active;
        // Verificação de segurança: 1 s logo depois de mudanças; 3 s quando tudo está parado.
        _stableSamples = changed ? 0 : _stableSamples + 1;
        // Barra transparente: o papel de parede pode mudar sem aviso (apresentação de slides); confere a cada 1 s.
        var interval = TimeSpan.FromMilliseconds(state is AdaptiveState.Desktop or AdaptiveState.Windowed || _stableSamples < 5 ? 1000 : 3000);
        if (_adaptivePoll is not null && _adaptivePoll.Interval != interval) _adaptivePoll.Interval = interval;
    }

    private void Render(AdaptiveSample sample, AdaptiveState state)
    {
        var appTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        switch (state)
        {
            case AdaptiveState.Material:
            {
                var theme = sample.WindowDark is bool dark ? (dark ? ElementTheme.Dark : ElementTheme.Light) : appTheme;
                var cover = sample.Color ?? (theme == ElementTheme.Dark ? AdaptiveLogic.DarkFallback : AdaptiveLogic.LightFallback);
                bool swapped = EnsureMaterialBase(sample.Material, sample.Active, theme, cover);
                ShowOverlay(Clear(cover), afterSwap: swapped);
                SetBarTheme(theme);
                UpdateDivider(null);
                break;
            }
            case AdaptiveState.Maximized when sample.Color is { } fill:
                // Cor sólida por cima; a base (material ou transparente) fica por baixo, sem ser recriada.
                if (_backdrop is null) EnsureTransparentBase(fill);
                ShowOverlay(new SolidColorBrush(fill), afterSwap: false);
                SetBarTheme(TitleBarColorSampler.IsLight(fill) ? ElementTheme.Light : ElementTheme.Dark);
                UpdateDivider(fill);
                break;
            case AdaptiveState.Painted when sample.Stops is { Count: >= 2 } stops:
                if (_backdrop is null) EnsureTransparentBase(sample.Color ?? stops[0].Color);
                ShowOverlay(Gradient(stops), afterSwap: false);
                SetBarTheme(sample.WindowDark is bool paintedDark ? (paintedDark ? ElementTheme.Dark : ElementTheme.Light) : appTheme);
                UpdateDivider(null);
                break;
            case AdaptiveState.Segments:
            {
                // Janela na metade da tela ou duas lado a lado: cor sólida do tema do Windows, sem seguir as barras de título.
                var solid = App.IsDarkTheme ? AdaptiveLogic.DarkFallback : AdaptiveLogic.LightFallback;
                if (_backdrop is null) EnsureTransparentBase(solid);
                ShowOverlay(new SolidColorBrush(solid), afterSwap: false);
                SetBarTheme(appTheme);
                UpdateDivider(solid);
                break;
            }
            default:
            {
                var previous = (Root.Background as SolidColorBrush)?.Color ?? sample.Color ?? AdaptiveLogic.DarkFallback;
                bool swapped = EnsureTransparentBase(previous);
                ShowOverlay(Clear(previous), afterSwap: swapped);
                var global = state is AdaptiveState.Desktop or AdaptiveState.Windowed ? ContrastTheme(sample.Luminance, appTheme) : appTheme;
                SetBarTheme(global);
                UpdateDivider(null);
                // Transparente: cada parte da barra escolhe texto e ícones pelo papel de parede atrás dela.
                ApplyRegionContrast(sample, global, appTheme);
                return;
            }
        }
        ResetRegionContrast();
    }

    /// <summary>Partes da barra que seguem o papel de parede (texto e ícones); botões com fundo próprio ficam de fora.</summary>
    private IEnumerable<FrameworkElement> ContrastRegions()
    {
        foreach (var e in new FrameworkElement?[] { TaskArea, NetworkModule, CpuModule, RamModule, GpuModule, BatteryModule })
            if (e is not null) yield return e;
        foreach (var c in QuotasModule.Children.OfType<FrameworkElement>()) yield return c;
        foreach (var c in ShortcutsModule.Children.OfType<FrameworkElement>()) yield return c;
    }

    /// <summary>
    /// Barra transparente: texto e ícones de cada parte claros ou escuros conforme o papel de parede logo atrás dela
    /// (contraste de acessibilidade, com folga para não alternar). Os botões com fundo próprio (pílula do agente e a caixa
    /// "Pergunte à sua IA") ficam no tema do Windows, como pedido.
    /// </summary>
    private void ApplyRegionContrast(AdaptiveSample sample, ElementTheme global, ElementTheme appTheme)
    {
        AgentModule.RequestedTheme = appTheme;
        CommandHost.RequestedTheme = appTheme;
        if (sample.Brightness is not { Count: > 0 } blocks || Root.XamlRoot is null || !GetWindowRect(_hwnd, out var win)) return;
        double scale = Root.XamlRoot.RasterizationScale;
        foreach (var el in ContrastRegions())
        {
            if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0) continue;
            var t = el.TransformToVisual(Root);
            var a = t.TransformPoint(new Windows.Foundation.Point(0, 0));
            var b = t.TransformPoint(new Windows.Foundation.Point(el.ActualWidth, 0));
            int left = win.Left + (int)(a.X * scale), right = win.Left + (int)(b.X * scale);
            var under = blocks.Where(k => k.X >= left - 32 && k.X <= right + 32).ToList();
            if (under.Count == 0) continue;
            bool? current = el.RequestedTheme switch { ElementTheme.Light => true, ElementTheme.Dark => false, _ => global == ElementTheme.Light };
            var theme = AdaptiveLogic.DarkTextFor(under.Average(k => k.Luminance), current) switch
            {
                true => ElementTheme.Light, false => ElementTheme.Dark, null => ElementTheme.Default,
            };
            if (el.RequestedTheme != theme) el.RequestedTheme = theme;
        }
    }

    /// <summary>Barra com cor ou material: tudo volta a seguir o tema da barra inteira.</summary>
    private void ResetRegionContrast()
    {
        if (AgentModule.RequestedTheme != ElementTheme.Default) AgentModule.RequestedTheme = ElementTheme.Default;
        if (CommandHost.RequestedTheme != ElementTheme.Default) CommandHost.RequestedTheme = ElementTheme.Default;
        foreach (var el in ContrastRegions()) if (el.RequestedTheme != ElementTheme.Default) el.RequestedTheme = ElementTheme.Default;
    }

    /// <summary>
    /// Base com o material da janela. Mesmo tipo: só atualiza estado ativo/inativo, tema e Mica ↔ Mica Alt, sem recriar
    /// (o Windows anima a transição igual à da barra de título). Outro tipo: troca coberto pela cor <paramref name="cover"/>.
    /// Devolve true quando trocou a base (a camada de cima é revelada logo depois).
    /// </summary>
    private bool EnsureMaterialBase(BackdropKind kind, bool active, ElementTheme theme, Color cover)
    {
        _backdropConfig ??= new SystemBackdropConfiguration();
        _backdropConfig.IsInputActive = active;
        _backdropConfig.Theme = theme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        try
        {
            if (_backdrop is MicaController mica && kind is BackdropKind.Mica or BackdropKind.MicaAlt)
            {
                var micaKind = kind == BackdropKind.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base;
                if (mica.Kind != micaKind) mica.Kind = micaKind;
                return false;
            }
            if (_backdrop is DesktopAcrylicController && kind == BackdropKind.Acrylic) return false;
        }
        catch (Exception ex) { App.Log("Fundo adaptativo: ajuste do material: " + ex.Message); }
        CoverNow(cover);
        SystemBackdrop = null; _tint = null;
        _backdrop?.Dispose(); _backdrop = null;
        ApplyMaterial(kind, 0);
        return true;
    }

    /// <summary>Base transparente (papel de parede). Se havia material, troca coberto pela cor <paramref name="cover"/>.</summary>
    private bool EnsureTransparentBase(Color cover)
    {
        if (_backdrop is null && _tint is not null && ReferenceEquals(SystemBackdrop, _tint)) return false;
        if (_backdrop is not null) CoverNow(cover);
        SystemBackdrop = null;
        _backdrop?.Dispose(); _backdrop = null;
        _tint = new TintBackdrop();
        _tint.SetColor(Color.FromArgb(0, 0, 0, 0), animate: false);
        SystemBackdrop = _tint;
        return true;
    }

    /// <summary>Camada de cima opaca na hora (sem transição): esconde a troca da base.</summary>
    private void CoverNow(Color cover)
    {
        _adaptiveReveal?.Stop(); _pendingOverlay = null;
        Root.BackgroundTransition = null;
        ResetRegionContrast();
        Root.Background = new SolidColorBrush(Color.FromArgb(255, cover.R, cover.G, cover.B));
    }

    /// <summary>
    /// Camada de cima com transição. Logo depois de trocar a base, espera um instante para a base nova já estar desenhada
    /// sob a cobertura e só então revela (senão a transição sairia do mesmo quadro da troca).
    /// </summary>
    private void ShowOverlay(Brush brush, bool afterSwap)
    {
        if (afterSwap || _adaptiveReveal is { IsRunning: true })
        {
            _pendingOverlay = brush;
            if (afterSwap) Restart(_adaptiveReveal!, 40);
            return;
        }
        SetOverlay(brush);
    }

    private void RevealOverlay()
    {
        if (_pendingOverlay is not { } brush || _closing) return;
        _pendingOverlay = null;
        SetOverlay(brush);
    }

    private void SetOverlay(Brush brush)
    {
        if (Root.BackgroundTransition is null && _overlayTransition is not null) Root.BackgroundTransition = _overlayTransition;
        if (Same(Root.Background, brush)) return;
        Root.Background = brush;
    }

    /// <summary>Mesmo pincel (cor ou degradê quase iguais): não redesenha a barra a cada leitura.</summary>
    private static bool Same(Brush? current, Brush next) => (current, next) switch
    {
        (SolidColorBrush a, SolidColorBrush b) => a.Color.A == b.Color.A && Distance(a.Color, b.Color) <= 6,
        (LinearGradientBrush a, LinearGradientBrush b) => a.GradientStops.Count == b.GradientStops.Count
            && !a.GradientStops.Where((s, i) => Math.Abs(s.Offset - b.GradientStops[i].Offset) > 0.001 || Distance(s.Color, b.GradientStops[i].Color) > 3).Any(),
        _ => false,
    };

    private static SolidColorBrush Clear(Color from) => new(Color.FromArgb(0, from.R, from.G, from.B));

    private static LinearGradientBrush Gradient(IReadOnlyList<(double Offset, Color Color)> stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        foreach (var (offset, c) in stops) brush.GradientStops.Add(new GradientStop { Offset = offset, Color = c });
        return brush;
    }

    private Microsoft.UI.Xaml.Shapes.Rectangle? _divider;

    /// <summary>
    /// Linha de 1 pixel físico na base da barra, só com janela maximizada de cor sólida: separa a barra da barra de título.
    /// Bem sutil (8% de preto sobre cor clara, 8% de branco sobre cor escura), com fade; some na área de trabalho e no material.
    /// </summary>
    private void UpdateDivider(Color? fill)
    {
        if (!_settings.Current.AdaptiveDivider) fill = null; // desligada nas configurações
        if (_divider is null)
        {
            if (fill is null) return;
            _divider = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) },
            };
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(_divider, 99); Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_divider, 99);
            Root.Children.Add(_divider);
        }
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        _divider.Height = 1 / Math.Max(1, scale);
        if (fill is { } c)
        {
            _divider.Fill = new SolidColorBrush(TitleBarColorSampler.IsLight(c) ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255));
            _divider.Opacity = 1;
        }
        else _divider.Opacity = 0;
    }

    /// <summary>
    /// Barra transparente: texto e ícones escuros sobre papel de parede claro e claros sobre escuro, pelo contraste de
    /// acessibilidade (com folga para não alternar num fundo de brilho médio). Sem leitura, o tema do Windows.
    /// </summary>
    private ElementTheme ContrastTheme(double? luminance, ElementTheme fallback)
    {
        bool? current = ScaleHost.RequestedTheme switch { ElementTheme.Light => true, ElementTheme.Dark => false, _ => null };
        return AdaptiveLogic.DarkTextFor(luminance, current) switch { true => ElementTheme.Light, false => ElementTheme.Dark, null => fallback };
    }

    /// <summary>
    /// Tema só do conteúdo da barra (texto, ícones, pílulas). A raiz da janela fica no tema do Windows: menus e painéis
    /// que abrem da barra herdam dela, e não ficam com acrílico claro e texto branco quando o papel de parede é claro.
    /// </summary>
    private void SetBarTheme(ElementTheme theme)
    {
        if (ScaleHost.RequestedTheme == theme) return;
        ScaleHost.RequestedTheme = theme;
    }

    private static int Distance(Color a, Color b)
        => Math.Abs(a.A - b.A) + Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static class AdaptiveNative
    {
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    }
}
