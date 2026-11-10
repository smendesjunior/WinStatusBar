using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.UI;

namespace WinStatusBar.Views;

/// <summary>
/// Vetores dos logotipos do agente, lidos de Assets\AgentLogos (SVGs do projeto):
///  • chatgpt-estatico.svg / chatgpt-rotacao.svg — logotipo do ChatGPT parado e o giro (duração do animateTransform);
///  • estrela-vetorial-animada.svg — estrela do Claude trabalhando: um caminho por quadro, cada um visível na sua vez.
/// </summary>
internal static class AgentLogoAssets
{
    public sealed record Frame(Color Fill, string Data, int Index);
    public sealed record Star(double Width, double Height, IReadOnlyList<Frame> Frames, int FrameCount, TimeSpan Duration);
    public sealed record OpenAi(double X, double Y, double Width, double Height, string Data, TimeSpan Turn);

    private static readonly Lazy<Star?> _star = new(LoadStar);
    private static readonly Lazy<OpenAi?> _openAi = new(LoadOpenAi);
    public static Star? ClaudeStar => _star.Value;
    public static OpenAi? OpenAiLogo => _openAi.Value;

    private static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLogos");

    public static Geometry ToGeometry(string data) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    private static double[] ViewBox(string svg)
    {
        var m = Regex.Match(svg, "viewBox=\"([^\"]+)\"");
        var v = m.Success ? m.Groups[1].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                                     .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray() : [];
        return v.Length == 4 ? v : [0, 0, 24, 24];
    }

    private static TimeSpan Dur(string text, TimeSpan fallback)
    {
        var m = Regex.Match(text, "dur=\"([0-9.]+)(ms|s)\"");
        if (!m.Success) return fallback;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value == "ms" ? TimeSpan.FromMilliseconds(v) : TimeSpan.FromSeconds(v);
    }

    private static Star? LoadStar()
    {
        try
        {
            string svg = File.ReadAllText(System.IO.Path.Combine(Dir, "estrela-vetorial-animada.svg"));
            var vb = ViewBox(svg);
            var frames = new List<Frame>();
            int count = 0;
            TimeSpan duration = TimeSpan.FromSeconds(0.81);
            foreach (Match p in Regex.Matches(svg, "<path ([^>]*)>(.*?)</path>", RegexOptions.Singleline))
            {
                string attrs = p.Groups[1].Value, inner = p.Groups[2].Value;
                var d = Regex.Match(attrs, " d=\"([^\"]+)\"");
                if (!d.Success) continue;
                var values = Regex.Match(inner, "values=\"([^\"]+)\"").Groups[1].Value.Split(';');
                // O último valor repete o primeiro (fecha o ciclo): quadros = valores - 1.
                count = Math.Max(count, Math.Max(1, values.Length - 1));
                int index = Array.FindIndex(values, v => v.Trim() == "visible");
                duration = Dur(inner, duration);
                frames.Add(new(ParseColor(Regex.Match(attrs, "fill=\"([^\"]+)\"").Groups[1].Value), d.Groups[1].Value, Math.Max(0, index)));
            }
            return frames.Count == 0 ? null : new(vb[2], vb[3], frames, count, duration);
        }
        catch (Exception ex) { App.Log("Estrela do Claude: " + ex.Message); return null; }
    }

    private static OpenAi? LoadOpenAi()
    {
        try
        {
            string still = File.ReadAllText(System.IO.Path.Combine(Dir, "chatgpt-estatico.svg"));
            string spinFile = System.IO.Path.Combine(Dir, "chatgpt-rotacao.svg");
            string spin = File.Exists(spinFile) ? File.ReadAllText(spinFile) : string.Empty;
            var vb = ViewBox(still);
            var d = Regex.Match(still, " d=\"([^\"]+)\"");
            return d.Success ? new(vb[0], vb[1], vb[2], vb[3], d.Groups[1].Value, Dur(spin, TimeSpan.FromSeconds(2))) : null;
        }
        catch (Exception ex) { App.Log("Logotipo do ChatGPT: " + ex.Message); return null; }
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return Color.FromArgb(255, 0xD9, 0x76, 0x56);
        return Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
    }
}

/// <summary>
/// Claude trabalhando: a estrela vetorial animada (estrela-vetorial-animada.svg), quadro a quadro, sem fundo.
/// Cada quadro é um caminho que só fica opaco na sua vez, trocado pelo relógio único da barra (FrameClock): a tela só é
/// redesenhada quando o quadro muda (~11 vezes por segundo). Só anima com o controle visível e carregado e com alguém
/// vendo a tela (AnimationGate).
/// </summary>
public sealed class ClaudeWorkingDots : Grid
{
    private const double Size = 17;
    private readonly List<(Path Path, int Index)> _frames = [];
    private readonly int _frameCount = 1;
    private readonly double _duration = 0.81;
    private Action<double>? _tick;
    private int _shown;
    private bool _running;

