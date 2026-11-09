using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Devices.Enumeration;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Views.Panels;
using WinStatusBar.Services;
namespace WinStatusBar.Views;

internal static class ConnectionsFlyout
{
    public static void Show(Button anchor, bool wifi)
    {
        var root = new StackPanel { Width = 320, Spacing = 8, RequestedTheme = anchor.ActualTheme };
        var title = new TextBlock { Text = wifi ? "Redes Wi-Fi" : "Dispositivos Bluetooth", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var status = PanelKit.Caption("Procurando…", 12);
        var rows = new StackPanel { Spacing = 5 };
        var refresh = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Atualizar", FontSize = 12 });
        var windows = new HyperlinkButton { Content = "Configurações do Windows ↗", FontSize = 12, Padding = new Thickness(0) };
        windows.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(wifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; actions.Children.Add(refresh); actions.Children.Add(windows);
        root.Children.Add(title); root.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(actions);
        var flyout = new Flyout { Content = root, Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = false };
        PopupMaterial.Apply(flyout);
        var lifetime = new CancellationTokenSource();
        bool closed = false, busy = false;
        DeviceWatcher? watcher = null;
        var devices = new Dictionary<string, DeviceInformation>();
        void RenderDevices()
        {
            if (closed || busy) return;
            rows.Children.Clear();
            foreach (var device in devices.Values.Where(d => !string.IsNullOrWhiteSpace(d.Name)).OrderByDescending(BluetoothConnections.IsConnected).ThenByDescending(d => d.Pairing.IsPaired).ThenBy(d => d.Name))
            {
                bool connected = BluetoothConnections.IsConnected(device);
                var label = new StackPanel { Spacing = 2 };
                label.Children.Add(new TextBlock { Text = device.Name, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
                label.Children.Add(PanelKit.Caption(connected ? "Conectado" : device.Pairing.IsPaired ? "Pareado · não conectado" : "Disponível para pareamento", 11));
                var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = !device.Pairing.IsPaired && device.Pairing.CanPair };
                if (connected)
                {
                    button.IsEnabled = true;
                    button.Style = ((Button)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Button xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Style='{StaticResource AccentButtonStyle}' />")).Style;
                    foreach (var text in label.Children.OfType<TextBlock>()) text.Foreground = ThemeBrushes.Get("TextOnAccentFillColorPrimaryBrush");
                }
                button.Click += async (_, _) =>
                {
                    if (connected || device.Pairing.IsPaired) return;
                    busy = true; button.IsEnabled = false; refresh.IsEnabled = false; status.Text = "Pareando " + device.Name + "…";
                    try
                    {
                        var result = await device.Pairing.PairAsync();
                        if (!closed) status.Text = result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired ? "Pareamento concluído. A conexão depende do tipo de dispositivo." : "Pareamento não concluído: " + result.Status;
                    }
                    catch (Exception ex) { if (!closed) status.Text = ex.Message; }
                    finally { busy = false; refresh.IsEnabled = true; if (!closed) RenderDevices(); }
                };
                rows.Children.Add(button);
            }
        }
        async Task LoadWifi()
        {
            if (busy || closed) return;
            busy = true; refresh.IsEnabled = false; status.Text = "Procurando redes…"; rows.Children.Clear();
            try
            {
                var networks = await Task.Run(() => WifiNetworks.Read(true), lifetime.Token);
                if (networks.Count == 0) { await Task.Delay(1800, lifetime.Token); networks = await Task.Run(() => WifiNetworks.Read(false), lifetime.Token); }
                if (closed) return;
                status.Text = networks.Count == 0 ? "Nenhuma rede encontrada. Confira se o Wi-Fi está ligado e fora do modo avião." : "Escolha uma rede para conectar.";
                foreach (var network in networks)
                {
                    var item = new StackPanel { Spacing = 5 };
                    var label = new StackPanel { Spacing = 2 };
                    label.Children.Add(new TextBlock { Text = network.Name, FontSize = 13, TextWrapping = TextWrapping.Wrap });
                    label.Children.Add(PanelKit.Caption(network.Connected ? "Conectado" : $"Sinal {network.Signal}% · " + (network.Secure ? "Protegida" : "Aberta"), 11));
                    var choose = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = !network.Connected && network.Connectable };
                    if (network.Connected && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var selectedStyle)) { choose.Style = (Style)selectedStyle; choose.IsEnabled = true; foreach (var text in label.Children.OfType<TextBlock>()) text.Foreground = ThemeBrushes.Get("TextOnAccentFillColorPrimaryBrush"); }
                    item.Children.Add(choose);
                    var details = new StackPanel { Spacing = 5, Visibility = Visibility.Collapsed };
                    var password = new PasswordBox { PlaceholderText = "Senha da rede", Visibility = network.Secure && network.Profile.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
                    var connect = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Conectar" });
                    details.Children.Add(password); details.Children.Add(connect); item.Children.Add(details);
                    choose.Click += (_, _) => { if (network.Connected) return; details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (password.Visibility == Visibility.Visible) password.Focus(FocusState.Programmatic); };
                    if (WifiNetworks.NeedsWindowsSetup(network)) { connect.IsEnabled = false; details.Children.Add(PanelKit.Caption("Configure a segurança desta rede no Windows uma vez. Depois, selecione o perfil salvo aqui.", 11)); }
                    connect.Click += async (_, _) =>
                    {
                        if (busy) return;
                        bool connected = false; busy = true; refresh.IsEnabled = false; connect.IsEnabled = false;
                        status.Text = "Conectando a " + network.Name + "…";
                        try { string secret = password.Password; password.Password = ""; await Task.Run(() => WifiNetworks.ConnectAsync(network, secret, lifetime.Token), lifetime.Token); connected = true; if (!closed) { status.Text = network.Name + " · conectado"; ((TextBlock)label.Children[1]).Text = "Conectado"; if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent)) choose.Style = (Style)accent; foreach (var text in label.Children.OfType<TextBlock>()) text.Foreground = ThemeBrushes.Get("TextOnAccentFillColorPrimaryBrush"); details.Visibility = Visibility.Collapsed; choose.IsEnabled = true; } }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { if (!closed) status.Text = ex.Message; }
                        finally { busy = false; refresh.IsEnabled = true; connect.IsEnabled = true; }
                        if (connected && !closed) await LoadWifi();
                    };
                    rows.Children.Add(item);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { busy = false; refresh.IsEnabled = true; }
        }
        void LoadBluetooth()
        {
            if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); devices.Clear(); rows.Children.Clear(); status.Text = "Procurando dispositivos visíveis…";
            watcher = BluetoothConnections.CreateWatcher(true);
            var currentWatcher = watcher;
            watcher.Added += (_, device) => anchor.DispatcherQueue.TryEnqueue(() => { if (closed || watcher != currentWatcher) return; devices[device.Id] = device; status.Text = "Selecione um dispositivo para parear. Os já pareados são indicados abaixo."; RenderDevices(); });
            watcher.Updated += (_, change) => anchor.DispatcherQueue.TryEnqueue(() => { if (!closed && watcher == currentWatcher && devices.TryGetValue(change.Id, out var device)) { device.Update(change); RenderDevices(); } });
            watcher.Removed += (_, change) => anchor.DispatcherQueue.TryEnqueue(() => { if (closed || watcher != currentWatcher) return; devices.Remove(change.Id); RenderDevices(); });
            watcher.EnumerationCompleted += (_, _) => anchor.DispatcherQueue.TryEnqueue(() => { if (!closed && watcher == currentWatcher && devices.Count == 0) status.Text = "Nenhum dispositivo encontrado. Ative o Bluetooth e deixe o dispositivo visível para pareamento."; });
            watcher.Start();
        }
        refresh.Click += async (_, _) => { try { if (wifi) await LoadWifi(); else LoadBluetooth(); } catch (Exception ex) { status.Text = ex.Message; } };
        flyout.Opened += async (_, _) => { try { if (wifi) await LoadWifi(); else LoadBluetooth(); } catch (Exception ex) { status.Text = ex.Message; } };
        flyout.Closed += (_, _) => { closed = true; lifetime.Cancel(); if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); };
        flyout.ShowAt(anchor);
    }
}
