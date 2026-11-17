using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using WinStatusBar.Services;
using WinStatusBar.Views.Panels;
using System.Runtime.InteropServices.WindowsRuntime;

namespace WinStatusBar.Views;

/// <summary>
/// Hotspot móvel na própria barra, como em Configurações › Rede › Hotspot móvel: ligar e desligar, de qual conexão
/// compartilhar, nome, senha e banda (com "Editar"), dispositivos conectados e o QR code para conectar. Usa a API do
/// Windows (NetworkOperatorTetheringManager); nada é guardado pela barra.
/// </summary>
internal static class HotspotFlyout
{
    private static readonly (TetheringWiFiBand Band, string Name)[] Bands =
        [(TetheringWiFiBand.Auto, "Qualquer um disponível"), (TetheringWiFiBand.TwoPointFourGigahertz, "2,4 GHz"),
         (TetheringWiFiBand.FiveGigahertz, "5 GHz"), (TetheringWiFiBand.SixGigahertz, "6 GHz")];

    public static void Show(Button anchor, Action changed)
    {
        var root = new StackPanel { Width = 340, Spacing = 10, RequestedTheme = anchor.ActualTheme };
        var title = new TextBlock { Text = "Hotspot móvel", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var toggle = new ToggleSwitch { OnContent = "Ativado", OffContent = "Desativado", MinWidth = 0 };
        var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(toggle, 1); title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title); head.Children.Add(toggle);
        var status = PanelKit.Caption("", 12); status.TextWrapping = TextWrapping.Wrap;
        var source = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Compartilhar minha conexão com a Internet de" };
        var props = new StackPanel { Spacing = 4 };
        var edit = PanelKit.Small(new Button { Content = "Editar", FontSize = 12 });
        var propsHead = new Grid(); propsHead.ColumnDefinitions.Add(new ColumnDefinition()); propsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var propsTitle = new TextBlock { Text = "Propriedades da rede", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(edit, 1); propsHead.Children.Add(propsTitle); propsHead.Children.Add(edit);
        var qr = new Image { Width = 150, Height = 150, HorizontalAlignment = HorizontalAlignment.Left };
        var qrBox = new StackPanel { Spacing = 4 };
        qrBox.Children.Add(PanelKit.Caption("Digitalizar para conectar:", 12));
        qrBox.Children.Add(new Border { Child = qr, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4) });
        var editor = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        var nameBox = new TextBox { Header = "Nome da rede", MaxLength = 32 };
        var passBox = new TextBox { Header = "Senha (mínimo 8 caracteres)", MaxLength = 63 };
        var bandBox = new ComboBox { Header = "Banda", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var b in Bands) bandBox.Items.Add(b.Name);
        var save = PanelKit.Small(new Button { Content = "Salvar", FontSize = 12 });
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent)) save.Style = (Style)accent;
        var cancel = PanelKit.Small(new Button { Content = "Cancelar", FontSize = 12 });
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; editActions.Children.Add(save); editActions.Children.Add(cancel);
        editor.Children.Add(nameBox); editor.Children.Add(passBox); editor.Children.Add(bandBox); editor.Children.Add(editActions);
        var clients = PanelKit.Caption("", 12); clients.TextWrapping = TextWrapping.Wrap;
        var windows = new HyperlinkButton { Content = "Configurações do Windows ↗", FontSize = 12, Padding = new Thickness(0) };
        windows.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:network-mobilehotspot"));

        root.Children.Add(head); root.Children.Add(status); root.Children.Add(source);
        root.Children.Add(propsHead); root.Children.Add(props); root.Children.Add(editor); root.Children.Add(qrBox);
        root.Children.Add(clients); root.Children.Add(windows);
        var flyout = new Flyout { Content = new ScrollViewer { Content = root, MaxHeight = 640, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = false };
        PopupMaterial.Apply(flyout);

        var profiles = new List<ConnectionProfile>();
        NetworkOperatorTetheringManager? manager = null;
        bool loading = false, closed = false;
        flyout.Closed += (_, _) => { closed = true; changed(); };

        void Field(string label, string value)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var l = PanelKit.Caption(label, 12); var v = new TextBlock { Text = value, FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1); row.Children.Add(l); row.Children.Add(v); props.Children.Add(row);
        }

        async Task RenderAsync()
        {
            if (closed || manager is null) return;
            loading = true;
            try
            {
                var cfg = manager.GetCurrentAccessPointConfiguration();
                bool on = manager.TetheringOperationalState == TetheringOperationalState.On;
                toggle.IsOn = on;
                toggle.IsEnabled = manager.TetheringOperationalState is TetheringOperationalState.On or TetheringOperationalState.Off;
                status.Text = manager.TetheringOperationalState switch
                {
                    TetheringOperationalState.InTransition => "Mudando o estado…",
                    TetheringOperationalState.Unknown => "O Windows não informou o estado do hotspot.",
                    _ => on ? "Outros dispositivos podem usar a sua conexão." : "Desligado.",
                };
                props.Children.Clear();
                Field("Nome:", cfg.Ssid);
                Field("Senha:", cfg.Passphrase);
                Field("Banda:", Bands.FirstOrDefault(b => b.Band == cfg.Band).Name ?? cfg.Band.ToString());
                qr.Source = await QrAsync(cfg.Ssid, cfg.Passphrase);
                int count = on ? (int)manager.ClientCount : 0;
                clients.Text = !on ? "" : count == 0 ? $"Dispositivos conectados: nenhum (máximo {manager.MaxClientCount})"
                    : $"Dispositivos conectados: {count} de {manager.MaxClientCount}\n" + string.Join("\n", manager.GetTetheringClients().Select(c => "• " + (c.HostNames.FirstOrDefault()?.DisplayName ?? c.MacAddress)));
            }
            catch (Exception ex) { status.Text = "Não foi possível ler o hotspot: " + ex.Message; App.Log("Hotspot: " + ex.Message); }
            finally { loading = false; }
        }

        void Load()
        {
            loading = true;
            try
            {
                profiles = NetworkInformation.GetConnectionProfiles()
                    .Where(p => p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess).ToList();
                var current = NetworkInformation.GetInternetConnectionProfile();
                source.Items.Clear();
                foreach (var p in profiles) source.Items.Add(p.ProfileName);
                int index = current is null ? 0 : Math.Max(0, profiles.FindIndex(p => p.ProfileName == current.ProfileName));
                if (profiles.Count == 0)
                {
                    status.Text = "Sem conexão com a Internet para compartilhar.";
                    toggle.IsEnabled = false; source.IsEnabled = false; edit.IsEnabled = false;
                    return;
                }
                source.SelectedIndex = index;
                manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profiles[index]);
            }
            catch (Exception ex) { status.Text = "Hotspot indisponível neste PC: " + ex.Message; toggle.IsEnabled = false; }
            finally { loading = false; }
            _ = RenderAsync();
        }

        source.SelectionChanged += async (_, _) =>
        {
            if (loading || source.SelectedIndex < 0 || source.SelectedIndex >= profiles.Count) return;
            bool wasOn = manager?.TetheringOperationalState == TetheringOperationalState.On;
            try
            {
                if (wasOn) await manager!.StopTetheringAsync();
                manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profiles[source.SelectedIndex]);
                if (wasOn) await manager.StartTetheringAsync();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            await RenderAsync();
        };

        toggle.Toggled += async (_, _) =>
        {
            if (loading || manager is null) return;
            toggle.IsEnabled = false;
            status.Text = toggle.IsOn ? "Ligando…" : "Desligando…";
            if (!toggle.IsOn) HotspotKeeper.TurnedOffByUser();
            try
            {
                var result = toggle.IsOn ? await manager.StartTetheringAsync() : await manager.StopTetheringAsync();
                if (result.Status != TetheringOperationStatus.Success)
                    status.Text = "O Windows não concluiu: " + Describe(result.Status) + (string.IsNullOrWhiteSpace(result.AdditionalErrorMessage) ? "" : " (" + result.AdditionalErrorMessage + ")");
                App.Log($"Hotspot: {(toggle.IsOn ? "ligar" : "desligar")} → {result.Status}");
            }
            catch (Exception ex) { status.Text = "Não foi possível mudar o hotspot: " + ex.Message; }
            string keep = status.Text;
            await RenderAsync();
            if (keep.StartsWith("O Windows não concluiu") || keep.StartsWith("Não foi possível")) status.Text = keep;
            toggle.IsEnabled = true;
            changed();
        };

        edit.Click += (_, _) =>
        {
            if (manager is null) return;
            var cfg = manager.GetCurrentAccessPointConfiguration();
            nameBox.Text = cfg.Ssid; passBox.Text = cfg.Passphrase;
            bandBox.SelectedIndex = Math.Max(0, Array.FindIndex(Bands, b => b.Band == cfg.Band));
            editor.Visibility = Visibility.Visible; props.Visibility = Visibility.Collapsed; edit.Visibility = Visibility.Collapsed;
        };
        cancel.Click += (_, _) => { editor.Visibility = Visibility.Collapsed; props.Visibility = Visibility.Visible; edit.Visibility = Visibility.Visible; };
        save.Click += async (_, _) =>
        {
            if (manager is null) return;
            string ssid = nameBox.Text.Trim(), pass = passBox.Text;
            if (ssid.Length == 0) { status.Text = "Informe o nome da rede."; return; }
            if (pass.Length < 8) { status.Text = "A senha precisa ter pelo menos 8 caracteres."; return; }
            save.IsEnabled = false;
            try
            {
                var cfg = manager.GetCurrentAccessPointConfiguration();
                cfg.Ssid = ssid; cfg.Passphrase = pass;
                var band = Bands[Math.Max(0, bandBox.SelectedIndex)].Band;
                if (cfg.IsBandSupported(band)) cfg.Band = band;
                else status.Text = "Banda não suportada por este adaptador; mantida a atual.";
                await manager.ConfigureAccessPointAsync(cfg);
                App.Log("Hotspot: propriedades alteradas");
                cancel.Visibility = Visibility.Visible;
                editor.Visibility = Visibility.Collapsed; props.Visibility = Visibility.Visible; edit.Visibility = Visibility.Visible;
                await RenderAsync();
            }
            catch (Exception ex) { status.Text = "Não foi possível salvar: " + ex.Message; }
            finally { save.IsEnabled = true; }
        };

        Load();
        flyout.ShowAt(anchor);
    }

    private static string Describe(TetheringOperationStatus s) => s switch
    {
        TetheringOperationStatus.WiFiDeviceOff => "o Wi-Fi está desligado",
        TetheringOperationStatus.EntitlementCheckFailure => "a operadora não permite compartilhar esta conexão",
        TetheringOperationStatus.MobileBroadbandDeviceOff => "a conexão móvel está desligada",
        TetheringOperationStatus.OperationInProgress => "outra mudança ainda está em andamento",
        TetheringOperationStatus.BluetoothDeviceOff => "o Bluetooth está desligado",
        TetheringOperationStatus.NetworkLimitedConnectivity => "a conexão está limitada",
        TetheringOperationStatus.AlreadyOn => "já estava ligado",
        _ => s.ToString(),
    };

    /// <summary>QR code padrão de Wi-Fi (o mesmo formato que as câmeras de celular reconhecem).</summary>
    private static async Task<BitmapImage?> QrAsync(string ssid, string pass)
    {
        try
        {
            static string Esc(string v) => v.Replace(@"\", @"\\").Replace(";", @"\;").Replace(",", @"\,").Replace(":", @"\:").Replace("\"", "\\\"");
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode($"WIFI:T:WPA;S:{Esc(ssid)};P:{Esc(pass)};;", QRCoder.QRCodeGenerator.ECCLevel.M);
            byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch { return null; }
    }
}
