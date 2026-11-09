using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinStatusBar.Core.Settings;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Services;

/// <summary>
/// Fundo da barra no modo "Adaptativo": uma cor sólida que pode ir até totalmente transparente (mostra o papel de parede).
/// Usa um pincel de composição do sistema como SystemBackdrop, com transição suave entre as cores.
/// </summary>
internal sealed class TintBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
{
    private static object? _queueController;
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;
    private Color _color = Color.FromArgb(0, 0, 0, 0);

    private ICompositionSupportsSystemBackdrop? _target;
    private XamlRoot? _root;

    /// <summary>
    /// Reaplica a transparência e o mesmo pincel sem desconectar o fundo. Recriar o SystemBackdrop (null → novo)
    /// deixava um instante sem fundo a cada ativação da janela: o "flick" ao clicar em menus e opções.
    /// </summary>
    public void Reapply()
    {
        if (_target is null || _root is null || _brush is null) return;
        EnableDesktopTransparency(_root);
        _target.SystemBackdrop = _brush;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _target = target; _root = xamlRoot;
        EnableDesktopTransparency(xamlRoot);
        EnsureSystemDispatcherQueue();
        _compositor ??= new Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(_color);
        target.SystemBackdrop = _brush;
    }

    /// <summary>
    /// A implementação base espera um material do sistema (Mica/Acrílico) e lança ArgumentException("target")
    /// quando o tema/foco muda com o nosso pincel de cor. Aqui a cor é do modo adaptativo: só reaplica o pincel.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // Ao ativar/desativar a janela (ex.: clique na barra de comando), o Windows volta ao fundo padrão escuro:
        // reaplica o nosso pincel (transparente na área de trabalho, cor da barra de título com janela maximizada).
        // Chamado pelo Windows na troca de ativação; uma exceção aqui chega como "não tratada" (visto no log ao alternar
        // entre Word e Claude). Falhar em reaplicar só mantém o fundo atual: a próxima leitura adaptativa corrige.
        try
        {
            if (_brush is not null && target is not null)
            {
                if (xamlRoot is not null) EnableDesktopTransparency(xamlRoot);
                target.SystemBackdrop = _brush;
            }
        }
        catch (Exception ex) { App.Log("Reaplicar fundo adaptativo: " + ex.Message); }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        _brush?.Dispose(); _brush = null;
        _target = null; _root = null;
        base.OnTargetDisconnected(target);
    }

    public Color Color => _color;

    // Um pincel com alpha zero não basta: sem esta configuração o DWM pode
    // compor a superfície da janela sobre preto, sobretudo no monitor sem apps.
    // Região vazia habilita o canal alpha sem introduzir blur ou sombra.
    internal static void EnableDesktopTransparency(XamlRoot xamlRoot)
    {
        if (xamlRoot.ContentIslandEnvironment is not { } environment) return;
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero) return;
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            int hr = DwmEnableBlurBehindWindow(hwnd, ref blur);
            if (hr < 0) App.Log($"Transparência DWM: HRESULT 0x{hr:X8}");
        }
        finally { DeleteObject(region); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind { public uint Flags; public int Enable; public IntPtr Region; public int TransitionOnMaximized; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);

    public void SetColor(Color color, bool animate)
    {
        _color = color;
        if (_brush is null || _compositor is null) return;
        if (!animate) { _brush.StopAnimation("Color"); _brush.Color = color; return; }
        var anim = _compositor.CreateColorKeyFrameAnimation();
        anim.InsertKeyFrame(1f, color);
        anim.Duration = TimeSpan.FromMilliseconds(120);
        _brush.StartAnimation("Color", anim);
    }

    /// <summary>O compositor do sistema (Windows.UI.Composition) exige um DispatcherQueue do Windows na thread de UI.</summary>
    internal static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || _queueController is not null) return;
        var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* atual */, apartmentType = 2 /* STA */ };
        if (CreateDispatcherQueueController(options, out var controller) == 0) _queueController = controller;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);
}

