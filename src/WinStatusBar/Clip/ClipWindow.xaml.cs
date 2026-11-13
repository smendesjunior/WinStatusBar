#nullable disable
using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace WinStatusBar.Clip;

public sealed partial class ClipWindow : Window, IClipActions
{
    const int WidthDip = 480, HeightDip = 600; // mesma largura dos painéis da barra

    readonly Store store;
    readonly ClipboardService clip;
    readonly IntPtr hwnd;
    readonly AppWindow appWin;
    readonly Native.SUBCLASSPROC subclass;
    readonly DispatcherQueueTimer clipDebounce, searchDebounce, emojiDebounce;
    readonly Dictionary<string, ClipVM> vms = new();
    bool panelVisible, suppressHide;
    IntPtr prevHwnd;
    string tab = "clip";
    List<EmojiGroup> emojiGroups;

    public ClipWindow(Store s, ClipboardService c)
    {
        InitializeComponent();
        Title = global::WinStatusBar.Services.Localization.T("Windows Clipmanager");
        store = s; clip = c;
        hwnd = WindowNative.GetWindowHandle(this);
        appWin = AppWindow;

        // Janela tipo "painel": sem barra de titulo, com borda e sombra do Windows, sempre por cima,
        // fora da barra de tarefas e do Alt+Tab, fundo acrilico (como o Win+V nativo).
        var p = OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(true, false);
        // Sem "sempre por cima": no WinUI 3 as dicas (tooltips) sao janelas proprias e ficariam atras do painel.
        // O painel ja some ao perder o foco, como o nativo.
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        appWin.SetPresenter(p);
        appWin.IsShownInSwitchers = false;
        appWin.Title = global::WinStatusBar.Services.Localization.T("Área de transferência · WinStatusBar");
        if (File.Exists(ClipHost.IconPath)) appWin.SetIcon(ClipHost.IconPath);
        SystemBackdrop = new DesktopAcrylicBackdrop();
        int round = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        appWin.Closing += (_, e) => { e.Cancel = true; HidePanel(); };
        Activated += OnActivated;

        // Monitor da area de transferencia (WM_CLIPBOARDUPDATE)
        clipDebounce = DispatcherQueue.CreateTimer();
        clipDebounce.Interval = TimeSpan.FromMilliseconds(150); clipDebounce.IsRepeating = false;
        clipDebounce.Tick += async (_, _) => { try { await clip.CaptureAsync(); } catch (Exception ex) { Log.Write(global::WinStatusBar.Services.Localization.T("Captura: ") + ex); } };
        subclass = SubclassProc;
        Native.SetWindowSubclass(hwnd, subclass, (UIntPtr)1, UIntPtr.Zero);
        if (!Native.AddClipboardFormatListener(hwnd)) Log.Write("AddClipboardFormatListener falhou");
        clip.Changed += () => { if (panelVisible && tab == "clip") RefreshClip(false); else clipDirty = true; };

        searchDebounce = DispatcherQueue.CreateTimer();
        searchDebounce.Interval = TimeSpan.FromMilliseconds(140); searchDebounce.IsRepeating = false;
        searchDebounce.Tick += (_, _) => RefreshClip(true);
        emojiDebounce = DispatcherQueue.CreateTimer();
        emojiDebounce.Interval = TimeSpan.FromMilliseconds(140); emojiDebounce.IsRepeating = false;
        emojiDebounce.Tick += (_, _) => RefreshEmoji();

        ClipVM.Actions = this;
        // Borda de foco arredondada: o card mostra a propria borda quando o item da lista recebe o foco
        ClipList.GotFocus += (_, e) => { if (e.OriginalSource is ListViewItem { Content: ClipVM vm }) vm.Focused = true; };
        ClipList.LostFocus += (_, e) => { if (e.OriginalSource is ListViewItem { Content: ClipVM vm }) vm.Focused = false; };
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        Root.CharacterReceived += Root_CharacterReceived;
        BuildEmojiChrome();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (emojiDirty) RefreshEmoji(); });
        // Folha de emojis pré-desenhados (instantâneos ao rolar): gera em segundo plano já na abertura da barra, na
        // escala do monitor; quando fica pronta, os itens trocam o texto pelo recorte da folha.
        EmojiAtlas.Changed += () => { foreach (var e in EmojiCatalog.All) e.RaiseDisplay(); };
        EnsureEmojiAtlas();
        Root.Loaded += (_, _) => { if (Root.XamlRoot is { } root) root.Changed += (_, _) => EnsureEmojiAtlas(); };
    }

    /// <summary>Escala da tela onde o painel está (refaz a folha se mudar para um monitor com outra escala).</summary>
    void EnsureEmojiAtlas()
    {
        double scale = Root.XamlRoot?.RasterizationScale is > 0 and var s ? s : Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        EmojiAtlas.Ensure(EmojiCatalog.AllGlyphs(), scale, DispatcherQueue);
    }

    IntPtr SubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (msg == 0x031D) { clipDebounce.Stop(); clipDebounce.Start(); }
        return Native.DefSubclassProc(h, msg, w, l);
    }

    // ================================================================== mostrar / esconder
    public string TogglePanel(string which, PointInt32? anchor = null)
    {
        // Fecha só se o painel está de fato à frente. Se ele abriu sem receber o foco (ficou atrás de outra janela),
        // o próximo Win+V o traz para a frente em vez de "fechar" um painel que a pessoa nem via.
        // Por cima de tudo (ver EnsureShown) ele está visível mesmo sem foco: aí o atalho fecha.
        if (panelVisible && which == tab && (Native.GetForegroundWindow() == hwnd || topmostFallback)) { HidePanel(); return "fechou"; }
        bool was = panelVisible;
        ShowPanel(which, anchor);
        return was ? "trouxe para a frente" : "abriu";
    }

    /// <summary>
    /// Confere, logo depois de abrir, se o painel recebeu o foco. O Windows às vezes recusa a troca (janela de
    /// administrador ou do sistema à frente): tenta de novo e, se ainda assim falhar, deixa o painel por cima de tudo
    /// até ser fechado, em vez de aberto atrás da janela atual, invisível.
    /// </summary>
    async void EnsureShown()
    {
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(50);
            if (!panelVisible || Native.GetForegroundWindow() == hwnd) return;
            if (i == 1)
            {
                Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero); Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                Native.ForceForeground(hwnd);
            }
        }
        if (!panelVisible || Native.GetForegroundWindow() == hwnd) return;
        Log.Write($"Painel aberto sem foco (em primeiro plano: {Native.Describe(Native.GetForegroundWindow())}); fica por cima até fechar.");
        if (appWin.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = true;
        topmostFallback = true;
    }

    bool topmostFallback;

    /// <summary>Abre o painel. Pelo atalho: junto ao cursor de texto; pelo botão da barra: logo abaixo dele, alinhado à direita.</summary>
    public void ShowPanel(string which, PointInt32? anchor = null)
    {
        Root.RequestedTheme = WinStatusBar.App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light; // mesmo tema da barra
        if (panelVisible) { SelectTab(which); Native.ForceForeground(hwnd); EnsureShown(); return; }
        IntPtr fg = Native.GetForegroundWindow();
        prevHwnd = fg == hwnd ? IntPtr.Zero : fg;
        // So os cards de arquivos sao refeitos (conferem se os arquivos ainda existem); os demais mantem as miniaturas
        foreach (var id in vms.Where(kv => kv.Value.Item.Kind == "files").Select(kv => kv.Key).ToList()) vms.Remove(id);
        clipDirty = true;
        if (ClipSearch.Text.Length > 0 || clipFilter != "all") { ClipSearch.Text = ""; clipFilter = "all"; }
        if (EmojiSearch.Text.Length > 0) { EmojiSearch.Text = ""; emojiDirty = true; }
        // Preserve the prepared category and item containers between openings.
        SelectTab(which);
        Place(fg, anchor);
        panelVisible = true;
        appWin.Show(true);
        Activate();
        Native.ForceForeground(hwnd);
        EnsureShown();
        FocusFirst();
        // Prepara a outra aba em segundo plano, para a troca de aba ser instantanea
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (tab == "clip" && emojiDirty) RefreshEmoji();
            else if (tab == "emoji" && clipDirty) RefreshClip(true);
        });
    }

    public void HidePanel()
    {
        panelVisible = false;
        if (topmostFallback && appWin.Presenter is OverlappedPresenter p) { p.IsAlwaysOnTop = false; topmostFallback = false; }
        appWin.Hide();
    }

    void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated && panelVisible && !suppressHide)
        {
            Log.Write($"Painel fechado ao perder o foco (em primeiro plano: {Native.Describe(Native.GetForegroundWindow())}).");
            HidePanel();
        }
    }

    void Place(IntPtr fg, PointInt32? anchor)
    {
        var pt = anchor is { } a ? new Native.POINT { X = a.X, Y = a.Y } : Native.CaretOrCursor(fg);
        IntPtr mon = Native.MonitorFromPoint(pt, 2);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        Native.GetDpiForMonitor(mon, 0, out uint dpi, out _);
        double sc = (dpi == 0 ? 96 : dpi) / 96.0;
        int w = (int)(WidthDip * sc), h = (int)(HeightDip * sc), m = (int)(8 * sc);
        var wa = mi.rcWork;
        int x = anchor is null ? pt.X : pt.X - w; // sob o botão: borda direita alinhada a ele, como os painéis
        x = Math.Max(wa.Left + m, Math.Min(x, wa.Right - w - m));
        int y = pt.Y;
        if (y + h > wa.Bottom - m) y = Math.Max(wa.Top + m, pt.Y - h - (int)(30 * sc));
        appWin.MoveAndResize(new RectInt32(x, y, w, h));
    }

    void FocusFirst()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (tab == "emoji") { EmojiSearch.Focus(FocusState.Programmatic); return; }
            var list = ClipList.ItemsSource as List<object>;
            int idx = list?.FindIndex(o => o is ClipVM) ?? -1;
            if (idx < 0) { ClipSearch.Focus(FocusState.Programmatic); return; }
            ClipList.ScrollIntoView(list[0]);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (ClipList.ContainerFromIndex(idx) is ListViewItem li) li.Focus(FocusState.Keyboard);
                else ClipSearch.Focus(FocusState.Programmatic);
            });
        });
    }

    bool syncingBars; // evita reentrada quando o codigo muda a selecao dos SelectorBar

    void SelectTab(string which)
    {
        tab = which;
        bool emoji = which == "emoji";
        EmojiView.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        ClipView.Visibility = emoji ? Visibility.Collapsed : Visibility.Visible;
        syncingBars = true;
        TabBar.SelectedItem = emoji ? TabEmoji : TabClip;
        syncingBars = false;
        // Trocar de aba so mostra o que ja esta montado; reconstroi apenas se algo mudou
        if (emoji) { if (emojiDirty) RefreshEmoji(); }
        else if (clipDirty) RefreshClip(true);
    }

    bool clipDirty = true, emojiDirty = true;

    /// <summary>O histórico mudou fora do painel (limites aplicados, limpeza pelas configurações): refaz a lista.</summary>
    public void Invalidate()
    {
        foreach (var id in vms.Keys.Where(k => !store.Items.Any(i => i.Id == k)).ToList()) vms.Remove(id);
        clipDirty = true;
        if (panelVisible && tab == "clip") RefreshClip(false);
    }

    void Close_Click(object sender, RoutedEventArgs e) => HidePanel();

    void TabBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (syncingBars) return;
        string which = sender.SelectedItem == TabEmoji ? "emoji" : "clip";
        if (which != tab) { SelectTab(which); FocusFirst(); }
    }

    // ================================================================== teclado
    void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape) { HidePanel(); e.Handled = true; return; }
        if (tab != "clip") return;
        var focusedVm = (FocusManager.GetFocusedElement(Content.XamlRoot) as ListViewItem)?.Content as ClipVM;
        if (ctrl && e.Key == VirtualKey.P && focusedVm != null) { TogglePin(focusedVm); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete && focusedVm != null) { DeleteItem(focusedVm.Item); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.F) { ClipSearch.Focus(FocusState.Programmatic); e.Handled = true; }
    }

    // Digitar com o foco na lista leva o texto para a pesquisa
    void Root_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (char.IsControl(e.Character)) return;
        var f = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (f is TextBox) return;
        var box = tab == "emoji" ? EmojiSearch : ClipSearch;
        box.Text += e.Character;
        box.Focus(FocusState.Programmatic);
        if (FindChild<TextBox>(box) is TextBox tb) tb.SelectionStart = tb.Text.Length;
        e.Handled = true;
    }

    void ClipSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down) { FocusFirstCard(); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter)
        {
            searchDebounce.Stop(); RefreshClip(true);
            var first = (ClipList.ItemsSource as List<object>)?.OfType<ClipVM>().FirstOrDefault();
            if (first != null) _ = Choose(first.Item, IsShift(), true);
            e.Handled = true;
        }
    }

    void FocusFirstCard()
    {
        var list = ClipList.ItemsSource as List<object>;
        int idx = list?.FindIndex(o => o is ClipVM) ?? -1;
        if (idx >= 0 && ClipList.ContainerFromIndex(idx) is ListViewItem li) li.Focus(FocusState.Keyboard);
    }

    static bool IsShift() => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    static T FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    // ================================================================== historico
    void ClipSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return; // limpezas feitas pelo codigo nao recarregam
        searchDebounce.Stop(); searchDebounce.Start();
    }

    string clipFilter = "all";

    /// <summary>Barra de icones das categorias presentes no historico (como a barra de categorias dos emojis).</summary>
    void BuildClipCategoryBar()
    {
        var counts = new Dictionary<string, int>();
        foreach (var it in store.Items)
        {
            string k = Category.Of(it).Key;
            counts[k] = counts.GetValueOrDefault(k) + 1;
            if (it.Pinned) counts["pinned"] = counts.GetValueOrDefault("pinned") + 1;
        }
        counts["all"] = store.Items.Count;
        if (clipFilter != "all" && counts.GetValueOrDefault(clipFilter) == 0) clipFilter = "all";

        var items = Category.Filters
            .Where(f => f.Key == "all" || counts.GetValueOrDefault(f.Key) > 0)
            .Select(f => (f.Key, f.Glyph, f.Font, $"{f.Name} ({counts.GetValueOrDefault(f.Key):N0})"));
        BuildIconBar(ClipCategoryBar, items, clipFilter, key => { clipFilter = key; RefreshClip(true); });
    }

    /// <summary>
    /// Barra de icones (filtros e categorias de emoji): botoes do WinUI distribuidos na largura toda,
    /// icones monocromaticos, realce arredondado ao passar o mouse e pilula de destaque no selecionado.
    /// </summary>
    void BuildIconBar(Grid bar, IEnumerable<(string Key, string Glyph, string Font, string Tip)> items, string selected, Action<string> onSelect)
    {
        bar.Children.Clear();
        bar.ColumnDefinitions.Clear();
        var accent = new SolidColorBrush(AccentColor());
        int col = 0;
        foreach (var (key, glyph, font, tip) in items)
        {
            bool sel = key == selected;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = glyph, FontSize = 16, Opacity = sel ? 1 : 0.72 };
            if (font != null) icon.FontFamily = new FontFamily(font);
            var pill = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 16, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = accent,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2),
                Visibility = sel ? Visibility.Visible : Visibility.Collapsed,
            };
            var content = new Grid { Height = 36 };
            content.Children.Add(icon);
            content.Children.Add(pill);
            var b = new Button
            {
                Content = content, Tag = key, Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                CornerRadius = new CornerRadius(4),
            };
            ToolTipService.SetToolTip(b, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (s, _) => onSelect((string)((Button)s).Tag);
            Grid.SetColumn(b, col++);
            bar.Children.Add(b);
        }
    }

    /// <summary>Cor de destaque do Windows na variacao que o WinUI usa para o tema atual.</summary>
    Windows.UI.Color AccentColor()
    {
        var ui = new Windows.UI.ViewManagement.UISettings();
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        return ui.GetColorValue(dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1);
    }

    void RefreshClip(bool resetScroll)
    {
        clipDirty = false;
        BuildClipCategoryBar();
        string q = ClipSearch.Text.Trim();
        var items = store.Items.Where(i => Category.InFilter(i, clipFilter) && Match(i, q)).ToList();
        var list = new List<object>(items.Count + 2);
        bool anyPinned = items.Any(i => i.Pinned);
        bool? last = null;
        foreach (var it in items)
        {
            if (anyPinned && last != it.Pinned)
            {
                list.Add(it.Pinned ? new SectionHeader(global::WinStatusBar.Services.Localization.T("Fixados"), "") : new SectionHeader(global::WinStatusBar.Services.Localization.T("Recentes"), ""));
                last = it.Pinned;
            }
            if (!vms.TryGetValue(it.Id, out var vm)) vms[it.Id] = vm = new ClipVM(it);
            list.Add(vm);
        }
        ClipList.ItemsSource = list;
        bool empty = items.Count == 0;
        ClipEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClipEmptyTitle.Text = store.Items.Count == 0 ? global::WinStatusBar.Services.Localization.T("Seu histórico está vazio") : global::WinStatusBar.Services.Localization.T("Nenhum resultado");
        ClipEmptyText.Text = store.Items.Count == 0 ? global::WinStatusBar.Services.Localization.T("Copie algo e ele aparecerá aqui — salvo para sempre.") : global::WinStatusBar.Services.Localization.T("Tente outra pesquisa ou categoria.");
    }

    static bool Match(ClipItem it, string q)
    {
        if (q.Length == 0) return true;
        foreach (var term in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tl = EmojiCatalog.Normalize(term);
            bool kind = (tl == "imagem" && it.Kind == "image") || (tl == "link" && it.Kind == "link") ||
                        (tl == "arquivo" && it.Kind == "files") || (tl.StartsWith("fixad") && it.Pinned);
            bool cat = tl.Length >= 3 && EmojiCatalog.Normalize(Category.Of(it).Name).StartsWith(tl);
            bool hit = kind || cat
                || (it.Text?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (it.Source?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) return false;
        }
        return true;
    }

    void ClipList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipVM vm) _ = Choose(vm.Item, IsShift(), true);
    }

    async Task Choose(ClipItem it, bool plain, bool paste)
    {
        try
        {
            if (!await clip.PutAsync(it, plain))
            {
                await Info(global::WinStatusBar.Services.Localization.T("Arquivos não encontrados"), global::WinStatusBar.Services.Localization.T("Os arquivos deste item não existem mais e não há cópia salva."));
                return;
            }
        }
        catch (Exception ex) { Log.Write(global::WinStatusBar.Services.Localization.T("Colar: ") + ex); return; }
        clip.Bump(it);
        IntPtr target = prevHwnd;
        if (!paste || !ClipHost.Options.ClipboardAutoPaste || target == IntPtr.Zero || !Native.IsWindow(target) || Native.IsShellWindow(target))
        {
            HidePanel();
            return;
        }
        // Devolve o foco ANTES de esconder o painel: enquanto o painel está em primeiro plano o Windows deixa a barra
        // escolher a próxima janela. Escondendo antes, o Windows ativava outra janela (às vezes a errada), recusava a
        // troca e o Ctrl+V caía no lugar errado ou em lugar nenhum.
        Native.ForceForeground(target);
        HidePanel();
        for (int i = 0; i < 20 && !Native.IsForeground(target); i++)
        {
            await Task.Delay(25);
            if (i == 4) Native.ForceForeground(target);
        }
        if (!Native.IsForeground(target))
        {
            Log.Write($"Colar: o app de destino não voltou ao primeiro plano ({Native.Describe(target)}; em primeiro plano: {Native.Describe(Native.GetForegroundWindow())}). O item ficou na área de transferência.");
            return;
        }
        if (Native.IsElevatedAboveUs(target))
        {
            Log.Write($"Colar: {Native.Describe(target)} roda como administrador; o Windows bloqueia o Ctrl+V automático. O item ficou na área de transferência.");
            return;
        }
        await Task.Delay(60); // o app de destino termina de receber o foco antes das teclas
        if (Native.SendCtrlV() == 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Write($"Colar: o Windows recusou o Ctrl+V para {Native.Describe(target)} (erro {error}).");
        }
    }

    public void TogglePin(ClipVM vm)
    {
        vm.Item.Pinned = !vm.Item.Pinned;
        store.Save(vm.Item);
        store.Sort();
        vm.RaisePin();
        RefreshClip(false);
    }

    void DeleteItem(ClipItem it)
    {
        store.Delete(it);
        vms.Remove(it.Id);
        RefreshClip(false);
        FocusFirstCard();
    }

    async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        int n = store.Items.Count(i => !i.Pinned);
        if (n == 0) return;
        var dlg = new ContentDialog
        {
            Title = global::WinStatusBar.Services.Localization.T("Limpar tudo"),
            Content = global::WinStatusBar.Services.Localization.F("Excluir {0:N0} itens? Os itens fixados serão mantidos.", new object[] {n}),
            PrimaryButtonText = global::WinStatusBar.Services.Localization.T("Excluir"),
            CloseButtonText = global::WinStatusBar.Services.Localization.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        suppressHide = true;
        try
        {
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            foreach (var it in store.Items.Where(i => !i.Pinned).ToList()) store.Delete(it);
            vms.Clear();
            RefreshClip(true);
        }
        finally { suppressHide = false; }
    }

    async Task Info(string title, string text)
    {
        suppressHide = true;
        try { await new ContentDialog { Title = title, Content = text, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync(); }
        finally { suppressHide = false; }
    }

    static string RelTime(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return global::WinStatusBar.Services.Localization.T("agora");
        if (d.TotalMinutes < 60) return global::WinStatusBar.Services.Localization.F("há {0} min", new object[] {(int)d.TotalMinutes});
        if (t.Date == DateTime.Today) return "hoje às " + t.ToString("HH:mm");
        if (t.Date == DateTime.Today.AddDays(-1)) return "ontem às " + t.ToString("HH:mm");
        return t.ToString("dd/MM/yyyy HH:mm");
    }

    static MenuFlyoutItem Item(string text, string glyph, Action a)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += (_, _) => a();
        return mi;
    }

    public void ShowItemMenu(ClipVM vm, FrameworkElement anchor)
    {
        var it = vm.Item;
        var mf = new MenuFlyout();
        mf.Items.Add(new MenuFlyoutItem
        {
            Text = $"{vm.Cat.Name} · {(it.Source != null ? it.Source + " · " : "")}{RelTime(it.LastUsed)}",
            Icon = new FontIcon { Glyph = vm.Cat.Glyph, Foreground = vm.CategoryBrush, FontFamily = vm.CategoryFont },
            IsEnabled = false,
        });
        mf.Items.Add(new MenuFlyoutSeparator());
        mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Colar"), "", () => _ = Choose(it, false, true)));
        if (it.Kind != "image") mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Colar como texto sem formatação"), "", () => _ = Choose(it, true, true)));
        mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Copiar"), "", () => _ = Choose(it, false, false)));
        mf.Items.Add(new MenuFlyoutSeparator());
        mf.Items.Add(Item(it.Pinned ? global::WinStatusBar.Services.Localization.T("Desafixar") : global::WinStatusBar.Services.Localization.T("Fixar"), it.Pinned ? "" : "", () => TogglePin(vm)));
        if (it.Kind == "link")
            mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Abrir link"), "", () => { var u = it.Text.Trim(); if (u.StartsWith("www.")) u = "https://" + u; Open(u); }));
        if (it.Kind == "image")
        {
            mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Abrir imagem"), "", () => Open(store.ImagePath(it))));
            mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Salvar imagem como..."), "", () => _ = SaveImageAs(it)));
        }
        if (it.Kind == "files" && Store.ResolveFile(it, 0) is string p0)
        {
            mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Abrir"), "", () => Open(p0)));
            mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Mostrar na pasta"), "", () => { Process.Start("explorer.exe", "/select,\"" + p0 + "\""); HidePanel(); }));
        }
        mf.Items.Add(new MenuFlyoutSeparator());
        mf.Items.Add(Item(global::WinStatusBar.Services.Localization.T("Excluir"), "", () => DeleteItem(it)));
        mf.ShowAt(anchor);
    }

    void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); HidePanel(); }
        catch (Exception ex) { Log.Write("Abrir: " + ex.Message); }
    }

    async Task SaveImageAs(ClipItem it)
    {
        var picker = new FileSavePicker { SuggestedFileName = "imagem_" + it.Created.ToString("yyyyMMdd_HHmmss") };
        picker.FileTypeChoices.Add(global::WinStatusBar.Services.Localization.T("Imagem PNG"), new List<string> { ".png" });
        InitializeWithWindow.Initialize(picker, hwnd);
        suppressHide = true;
        try
        {
            var f = await picker.PickSaveFileAsync();
            if (f != null) File.Copy(store.ImagePath(it), f.Path, true);
        }
        catch (Exception ex) { Log.Write(global::WinStatusBar.Services.Localization.T("Salvar imagem: ") + ex.Message); }
        finally { suppressHide = false; }
    }

    // ================================================================== emojis
    void BuildEmojiChrome()
    {
        // Seletor de tom de pele
        string[] hands = { "✋", "✋🏻", "✋🏼", "✋🏽", "✋🏾", "✋🏿" };
        string[] names = { global::WinStatusBar.Services.Localization.T("Padrão"), global::WinStatusBar.Services.Localization.T("Pele clara"), global::WinStatusBar.Services.Localization.T("Pele morena clara"), global::WinStatusBar.Services.Localization.T("Pele morena"), global::WinStatusBar.Services.Localization.T("Pele morena escura"), global::WinStatusBar.Services.Localization.T("Pele escura") };
        for (int i = 0; i < hands.Length; i++)
        {
            int tone = i;
            var b = new Button
            {
                Content = new TextBlock { Text = hands[i], FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 18 },
                Width = 40, Height = 40, Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(b, names[i]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, names[i]);
            b.Click += (_, _) => { EmojiCatalog.SetTone(tone); ToneButtonText.Text = hands[tone]; ToneFlyout.Hide(); };
            TonePanel.Children.Add(b);
        }
        ToneButtonText.Text = hands[Math.Clamp(EmojiCatalog.Tone, 0, 5)];

        BuildEmojiCategoryBar("Smileys & Emotion");
    }

    string emojiCategory;
    void BuildEmojiCategoryBar(string selected)
    {
        emojiCategory = selected;
        BuildIconBar(EmojiCategoryBar, EmojiCatalog.GroupInfo.Select(g => (g.Id, g.Icon, (string)null, g.Title)), selected,
            id => { BuildEmojiCategoryBar(id); JumpToGroup(id); });
    }

    void EmojiSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        emojiDebounce.Stop(); emojiDebounce.Start();
    }

    void RefreshEmoji()
    {
        emojiDirty = false;
        string q = EmojiSearch.Text.Trim();
        if (q.Length > 0) emojiDirty = true; // ao limpar a pesquisa, volta a lista completa
        emojiGroups = q.Length == 0 ? EmojiCatalog.BuildGroups() : EmojiCatalog.Search(q);
        EmojiGrid.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = emojiGroups }.View;
        EmojiCategoryBar.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length == 0) BuildEmojiCategoryBar(emojiCategory ?? emojiGroups.FirstOrDefault()?.Id ?? "Smileys & Emotion");
    }

    void JumpToGroup(string id)
    {
        if (EmojiSearch.Text.Length > 0) { EmojiSearch.Text = ""; RefreshEmoji(); }
        var g = emojiGroups?.FirstOrDefault(x => x.Id == id);
        if (g != null && g.Count > 0) EmojiGrid.ScrollIntoView(g[0], ScrollIntoViewAlignment.Leading);
    }

    async void EmojiGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EmojiVM vm) return;
        string text = vm.Display;
        EmojiCatalog.AddRecent(vm.Base);
        emojiDirty = true; // "Usados recentemente" e atualizado na proxima abertura (como no nativo)
        if (prevHwnd == IntPtr.Zero || Native.IsShellWindow(prevHwnd))
        {
            // Sem um app de destino: copia o emoji
            await clip.PutTextAsync(text);
            return;
        }
        // Digita o emoji no app de origem e mantem o painel aberto (como o nativo)
        suppressHide = true;
        try
        {
            Native.SetForegroundWindow(prevHwnd);
            await Task.Delay(50);
            Native.SendText(text);
            await Task.Delay(60);
            Native.ForceForeground(hwnd);
        }
        finally { suppressHide = false; }
    }
}