    public ClaudeWorkingDots()
    {
        Width = Size; Height = Size; IsHitTestVisible = false;
        if (AgentLogoAssets.ClaudeStar is { } star)
        {
            // Enquadra a área ocupada por todos os quadros (quadrado em torno do centro), para a estrela ocupar
            // os 17 px como os ícones das cotas, sem mudar de posição entre quadros.
            var (x0, y0, side) = Bounds(star.Frames.Select(f => f.Data));
            var canvas = new Canvas { Width = side, Height = side };
            _frameCount = Math.Max(1, star.FrameCount);
            _duration = star.Duration.TotalSeconds;
            foreach (var f in star.Frames)
            {
                var path = new Path { Data = AgentLogoAssets.ToGeometry(f.Data), Fill = new SolidColorBrush(f.Fill), Opacity = f.Index == 0 ? 1 : 0 };
                Canvas.SetLeft(path, -x0); Canvas.SetTop(path, -y0);
                canvas.Children.Add(path);
                _frames.Add((path, f.Index));
            }
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = canvas });
        }
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
        AnimationGate.Watch(this, Sync);
    }

    /// <summary>Quadrado que contém todos os pontos dos caminhos (só comandos absolutos M/L/Z, como no SVG da estrela).</summary>
    private static (double X, double Y, double Side) Bounds(IEnumerable<string> paths)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var d in paths)
        {
            var n = Regex.Matches(d, @"-?[0-9]*\.?[0-9]+").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            for (int i = 0; i + 1 < n.Count; i += 2)
            { minX = Math.Min(minX, n[i]); maxX = Math.Max(maxX, n[i]); minY = Math.Min(minY, n[i + 1]); maxY = Math.Max(maxY, n[i + 1]); }
        }
        if (minX > maxX) return (0, 0, 120);
        double side = Math.Max(maxX - minX, maxY - minY);
        return ((minX + maxX - side) / 2, (minY + maxY - side) / 2, side);
    }

    private void Sync()
    {
        if (Visibility == Visibility.Visible && IsLoaded && AnimationGate.Allowed && _frames.Count > 0)
        {
            if (!_running) { _running = true; FrameClock.Add(_tick ??= OnFrame); }
        }
        else Stop();
    }

    /// <summary>Quadro da vez: cada um ocupa 1/<c>_frameCount</c> do ciclo do SVG (0,81 s).</summary>
    private void OnFrame(double t) => Show(Math.Min(_frameCount - 1, (int)(FrameClock.Phase(t, _duration) * _frameCount)));

    private void Show(int index)
    {
        if (index == _shown) return;
        _shown = index;
        foreach (var (path, i) in _frames) path.Opacity = i == index ? 1 : 0;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_tick is not null) FrameClock.Remove(_tick);
        Show(0);
    }
}

/// <summary>
/// Logotipo do ChatGPT em vetor (chatgpt-estatico.svg), na cor do texto da barra: branco no tema escuro e
/// preto no claro (o SVG é branco; no claro sai invertido). Trabalhando (<see cref="IsWorking"/>), gira no
/// próprio centro, uma volta linear por ciclo (chatgpt-rotacao.svg: 2 s); ao parar, volta suavemente a zero.
/// </summary>
public sealed class OpenAiLogo : Grid
{
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(
        nameof(IsWorking), typeof(bool), typeof(OpenAiLogo), new PropertyMetadata(false, (d, _) => ((OpenAiLogo)d).Sync()));
    public bool IsWorking { get => (bool)GetValue(IsWorkingProperty); set => SetValue(IsWorkingProperty, value); }

    private readonly RotateTransform _rotate = new();
    private readonly Path? _path;
    private readonly TimeSpan _turn = TimeSpan.FromSeconds(2);
    private Storyboard? _story;
    private Action<double>? _tick;
    private bool _spinning;