/// <summary>
/// Resultado completo de uma leitura, só deste monitor. Cada barra decide com o próprio resultado.
/// <paramref name="Active"/>: a janela decisora é a janela ativa (barra de título no visual ativo).
/// <paramref name="FallbackLike"/>: a barra de título está na cor de reserva do material (janela inativa) — material inativo e
/// cor sólida ficam idênticos, então a barra mantém o modo em que está (sem recriar o fundo).
/// <paramref name="Luminance"/>: luminância relativa média (0–1) do papel de parede atrás da barra transparente.
/// <paramref name="Brightness"/>: a mesma luminância por trecho de 64 px (centro em coordenadas de tela): cada parte da barra
/// escolhe texto e ícones claros ou escuros pelo que está atrás dela.
/// </summary>
internal sealed record AdaptiveSample(AdaptiveState State, Color? Color, BackdropKind Material, bool Active, bool? WindowDark,
                                      bool Covered, IntPtr Target, RECT Bounds, bool FallbackLike, string Info,
                                      IReadOnlyList<(double Offset, Color Color)>? Stops = null, double? Luminance = null,
                                      IReadOnlyList<(int X, double Luminance)>? Brightness = null)
{
    /// <summary>Descrição para o log (nome do processo): só quando o estado muda, porque custa ~8 ms.</summary>
    public string Describe() => Target == IntPtr.Zero ? "área de trabalho" : TitleBarColorSampler.DescribeWindow(Target, Bounds);
}

/// <summary>
/// Avisa em tempo real quando algo que muda o fundo adaptativo acontece: troca de janela em primeiro plano,
/// minimizar/restaurar, maximizar, mover/redimensionar, janela aberta ou fechada (SetWinEventHook, fora do processo,
/// entregue na thread de UI). Um único conjunto de ganchos para todas as barras; cada barra filtra pelo próprio monitor.
/// </summary>
internal static class WindowChangeEvents
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    private static readonly WinEventProc Callback = OnEvent;
    private static readonly List<IntPtr> Hooks = new();
    private static int _subscribers;
    private static event Action<uint, IntPtr>? ChangedInternal;

    /// <summary>(evento, janela de nível superior).</summary>
    public static event Action<uint, IntPtr> Changed
    {
        add { ChangedInternal += value; if (++_subscribers == 1) Install(); }
        remove { ChangedInternal -= value; if (--_subscribers == 0) Uninstall(); }
    }

    private static void Install()
    {
        (uint Min, uint Max)[] ranges =
        [
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND), (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND),
            (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND), (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE),
            (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE), (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
        ];
        foreach (var (min, max) in ranges)
        {
            var hook = SetWinEventHook(min, max, IntPtr.Zero, Callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero) Hooks.Add(hook);
        }
    }

    private static void Uninstall()
    {
        foreach (var h in Hooks) UnhookWinEvent(h);
        Hooks.Clear();
    }

    private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Só a própria janela (não cursor, caret ou controles internos), e só janelas de nível superior.
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (ev is EVENT_OBJECT_LOCATIONCHANGE or EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE or EVENT_OBJECT_DESTROY
            && GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
        ChangedInternal?.Invoke(ev, hwnd);
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}

