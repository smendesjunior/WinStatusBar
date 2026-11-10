using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace WinStatusBar.Views;

/// <summary>
/// Relógio único das animações contínuas da barra (brilho do contorno, estrela do Claude, logotipos girando, círculo da
/// tarefa): ~32 quadros por segundo num temporizador da thread da interface. Animações do compositor e storyboards do
/// XAML mantêm a composição rodando na taxa da tela (165 Hz neste PC) enquanto existirem, mesmo quando o valor muda bem
/// menos: medido em 10/11/2026, ~11% de um núcleo só na thread do compositor com uma IA trabalhando. Com os valores
/// definidos a cada quadro deste relógio, a tela é redesenhada ~32 vezes por segundo, com a mesma aparência.
/// </summary>
internal static class FrameClock
{
    /// <summary>Dois tiques do relógio padrão do Windows (15,6 ms): ~32 quadros por segundo.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(31);
    private static readonly Stopwatch Time = Stopwatch.StartNew();
    [ThreadStatic] private static List<Action<double>>? _frames;
    [ThreadStatic] private static DispatcherQueueTimer? _timer;

    /// <summary>Segundos desde o início do relógio: tempo contínuo, as animações não voltam ao começo ao recomeçar.</summary>
    public static double Now => Time.Elapsed.TotalSeconds;

    /// <summary>Fração (0 a 1) de um ciclo de <paramref name="seconds"/> no instante <paramref name="t"/>.</summary>
    public static double Phase(double t, double seconds) => seconds <= 0 ? 0 : t / seconds - Math.Floor(t / seconds);

    /// <summary>Passa a receber os quadros (na thread da interface que chamou) e já desenha o quadro atual.</summary>
    public static void Add(Action<double> frame)
    {
        var frames = _frames ??= [];
        if (frames.Contains(frame)) return;
        frames.Add(frame);
        if (_timer is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = Period;
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => { double t = Now; foreach (var f in frames.ToArray()) f(t); };
        }
        if (_timer is { IsRunning: false }) _timer.Start();
        frame(Now);
    }

    /// <summary>Para de receber quadros; sem nenhuma animação, o temporizador para.</summary>
    public static void Remove(Action<double> frame)
    {
        if (_frames is { } frames && frames.Remove(frame) && frames.Count == 0) _timer?.Stop();
    }
}
