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
    private readonly Storyboard _story = new() { RepeatBehavior = RepeatBehavior.Forever };
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
                var animation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3) };
                for (int i = 0; i < layer.Frames.Length; i++)
                    animation.KeyFrames.Add(new DiscreteObjectKeyFrame { KeyTime = TimeSpan.FromSeconds(3.0 * i / layer.Frames.Length), Value = layer.Frames[i] });
                Storyboard.SetTarget(animation, path);
                Storyboard.SetTargetProperty(animation, "Data");
                _story.Children.Add(animation);
            }
        }
        catch (Exception ex) { global::WinStatusBar.App.Log("Ícone Ollama: " + ex.Message); }
        Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
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
        if (!IsWorking || !IsLoaded || Visibility != Visibility.Visible) { Stop(); return; }
        if (!_running && _story.Children.Count > 0) { _story.Begin(); _running = true; }
    }
    private void Stop() { if (_running) _story.Stop(); _running = false; }
}