/// <summary>
/// Lê a linha da barra de título logo abaixo da barra, numa única captura por leitura (roda fora da thread da interface).
/// Decide a janela que encosta na barra (a primeira maximizada na ordem Z, ou janelas encaixadas no topo), ignora os
/// pixels de qualquer janela que esteja na frente dela, e informa se ela está ativa e o material que declara ao Windows.
/// </summary>
internal static class TitleBarColorSampler
{
    public static AdaptiveSample Sample(MonitorInfo monitor, uint selfPid, bool barTransparent)
    {
        RECT work = monitor.Work;
        IntPtr target = IntPtr.Zero; RECT bounds = default; int targetZ = int.MaxValue;
        IntPtr windowed = IntPtr.Zero; RECT windowedBounds = default;
        var front = new List<(int Z, RECT R)>();        // janelas na frente (inclusive menus da barra): pixels não contam
        var tops = new List<(int Z, IntPtr Hwnd, RECT R)>(); // janelas encostadas no topo sem ocupar a largura (Snap)
        int z = 0, workWidth = work.Right - work.Left;
        EnumWindows((hwnd, _) =>
        {
            z++;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
            // Precisa cruzar a área de trabalho deste monitor.
            if (r.Right <= work.Left || r.Left >= work.Right || r.Bottom <= work.Top || r.Top >= work.Bottom) return true;
            long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TRANSPARENT) != 0) return true; // sobreposições que deixam o clique passar (efeitos, gravação)
            bool overlayWide = r.Right - r.Left >= workWidth * 9 / 10;
            GetWindowThreadProcessId(hwnd, out uint pid);
            // Menus e painéis da própria barra abrem logo abaixo dela, sobre a linha lida.
            if (pid == selfPid) { front.Add((z, r)); return true; }
            // Dicas, notificações, mini players e janelas pequenas não decidem a cor, mas os pixels delas não contam.
            if ((ex & WS_EX_TOOLWINDOW) != 0 || r.Right - r.Left < 50 || r.Bottom - r.Top < 50 || (ex & WS_EX_TOPMOST) != 0 && !Covers(r, work))
            {
                if (!overlayWide) front.Add((z, r));
                return true;
            }
            // Área de trabalho (ícones/papel de parede) encerra a busca: não há janela de app atrás.
            if (ClassOf(hwnd) is "Progman" or "WorkerW") return false;
            if (IsZoomed(hwnd) || Covers(r, work)) { target = hwnd; bounds = r; targetZ = z; return false; }
            if (r.Top <= work.Top + 2 && r.Right - r.Left >= 200) { tops.Add((z, hwnd, r)); return true; }
            if (windowed == IntPtr.Zero) { windowed = hwnd; windowedBounds = r; }
            front.Add((z, r));
            return true;
        }, IntPtr.Zero);

        int y = target != IntPtr.Zero && tops.Count == 0 ? Math.Max(bounds.Top, work.Top) + 4 : work.Top + 4;
        var row = CaptureRow(work.Left, y, workWidth);
        if (row is null)
            return new(AdaptiveState.Desktop, null, BackdropKind.MicaAlt, false, null, true, IntPtr.Zero, default, false, "captura falhou");
        List<(int Left, int Right)> Skip(int beforeZ) => AdaptiveLogic.CoveredAt(front.Where(f => f.Z < beforeZ).Select(f => (f.R.Left, f.R.Top, f.R.Right, f.R.Bottom)), y);
        // Papel de parede atrás da barra: com a barra transparente, a linha de cima dela (acima das pílulas) é o próprio
        // papel de parede; a linha logo abaixo (sem as janelas da frente) completa onde a de cima não serve.
        var below = TitleBarColorSampler.BlockMedians(row, workWidth, work.Left, Skip(int.MaxValue));
        var behind = barTransparent && CaptureRow(work.Left, monitor.Bounds.Top + 2, workWidth) is { } topRow
            ? TitleBarColorSampler.BlockMedians(topRow, workWidth, work.Left, new()) : new();
        var brightness = behind.Concat(below).GroupBy(b => b.X)
            .Select(g => (X: g.Key, Luminance: g.Average(b => AdaptiveLogic.RelativeLuminance(b.Color)))).OrderBy(b => b.X).ToList();
        double? luminance = brightness.Count > 0 ? brightness.Average(b => b.Luminance) : null;

        // Janela encaixada (Snap) ou duas lado a lado encostadas no topo: a barra não segue nenhuma das barras de título;
        // fica na cor sólida do tema do Windows (pedido de 08/11/2026). Uma janela na metade da tela já basta.
        string topInfo = string.Empty;
        if (tops.Count > 0)
        {
            var segs = AdaptiveLogic.Segments(tops.Select(t => (t.R.Left, t.R.Right)).ToList(), target != IntPtr.Zero, work.Left, work.Right);
            int snappedWidth = segs.Where(s => s.Owner >= 0).Sum(s => s.Right - s.Left);
            topInfo = $" · encaixadas {tops.Count}, {snappedWidth * 100 / Math.Max(1, workWidth)}% da largura";
            if (snappedWidth >= workWidth / 10)
            {
                var main = segs.Where(s => s.Owner >= 0).MaxBy(s => s.Right - s.Left);
                return new(AdaptiveState.Segments, null, BackdropKind.MicaAlt, false, null, false, tops[main.Owner].Hwnd, tops[main.Owner].R, false,
                           $"{tops.Count} janela(s) encaixada(s){(target != IntPtr.Zero ? " + maximizada atrás" : string.Empty)} · {snappedWidth * 100 / Math.Max(1, workWidth)}% da largura", Luminance: luminance);
            }
        }

        // Nenhuma janela ocupando o topo: só janelas menores (transparente, como na área de trabalho) ou nada.
        if (target == IntPtr.Zero && luminance is { } wallY)
            topInfo += $" · papel de parede Y={wallY:0.00} ({(barTransparent ? "atrás da barra" : "logo abaixo")})";
        if (target == IntPtr.Zero)
            return windowed == IntPtr.Zero
                ? new(AdaptiveState.Desktop, null, BackdropKind.MicaAlt, false, null, false, IntPtr.Zero, default, false, topInfo.TrimStart(' ', '·'), Luminance: luminance, Brightness: brightness)
                : new(AdaptiveState.Windowed, null, BackdropKind.MicaAlt, false, null, false, windowed, windowedBounds, false, topInfo.TrimStart(' ', '·'), Luminance: luminance, Brightness: brightness);

        int sliceLeft = Math.Max(bounds.Left, work.Left) + 6, sliceRight = Math.Min(bounds.Right, work.Right) - 6;
        var analysis = Analyze(row, work.Left, sliceLeft, sliceRight, Skip(targetZ));
        var color = analysis.Color;
        bool covered = analysis.Usable < (sliceRight - sliceLeft) / 3;
        bool active = GetForegroundWindow() == target;
        BackdropKind? declared = DwmGetWindowAttribute(target, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, out int sbt, sizeof(int)) == 0
            ? sbt switch { 2 => BackdropKind.Mica, 3 => BackdropKind.Acrylic, 4 => BackdropKind.MicaAlt, _ => null }
            : null;
        // Cor de legenda definida pelo app: só vale como confirmação da cor vista (o padrão do Windows não é uma cor).
        Color? caption = null;
        if (color is { } seenColor && DwmGetWindowAttribute(target, 35 /* DWMWA_CAPTION_COLOR */, out int colorRef, sizeof(int)) == 0
            && colorRef is not unchecked((int)0xFFFFFFFF) and not unchecked((int)0xFFFFFFFE))
        {
            var c = Color.FromArgb(255, (byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
            if (AdaptiveLogic.Distance(c, seenColor) <= 12) caption = c;
        }
        bool accent = AccentOnTitleBars();
        // Tema da janela: a cor vista na tela tem prioridade (o Office pinta escuro sem informar o modo escuro ao Windows).
        bool? dark = color is { } seen ? DarkFromColor(seen)
            : DwmGetWindowAttribute(target, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, out int darkMode, sizeof(int)) == 0 ? darkMode != 0 : null;
        string info = (color is { } sc ? $"cor #{sc.R:X2}{sc.G:X2}{sc.B:X2}" : "sem cor dominante")
                      + (active ? " · ativa" : " · inativa")
                      + (dark is bool d ? (d ? " · tema escuro" : " · tema claro") : string.Empty)
                      + (declared is not null ? $" · declara {declared}" : string.Empty)
                      + (accent ? " · destaque nas barras de título" : string.Empty)
                      + (caption is not null ? " · cor de legenda do app" : string.Empty)
                      + (analysis.GradientInfo.Length > 0 ? " · " + analysis.GradientInfo : string.Empty) + topInfo;

        // Material pintado pelo próprio app (o Office desenha um Mica próprio e não declara nada ao Windows): estende a linha
        // da barra de título para cima — cada trecho de 64 px vira uma parada de degradê horizontal —, sem emenda.
        if (!covered && declared is null && caption is null && analysis.Blocks.Count >= 4 && (analysis.Gradient || IsOfficeFrame(target)))
        {
            double span = Math.Max(1, work.Right - work.Left);
            var stops = analysis.Blocks.Select(b => ((b.X - work.Left) / span, b.Color)).ToList();
            var average = Color.FromArgb(255, (byte)analysis.Blocks.Average(b => b.Color.R), (byte)analysis.Blocks.Average(b => b.Color.G), (byte)analysis.Blocks.Average(b => b.Color.B));
            return new(AdaptiveState.Painted, average, BackdropKind.MicaAlt, active, DarkFromColor(average), covered, target, bounds, false, info + " · estendida", stops, luminance);
        }
        var (state, isActive) = AdaptiveLogic.Classify(declared, active, accent, color, analysis.Gradient, caption);
        bool fallbackLike = declared is not null && color is { } fc && AdaptiveLogic.IsFallbackColor(fc) && !active;
        return new(state, color, declared ?? BackdropKind.MicaAlt, isActive, dark, covered, target, bounds, fallbackLike, info, Luminance: luminance);
    }

    /// <summary>"Mostrar cor de destaque nas barras de título" (Personalização › Cores). Lido a cada 5 s no máximo.</summary>
    private static bool AccentOnTitleBars()
    {
        if (Environment.TickCount64 - _accentReadAt < 5000) return _accentOn;
        _accentReadAt = Environment.TickCount64;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            _accentOn = key?.GetValue("ColorPrevalence") is int v && v == 1;
        }
        catch { _accentOn = false; }
        return _accentOn;
    }
    private static long _accentReadAt = -10000;
    private static bool _accentOn;

    internal static string DescribeWindow(IntPtr hwnd, RECT r)
    {
        string proc = string.Empty;
        try { GetWindowThreadProcessId(hwnd, out uint pid); proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        return $"{proc}/{ClassOf(hwnd)} {r.Left},{r.Top},{r.Right},{r.Bottom}{(IsZoomed(hwnd) ? " maximizada" : "")}";
    }

    /// <summary>Janelas principais do Office (Word, Excel, PowerPoint, Outlook), que pintam o próprio Mica.</summary>
    private static bool IsOfficeFrame(IntPtr hwnd) => ClassOf(hwnd) is "OpusApp" or "XLMAIN" or "PPTFrameClass" or "rctrl_renwnd32";

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder name, int max);

    /// <summary>
    /// Janela que se comporta como maximizada: encosta no topo e ocupa a largura toda (folga de 1% de cada lado, porque
    /// janelas no tamanho normal quase do tamanho da tela ficam alguns pixels antes da borda). A barra segue a barra de título dela.
    /// </summary>
    private static bool Covers(RECT r, RECT work)
    {
        int slack = Math.Max(2, (work.Right - work.Left) / 100);
        return r.Left <= work.Left + slack && r.Right >= work.Right - slack && r.Top <= work.Top + 2;
    }

    private sealed record Analysis(Color? Color, int Usable, bool Gradient, string GradientInfo, List<(int X, Color Color)> Blocks);

    /// <summary>
    /// Cor da barra de título num trecho [left, right) da linha capturada (coordenadas de tela), sem os pixels cobertos.
    /// A cor de fundo real é a da área vazia de arrastar, logo à esquerda dos botões minimizar/maximizar/fechar: sem
    /// abas, texto ou ícones (a "mais frequente" da linha inteira mudava conforme o tamanho das abas).
    /// </summary>
    private static Analysis Analyze(byte[] row, int rowLeft, int left, int right, List<(int Left, int Right)> skip)
    {
        int width = right - left;
        if (width < 16) return new(null, 0, false, string.Empty, new());
        var pixels = new byte[width * 4];
        Array.Copy(row, (left - rowLeft) * 4, pixels, 0, width * 4);
        bool Covered(int px) { foreach (var (l, rr) in skip) if (px >= l && px < rr) return true; return false; }
        var bins = new Dictionary<int, (int Count, long R, long G, long B)>();
        int usable = 0;
        for (int i = 0; i < width; i++)
        {
            if (Covered(left + i)) continue;
            usable++;
            byte b = pixels[i * 4], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
            int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
            var cur = bins.GetValueOrDefault(key);
            bins[key] = (cur.Count + 1, cur.R + r, cur.G + g, cur.B + b);
        }
        if (usable < width / 3 || bins.Count == 0) return new(null, usable, false, string.Empty, new());
        bool gradient = IsGradient(pixels, width, left, skip, out string gradientInfo);
        var blocks = BlockMedians(pixels, width, left, skip);
        int dragEnd = width - Math.Max(140, width / 12), dragStart = Math.Max(0, dragEnd - Math.Max(160, width / 6));
        var drag = new Dictionary<int, (int Count, long R, long G, long B)>();
        int dragUsable = 0;
        for (int i = dragStart; i < dragEnd; i++)
        {
            if (Covered(left + i)) continue;
            dragUsable++;
            byte b = pixels[i * 4], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
            int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
            var cur = drag.GetValueOrDefault(key);
            drag[key] = (cur.Count + 1, cur.R + r, cur.G + g, cur.B + b);
        }
        if (!gradient && dragUsable >= 64 && drag.Count > 0)
        {
            var d = drag.Values.MaxBy(v => v.Count);
            if (d.Count >= dragUsable / 2)
                return new(Color.FromArgb(255, (byte)(d.R / d.Count), (byte)(d.G / d.Count), (byte)(d.B / d.Count)), usable, gradient, gradientInfo, blocks);
        }
        var top = bins.Values.MaxBy(v => v.Count);
        if (top.Count < usable / 5) return new(null, usable, gradient, gradientInfo, blocks); // sem cor dominante (imagem/vídeo)
        return new(Color.FromArgb(255, (byte)(top.R / top.Count), (byte)(top.G / top.Count), (byte)(top.B / top.Count)), usable, gradient, gradientInfo, blocks);
    }

    /// <summary>
    /// Degradê de material (Mica/Acrílico desenhados pelo app): a cor mediana de blocos de 64 px muda de forma
    /// contínua ao longo da linha (variação total ≥ 6, saltos ≤ 6, mudança em ≥ 40% dos blocos). A mediana ignora
    /// ícones e texto; cor sólida tem variação quase zero e abas/regiões de outra cor geram degraus isolados.
    /// </summary>
    internal static bool IsGradient(byte[] pixels, int width, int x, List<(int Left, int Right)> skip, out string info)
    {
        info = string.Empty;
        var medians = BlockMedians(pixels, width, x, skip).Select(b => (R: (int)b.Color.R, G: (int)b.Color.G, B: (int)b.Color.B)).ToList();
        if (medians.Count < 4) return false;
        int spread = Math.Max(medians.Max(m => m.R) - medians.Min(m => m.R),
                     Math.Max(medians.Max(m => m.G) - medians.Min(m => m.G), medians.Max(m => m.B) - medians.Min(m => m.B)));
        int maxStep = 0, changed = 0;
        for (int k = 1; k < medians.Count; k++)
        {
            int step = Math.Max(Math.Abs(medians[k].R - medians[k - 1].R),
                       Math.Max(Math.Abs(medians[k].G - medians[k - 1].G), Math.Abs(medians[k].B - medians[k - 1].B)));
            maxStep = Math.Max(maxStep, step);
            if (step >= 1) changed++;
        }
        info = $"variação {spread}, maior salto {maxStep}, blocos que mudam {changed}/{medians.Count - 1}";
        return spread >= 6 && maxStep <= 6 && changed >= (medians.Count - 1) * 0.4;
    }

    /// <summary>
    /// Cor mediana de cada trecho de 64 px da linha, com o centro do trecho em coordenadas de tela. A mediana descarta
    /// texto, ícones e botões estreitos; trechos cobertos por outras janelas são pulados.
    /// </summary>
    internal static List<(int X, Color Color)> BlockMedians(byte[] pixels, int width, int x, List<(int Left, int Right)> skip)
    {
        const int Block = 64;
        var list = new List<(int, Color)>();
        var rs = new List<int>(Block); var gs = new List<int>(Block); var bs = new List<int>(Block);
        for (int start = 0; start + Block <= width; start += Block)
        {
            rs.Clear(); gs.Clear(); bs.Clear();
            for (int i = start; i < start + Block; i++)
            {
                int px = x + i;
                bool covered = false;
                foreach (var (l, rr) in skip) if (px >= l && px < rr) { covered = true; break; }
                if (covered) continue;
                bs.Add(pixels[i * 4]); gs.Add(pixels[i * 4 + 1]); rs.Add(pixels[i * 4 + 2]);
            }
            if (rs.Count < Block / 2) continue;
            rs.Sort(); gs.Sort(); bs.Sort();
            list.Add((x + start + Block / 2, Color.FromArgb(255, (byte)rs[rs.Count / 2], (byte)gs[gs.Count / 2], (byte)bs[bs.Count / 2])));
        }
        return list;
    }

    /// <summary>Uma linha de pixels da tela (BGRA). Uma captura por leitura (~6 ms; fora da thread da interface).</summary>
    private static byte[]? CaptureRow(int x, int y, int width)
    {
        if (width < 16) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, width, 1);
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, width, 1, screen, x, y, SRCCOPY)) return null;
            SelectObject(mem, old); old = IntPtr.Zero;
            var info = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -1, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[width * 4];
            return GetDIBits(mem, bmp, 0, 1, pixels, ref info, 0) == 0 ? null : pixels;
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static bool IsLight(Color c) => Luma(c) > 150;

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    /// <summary>Tema do material pela cor da barra de título: Mica escuro fica perto de #202020, claro perto de #F3F3F3.</summary>
    internal static bool DarkFromColor(Color c) => Luma(c) < 128;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8;
    private const uint SRCCOPY = 0x00CC0020;
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
