using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinStatusBar.Views;

/// <summary>SVGs fornecidos pelo usuário: exterior transparente, interior branco e traços pretos nos dois temas.</summary>
public sealed class OllamaLogo : Grid
{
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(nameof(IsWorking), typeof(bool),
        typeof(OllamaLogo), new PropertyMetadata(false, (obj, _) => ((OllamaLogo)obj).Sync()));
    public bool IsWorking { get => (bool)GetValue(IsWorkingProperty); set => SetValue(IsWorkingProperty, value); }
    private sealed record Layer(Geometry Still, Geometry[] Frames, bool White);
    private static readonly Lazy<Layer[]> Layers = new(Load);
    /// <summary>Ondulação em 3 s, um quadro de cada camada por vez, pelo relógio único da barra (FrameClock).</summary>
    private const double Cycle = 3.0;
    private readonly List<(Path Path, Layer Layer)> _paths = [];
    private int[] _shown = [];
    private Action<double>? _tick;
    private bool _running;

    public OllamaLogo()
    {
        IsHitTestVisible = false;
        var canvas = new Canvas { Width = 512, Height = 512 }; // sem pincel de fundo
        try
        {
            foreach (var layer in Layers.Value)
            {
                var path = new Path { Data = layer.Still, Fill = new SolidColorBrush(layer.White ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black) };
                canvas.Children.Add(path);
                _paths.Add((path, layer));
            }
            _shown = Enumerable.Repeat(-1, _paths.Count).ToArray();
        }
        catch (Exception ex) { global::WinStatusBar.App.Log("Ícone Ollama: " + ex.Message); }
        Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
        AnimationGate.Watch(this, Sync);
    }

    private static Layer[] Load()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLogos");
        var still = XDocument.Load(System.IO.Path.Combine(dir, "ollama-estatico.svg")).Descendants().Where(e => e.Name.LocalName == "path").ToArray();
        var wave = XDocument.Load(System.IO.Path.Combine(dir, "ollama-ondulacao.svg")).Descendants().Where(e => e.Name.LocalName == "path").ToArray();
        return still.Select((path, index) =>
        {
            var values = wave[index].Elements().First(e => e.Name.LocalName == "animate").Attribute("values")!.Value.Split(';');
            var frames = new List<Geometry>();
            // Amostra os próprios quadros e seus pontos médios: ~27 fps, mantendo a interpolação linear do SVG.
            for (int i = 0; i < values.Length - 1; i++)
            {
                frames.Add(AgentLogoAssets.ToGeometry("F1 " + values[i]));
                frames.Add(AgentLogoAssets.ToGeometry("F1 " + Midpoint(values[i], values[i + 1])));
            }
            return new Layer(AgentLogoAssets.ToGeometry("F1 " + path.Attribute("d")!.Value), frames.ToArray(),
                path.Attribute("fill")?.Value == "white");
        }).ToArray();
    }

    private static string Midpoint(string first, string second)
    {
        const string number = @"[-+]?(?:\d*\.?\d+)(?:[eE][-+]?\d+)?";
        var values = Regex.Matches(second, number).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        int index = 0;
        return Regex.Replace(first, number, m => ((double.Parse(m.Value, CultureInfo.InvariantCulture) + values[index++]) / 2).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private void Sync()
    {
        if (!IsWorking || !IsLoaded || Visibility != Visibility.Visible || !AnimationGate.Allowed) { Stop(); return; }
        if (!_running && _paths.Count > 0) { _running = true; FrameClock.Add(_tick ??= OnFrame); }
    }

    /// <summary>Troca o desenho de cada camada só quando o quadro muda (~27 vezes por segundo).</summary>
    private void OnFrame(double t)
    {
        double phase = FrameClock.Phase(t, Cycle);
        for (int k = 0; k < _paths.Count; k++)
        {
            var (path, layer) = _paths[k];
            if (layer.Frames.Length == 0) continue;
            int i = Math.Min(layer.Frames.Length - 1, (int)(phase * layer.Frames.Length));
            if (i == _shown[k]) continue;
            _shown[k] = i;
            path.Data = layer.Frames[i];
        }
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_tick is not null) FrameClock.Remove(_tick);
        for (int k = 0; k < _paths.Count; k++) { _paths[k].Path.Data = _paths[k].Layer.Still; _shown[k] = -1; }
    }
}
