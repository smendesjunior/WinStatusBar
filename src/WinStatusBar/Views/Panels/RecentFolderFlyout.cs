using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;
namespace WinStatusBar.Views.Panels;
/// <summary>
/// Popup de uma pasta (Downloads, recentes…): os arquivos mais recentes primeiro, com miniaturas, pesquisa, arrastar
/// para dentro e para fora, botão direito como no Explorador (vale para a seleção inteira) e atalhos de teclado
/// (Enter, F2, Del, Ctrl+C/X/V/A). Pastas grandes mostram só os mais recentes; a pesquisa procura em todos.
/// </summary>
internal static class RecentFolderFlyout
{
    private const int VisibleLimit = 120;

    public static FolderPopupWindow Create(string title, Func<string> resolvePath)
    {
        var body = new StackPanel { Spacing = 12, AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var grid = new GridView
        {
            AllowDrop = true, CanDragItems = true, CanReorderItems = false,
            SelectionMode = ListViewSelectionMode.Extended, IsItemClickEnabled = true,
            Padding = new Thickness(0),
            ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='4' ItemWidth='116' ItemHeight='120'/></ItemsPanelTemplate>")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Visible);
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(grid, ScrollMode.Disabled);
        var status = PanelKit.Caption("", 12);
        string? path = null;
        CancellationTokenSource? loading = null;
        CancellationToken current = CancellationToken.None;
        FolderPopupWindow? flyout = null;
        var open = SettingsDetailsPanel.ActionButton("", "Abrir pasta", () => { flyout?.Hide(); if (path is not null) WindowsLauncherService.OpenFolder(path); });
        body.Children.Add(PanelKit.Header(title, PanelKit.Caption("Todos os arquivos · mais recentes primeiro"), open));
        var search = new TextBox { PlaceholderText = "Pesquisar pelo nome do arquivo" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Pesquisar arquivos nesta pasta");
        body.Children.Add(search);
        var selectionCount = PanelKit.Caption("", 11);
        var actions = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < 5; i++) actions.ColumnDefinitions.Add(new ColumnDefinition());
        var select = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = "Selecionar", Padding = new Thickness(10, 5, 10, 5) };
        ToolTipService.SetToolTip(select, "Marcar vários itens com toque ou clique (Ctrl e Shift também selecionam)");
        var copy = SettingsDetailsPanel.ActionButton("", "Copiar", () => _ = Guard(() => CopyItemsAsync(Selected(), false)));
        var cut = SettingsDetailsPanel.ActionButton("", "Recortar", () => _ = Guard(() => CopyItemsAsync(Selected(), true)));
        var all = SettingsDetailsPanel.ActionButton("", "Todos", () => { if (select.IsChecked != true) select.IsChecked = true; grid.SelectAll(); });
        var close = SettingsDetailsPanel.ActionButton("", "Fechar", () => flyout?.Hide());
        var actionButtons = new Control[] { select, copy, cut, all, close };
        for (int i = 0; i < actionButtons.Length; i++)
        {
            var button = actionButtons[i]; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Padding = new Thickness(5);
            Grid.SetColumn(button, i); actions.Children.Add(button);
        }
        body.Children.Add(actions);
        body.Children.Add(selectionCount);
        body.Children.Add(status);

        RecentFolderItem[] Selected() => grid.SelectedItems.Select(ResolveItem).OfType<RecentFolderItem>().ToArray();
        void ShowStatus(string text) { status.Text = text; status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        async Task Guard(Func<Task> action) { try { await action(); } catch (Exception ex) { ShowStatus(ex.Message); } }
        void SelectionChanged()
        {
            bool selecting = select.IsChecked == true;
            grid.SelectionMode = selecting ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
            grid.IsItemClickEnabled = !selecting; // no modo Selecionar, o clique marca o item em vez de abrir
            int count = grid.SelectedItems.Count;
            copy.IsEnabled = cut.IsEnabled = count > 0;
            selectionCount.Text = count > 1 || selecting ? count + " selecionado(s)" : "";
            selectionCount.Visibility = selectionCount.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        select.Checked += (_, _) => SelectionChanged();
        select.Unchecked += (_, _) => { grid.SelectedItems.Clear(); SelectionChanged(); };
        grid.SelectionChanged += (_, _) => SelectionChanged();

        // Dados da pasta e blocos criados só quando aparecem (no máximo VisibleLimit por vez).
        var items = new List<RecentFolderItem>();
        var tiles = new Dictionary<RecentFolderItem, GridViewItem>(ReferenceEqualityComparer.Instance);
        void Filter()
        {
            grid.Items.Clear();
            string query = search.Text.Trim();
            var matches = query.Length == 0 ? items : items.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            foreach (var item in matches.Take(VisibleLimit)) grid.Items.Add(Tile(item));
            ShowStatus(matches.Count == 0 ? (items.Count == 0 ? "Esta pasta está vazia." : "Nenhum arquivo encontrado.")
                : matches.Count > VisibleLimit ? $"Mostrando os {VisibleLimit} mais recentes de {matches.Count} · pesquise pelo nome para achar os demais" : "");
            SelectionChanged();
        }
        search.TextChanged += (_, _) => Filter();
        var content = new Grid { RowSpacing = 12, AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(body); Grid.SetRow(grid, 1); content.Children.Add(grid);
        grid.SizeChanged += (_, _) =>
        {
            if (grid.ItemsPanelRoot is ItemsWrapGrid wrap) wrap.ItemWidth = Math.Max(60, (grid.ActualWidth - 16) / 4);
        };
        flyout = new FolderPopupWindow(title, content, () => _ = LoadAsync(), () => loading?.Cancel());
        bool dragging = false;

        static bool Pressed(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        grid.ItemClick += (_, e) =>
        {
            // Ctrl/Shift + clique só seleciona (como no Explorador); clique simples abre.
            if (dragging || select.IsChecked == true || Pressed(VirtualKey.Control) || Pressed(VirtualKey.Shift)) return;
            if (ResolveItem(e.ClickedItem) is RecentFolderItem item) { flyout.Hide(); WindowsLauncherService.Shell(item.Path); }
        };
        grid.DragItemsStarting += (_, e) =>
        {
            var dragged = e.Items.Select(ResolveItem).OfType<RecentFolderItem>().ToArray();
            if (dragged.Length == 0) { e.Cancel = true; return; }
            dragging = true;
            flyout.IsDragging = true;
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var files = new List<IStorageItem>();
                    foreach (var item in dragged) files.Add(await StorageItemAsync(item));
                    request.SetData(files);
                }
                catch (Exception ex) { App.Log("Pasta: arrastar falhou: " + ex.Message); }
                finally { deferral.Complete(); }
            });
        };
        grid.DragItemsCompleted += (_, e) =>
        {
            dragging = false;
            flyout.IsDragging = false;
            if (e.DropResult != DataPackageOperation.None) flyout.Hide();
        };
        content.DragOver += (_, e) =>
        {
            if (path is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.None; return; }
            e.AcceptedOperation = DropOperation(e);
            e.DragUIOverride.Caption = (e.AcceptedOperation == DataPackageOperation.Move ? "Mover para " : "Copiar para ") + title;
            e.Handled = true;
        };
        async Task DropIntoAsync(DragEventArgs e, string? destination)
        {
            e.Handled = true;
            e.AcceptedOperation = DropOperation(e);
            var deferral = e.GetDeferral();
            try
            {
                if (destination is null || e.AcceptedOperation == DataPackageOperation.None || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var dropped = await e.DataView.GetStorageItemsAsync();
                bool success = await FolderShellOperations.TransferAsync(dropped.Select(x => x.Path), destination, e.AcceptedOperation == DataPackageOperation.Move);
                if (success) { e.DataView.ReportOperationCompleted(e.AcceptedOperation); await LoadAsync(); }
                else e.AcceptedOperation = DataPackageOperation.None;
            }
            catch (Exception ex) { ShowStatus(ex.Message); e.AcceptedOperation = DataPackageOperation.None; }
            finally { deferral.Complete(); }
        }
        content.Drop += async (_, e) => await DropIntoAsync(e, path);

        async Task PasteAsync()
        {
            var data = Clipboard.GetContent();
            if (path is null || !data.Contains(StandardDataFormats.StorageItems)) return;
            var pasted = await data.GetStorageItemsAsync();
            bool move = data.RequestedOperation == DataPackageOperation.Move;
            if (await FolderShellOperations.TransferAsync(pasted.Select(x => x.Path), path, move))
            {
                data.ReportOperationCompleted(move ? DataPackageOperation.Move : DataPackageOperation.Copy);
                await LoadAsync();
            }
        }
        async Task NewFolderAsync()
        {
            if (path is null) return;
            string name = "Nova pasta";
            for (int n = 2; Directory.Exists(Path.Combine(path, name)) || File.Exists(Path.Combine(path, name)); n++) name = $"Nova pasta ({n})";
            Directory.CreateDirectory(Path.Combine(path, name));
            search.Text = string.Empty;
            await LoadAsync();
        }
        async Task RenameAsync(RecentFolderItem item, XamlRoot root)
        {
            var name = new TextBox { Text = item.Name };
            // Seleciona só o nome, sem a extensão (como o F2 do Explorador).
            name.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.Select(0, item.IsDirectory ? item.Name.Length : Path.GetFileNameWithoutExtension(item.Name).Length); };
            // No lugar da pesquisa, dentro da própria janela (um diálogo dela ficaria invisível e travaria os cliques).
            if (!await InlinePrompt.AskAsync(search, "Renomear", item.Name, "Salvar", input: name, maxWidth: double.PositiveInfinity) || name.Text == item.Name) return;
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text is "." or ".." || name.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("Nome de arquivo inválido.");
            await FolderShellOperations.RenameAsync(item.Path, Path.Combine(Path.GetDirectoryName(item.Path)!, name.Text));
            await LoadAsync();
        }
        async Task DeleteAsync(RecentFolderItem[] targets)
        {
            if (targets.Length == 0) return;
            // Vai para a Lixeira; o próprio Windows pede a confirmação.
            if (await FolderShellOperations.DeleteAsync(targets.Select(t => t.Path))) await LoadAsync();
        }
        static void CopyPaths(RecentFolderItem[] targets)
        {
            var data = new DataPackage();
            data.SetText(string.Join(Environment.NewLine, targets.Select(t => t.Path)));
            Clipboard.SetContent(data); Clipboard.Flush();
        }

        // Fundo da pasta: colar, nova pasta, atualizar e abrir no Explorador.
        var background = new MenuFlyout();
        void AddTo(MenuFlyout menu, string label, Symbol icon, Func<Task> action, string? hint = null)
        {
            var command = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(icon) };
            if (hint is not null) command.KeyboardAcceleratorTextOverride = hint;
            command.Click += async (_, _) => await Guard(action);
            menu.Items.Add(command);
        }
        background.Opening += (_, _) =>
        {
            background.Items.Clear();
            var paste = new MenuFlyoutItem { Text = "Colar nesta pasta", Icon = new SymbolIcon(Symbol.Paste), KeyboardAcceleratorTextOverride = "Ctrl+V" };
            try { paste.IsEnabled = Clipboard.GetContent().Contains(StandardDataFormats.StorageItems); } catch { }
            paste.Click += async (_, _) => await Guard(PasteAsync);
            background.Items.Add(paste);
            AddTo(background, "Nova pasta", Symbol.NewFolder, NewFolderAsync);
            background.Items.Add(new MenuFlyoutSeparator());
            AddTo(background, "Atualizar", Symbol.Refresh, LoadAsync, "F5");
            AddTo(background, "Abrir no Explorador", Symbol.Folder, () => { flyout?.Hide(); if (path is not null) WindowsLauncherService.OpenFolder(path); return Task.CompletedTask; });
        };
        content.ContextFlyout = background;

        // Item: o menu é montado ao abrir e age sobre a seleção inteira quando o item faz parte dela.
        MenuFlyout ItemMenu(RecentFolderItem item, GridViewItem button)
        {
            var menu = new MenuFlyout();
            menu.Opening += (_, _) =>
            {
                menu.Items.Clear();
                var chosen = Selected();
                var targets = chosen.Length > 1 && chosen.Contains(item) ? chosen : [item];
                string many = targets.Length > 1 ? $" ({targets.Length} itens)" : "";
                if (targets.Length == 1)
                {
                    AddTo(menu, "Abrir", Symbol.OpenFile, () => { flyout?.Hide(); WindowsLauncherService.Shell(item.Path); return Task.CompletedTask; }, "Enter");
                    if (!item.IsDirectory)
                        AddTo(menu, "Abrir com…", Symbol.OpenWith, () => { flyout?.Hide(); WindowsLauncherService.Shell("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + item.Path); return Task.CompletedTask; });
                    AddTo(menu, "Mostrar no Explorador", Symbol.Folder, () => { flyout?.Hide(); WindowsLauncherService.Shell("explorer.exe", "/select,\"" + item.Path + "\""); return Task.CompletedTask; });
                    menu.Items.Add(new MenuFlyoutSeparator());
                }
                AddTo(menu, "Copiar" + many, Symbol.Copy, () => CopyItemsAsync(targets, false), "Ctrl+C");
                AddTo(menu, "Recortar" + many, Symbol.Cut, () => CopyItemsAsync(targets, true), "Ctrl+X");
                AddTo(menu, "Copiar caminho" + many, Symbol.Link, () => { CopyPaths(targets); return Task.CompletedTask; });
                if (targets.Length == 1) AddTo(menu, "Renomear", Symbol.Rename, () => RenameAsync(item, button.XamlRoot), "F2");
                AddTo(menu, "Excluir" + many, Symbol.Delete, () => DeleteAsync(targets), "Del");
                if (targets.Length == 1)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    AddTo(menu, "Propriedades", Symbol.Setting, () => { FolderShellOperations.Properties(item.Path); return Task.CompletedTask; });
                }
            };
            return menu;
        }

        grid.KeyDown += async (_, e) =>
        {
            bool ctrl = Pressed(VirtualKey.Control);
            var chosen = Selected();
            switch (e.Key)
            {
                case VirtualKey.Enter when chosen.Length == 1: e.Handled = true; flyout.Hide(); WindowsLauncherService.Shell(chosen[0].Path); break;
                case VirtualKey.F2 when chosen.Length == 1: e.Handled = true; await Guard(() => RenameAsync(chosen[0], grid.XamlRoot)); break;
                case VirtualKey.Delete when chosen.Length > 0: e.Handled = true; await Guard(() => DeleteAsync(chosen)); break;
                case VirtualKey.F5: e.Handled = true; await Guard(LoadAsync); break;
                case VirtualKey.C when ctrl && chosen.Length > 0: e.Handled = true; await Guard(() => CopyItemsAsync(chosen, false)); break;
                case VirtualKey.X when ctrl && chosen.Length > 0: e.Handled = true; await Guard(() => CopyItemsAsync(chosen, true)); break;
                case VirtualKey.V when ctrl: e.Handled = true; await Guard(PasteAsync); break;
                case VirtualKey.A when ctrl: e.Handled = true; grid.SelectAll(); break;
            }
        };

        GridViewItem Tile(RecentFolderItem item)
        {
            if (tiles.TryGetValue(item, out var existing)) return existing;
            var tile = new StackPanel { Spacing = 6, Width = 88, Tag = item };
            var preview = new Grid { Height = 64 };
            preview.Children.Add(new FontIcon { Glyph = item.IsDirectory ? "" : "", FontSize = 38 });
            tile.Children.Add(preview);
            tile.Children.Add(new TextBlock { Text = item.Name, FontSize = 11, MaxLines = 2, Height = 30, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
            var button = new GridViewItem { Content = tile, Tag = item, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 8), HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(5), Background = PanelKit.Res("ControlFillColorDefaultBrush") };
            ToolTipService.SetToolTip(button, item.Name + "\n" + item.Modified.ToLocalTime().ToString("g"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Name);
            button.ContextFlyout = ItemMenu(item, button);
            if (item.IsDirectory)
            {
                button.AllowDrop = true;
                button.DragOver += (_, e) =>
                {
                    if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                    e.AcceptedOperation = DropOperation(e);
                    e.DragUIOverride.Caption = (e.AcceptedOperation == DataPackageOperation.Move ? "Mover para " : "Copiar para ") + item.Name;
                    e.Handled = true;
                };
                button.Drop += async (_, e) => await DropIntoAsync(e, item.Path);
            }
            var token = current;
            bool thumbnailLoaded = false;
            button.Loaded += async (_, _) =>
            {
                if (thumbnailLoaded || token.IsCancellationRequested) return;
                thumbnailLoaded = true;
                await LoadThumbnailAsync(item, preview, token);
            };
            return tiles[item] = button;
        }

        return flyout;
        async Task LoadAsync()
        {
            loading?.Cancel(); loading?.Dispose();
            loading = new CancellationTokenSource();
            var token = current = loading.Token;
            items.Clear(); tiles.Clear(); grid.Items.Clear(); ShowStatus("Carregando…"); open.IsEnabled = false;
            try
            {
                path = await Task.Run(resolvePath, token);
                if (token.IsCancellationRequested) return;
                open.IsEnabled = true;
                var read = await Task.Run(() => RecentFolderItems.Read(path, token), token);
                if (token.IsCancellationRequested) return;
                items.AddRange(read);
                Filter();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) { ShowStatus("Não foi possível acessar esta pasta."); App.Log("Pasta recente: " + ex.Message); } }
        }
    }

    /// <summary>Shift move; senão copia (mais seguro), dentro do que a origem permite.</summary>
    private static DataPackageOperation DropOperation(DragEventArgs e)
    {
        bool move = (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift) != 0;
        var wanted = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        return (e.AllowedOperations & wanted) != 0 ? wanted : e.AllowedOperations & (DataPackageOperation.Copy | DataPackageOperation.Move);
    }
    private static async Task<IStorageItem> StorageItemAsync(RecentFolderItem item) => item.IsDirectory
        ? await StorageFolder.GetFolderFromPathAsync(item.Path) : await StorageFile.GetFileFromPathAsync(item.Path);
    private static RecentFolderItem? ResolveItem(object? value) => value switch
    {
        RecentFolderItem item => item,
        FrameworkElement { Tag: RecentFolderItem item } => item,
        _ => null,
    };
    private static async Task CopyItemsAsync(IEnumerable<RecentFolderItem> items, bool move)
    {
        var files = new List<IStorageItem>();
        foreach (var item in items) files.Add(await StorageItemAsync(item));
        if (files.Count == 0) return;
        var data = new DataPackage { RequestedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy };
        data.SetStorageItems(files);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }
    private static readonly SemaphoreSlim ThumbnailSlots = new(4);
    private static async Task LoadThumbnailAsync(RecentFolderItem item, Grid preview, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await ThumbnailSlots.WaitAsync(token); entered = true;
            if (token.IsCancellationRequested) return;
            var attributes = await Task.Run(() => File.GetAttributes(item.Path));
            // Cached thumbnails only for cloud placeholders; generating a preview must not download files.
            var options = ((uint)attributes & (0x1000u | 0x40000u | 0x400000u)) != 0
                ? ThumbnailOptions.ReturnOnlyIfCached : ThumbnailOptions.UseCurrentScale;
            if (token.IsCancellationRequested) return;
            using var thumbnail = item.IsDirectory
                ? await (await StorageFolder.GetFolderFromPathAsync(item.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 96, options)
                : await (await StorageFile.GetFileFromPathAsync(item.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 96, options);
            if (thumbnail is null || token.IsCancellationRequested) return;
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(thumbnail);
            if (token.IsCancellationRequested) return;
            preview.Children.Clear(); preview.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        }
        catch { /* Unavailable/offline items retain their file or folder glyph. */ }
        finally { if (entered) ThumbnailSlots.Release(); }
    }
}