    public OpenAiLogo()
    {
        IsHitTestVisible = false;
        if (AgentLogoAssets.OpenAiLogo is { } logo)
        {
            _turn = logo.Turn;
            _path = new Path { Data = AgentLogoAssets.ToGeometry(logo.Data) };
            // Enquadra só o símbolo (grade 24×24, sem a margem do viewBox), para ocupar os 17 px como os ícones das cotas.
            var canvas = new Canvas { Width = 24, Height = 24, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = _rotate };
            canvas.Children.Add(_path);
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = canvas });
            ApplyColor();
        }
        Loaded += (_, _) => { ApplyColor(); Sync(); };
        Unloaded += (_, _) => { if (_tick is not null) FrameClock.Remove(_tick); _story?.Stop(); _story = null; _spinning = false; };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
        ActualThemeChanged += (_, _) => ApplyColor();
        AnimationGate.Watch(this, Sync);
    }

    /// <summary>Branco no tema escuro, preto no claro: o mesmo pincel do texto e dos ícones da barra.</summary>
    private void ApplyColor()
    {
        if (_path is null) return;
        var dark = ActualTheme == ElementTheme.Dark;
        _path.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(0xE4, 0, 0, 0));
    }

    private void Sync()
    {
        bool want = IsWorking && IsLoaded && Visibility == Visibility.Visible && AnimationGate.Allowed;
        if (want == _spinning) return;
        _spinning = want;
        // Sem soltar o storyboard da volta a zero, o valor animado dele ficaria por cima do ângulo definido pelo relógio.
        _story?.Stop(); _story = null;
        if (want)
        {
            // Uma volta linear por ciclo, pelo relógio único da barra (~32 quadros por segundo).
            FrameClock.Add(_tick ??= t => _rotate.Angle = 360 * FrameClock.Phase(t, _turn.TotalSeconds));
            return;
        }
        if (_tick is not null) FrameClock.Remove(_tick);
        // Ao parar, volta suavemente a zero (250 ms, uma vez só).
        var anim = new DoubleAnimation { From = _rotate.Angle % 360, To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _story = new Storyboard();
        Storyboard.SetTarget(anim, _rotate);
        Storyboard.SetTargetProperty(anim, "Angle");
        _story.Children.Add(anim);
        _story.Begin();
    }
}

/// <summary>
/// Velocidade da animação de um ProgressRing (<c>local:RingSpeed.Rate="0.8"</c> = 20% mais lento) e o círculo girando
/// pelo relógio único da barra: no modo indeterminado, o AnimatedVisualPlayer do modelo do controle fica parado e o
/// progresso da animação é definido a cada quadro do relógio (~32 por segundo), em vez de tocar na taxa da tela
/// (165 Hz neste PC). Sem ninguém vendo (tela apagada ou sessão bloqueada), o círculo girando é desativado.
/// </summary>
public static class RingSpeed
{
    public static readonly DependencyProperty RateProperty = DependencyProperty.RegisterAttached(
        "Rate", typeof(double), typeof(RingSpeed), new PropertyMetadata(1.0, (d, _) => Hook(d)));
    private static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(Action<double>), typeof(RingSpeed), new PropertyMetadata(null));

    public static double GetRate(DependencyObject o) => (double)o.GetValue(RateProperty);
    public static void SetRate(DependencyObject o, double value) => o.SetValue(RateProperty, value);

    private static void Hook(DependencyObject d)
    {
        if (d is not ProgressRing ring) return;
        ring.Loaded -= OnLoaded; ring.Loaded += OnLoaded;
        ring.Unloaded -= OnUnloaded; ring.Unloaded += OnUnloaded;
        ring.RegisterPropertyChangedCallback(ProgressRing.IsIndeterminateProperty, (s, _) => Refresh((ProgressRing)s));
        ring.RegisterPropertyChangedCallback(ProgressRing.IsActiveProperty, (s, _) => Drive((ProgressRing)s));
        ring.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (s, _) => Refresh((ProgressRing)s));
        AnimationGate.Watch(ring, () => Refresh(ring));
        Refresh(ring);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Refresh((ProgressRing)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Drive((ProgressRing)sender);

    private static void Refresh(ProgressRing ring)
    {
        // Círculo girando sem ninguém para ver: desativado até a tela acender. O de porcentagem não anima e fica ativo.
        ring.IsActive = !(ring.IsIndeterminate && Core.Presence.Away);
        if (Player(ring) is { } player) { player.PlaybackRate = GetRate(ring); Drive(ring); return; }
        // O modelo pode ainda não estar aplicado: de novo após o próximo layout.
        ring.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Player(ring) is { } later) later.PlaybackRate = GetRate(ring);
            Drive(ring);
        });
    }

    private static void Drive(ProgressRing ring)
    {
        var current = ring.GetValue(SpinProperty) as Action<double>;
        var player = ring.IsIndeterminate && ring.IsActive && ring.IsLoaded && ring.Visibility == Visibility.Visible && AnimationGate.Allowed
            ? Player(ring) : null;
        if (player is null)
        {
            if (current is not null) { FrameClock.Remove(current); ring.ClearValue(SpinProperty); }
            return;
        }
        if (current is not null) return;
        Action<double> frame = t =>
        {
            double seconds = player.Duration.TotalSeconds;
            if (seconds <= 0) return;
            if (player.IsPlaying) player.Pause(); // o controle volta a tocar quando muda de estado: o relógio manda
            player.SetProgress(FrameClock.Phase(t * GetRate(ring), seconds));
        };
        ring.SetValue(SpinProperty, frame);
        FrameClock.Add(frame);
    }

    private static AnimatedVisualPlayer? Player(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is AnimatedVisualPlayer player) return player;
            if (Player(child) is { } found) return found;
        }
        return null;
    }
}
