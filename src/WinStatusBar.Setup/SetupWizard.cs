using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinStatusBar.Setup;

/// <summary>
/// Assistente de instalação em 3 etapas (Sobre → Instalando → Concluído) para quem abre o EXE com dois cliques.
/// As atualizações automáticas usam /quiet e nunca mostram esta janela.
/// </summary>
internal sealed class SetupWizard : Form
{
    private const int StepCount = 6, ContentWidth = 496;
    private readonly string _version;
    private readonly string? _installedVersion;
    private readonly Action<Action<string>> _install;
    private readonly string _logPath;
    private readonly bool _dark = IsDarkTheme();
    private readonly Color _back, _footer, _text, _secondary, _accent, _accentText, _buttonBack, _buttonBorder;
    private readonly string _family;
    private readonly Label _title = new();
    private readonly FlowLayoutPanel _content = new();
    private readonly FlowLayoutPanel _buttons = new();
    private bool _installing;
    private CheckBox? _launch;

    public bool LaunchRequested { get; private set; }

    public SetupWizard(string version, string? installedVersion, Action<Action<string>> install, string logPath)
    {
        _version = version;
        _installedVersion = installedVersion;
        _install = install;
        _logPath = logPath;
        _family = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        (_back, _footer, _text, _secondary, _accent, _accentText, _buttonBack, _buttonBorder) = _dark
            ? (Hex("#202020"), Hex("#1B1B1B"), Hex("#FFFFFF"), Hex("#C5C5C5"), Hex("#60CDFF"), Hex("#000000"), Hex("#2D2D2D"), Hex("#3F3F3F"))
            : (Hex("#F3F3F3"), Hex("#EBEBEB"), Hex("#1A1A1A"), Hex("#5D5D5D"), Hex("#005FB8"), Hex("#FFFFFF"), Hex("#FBFBFB"), Hex("#D0D0D0"));

        Text = "Instalar o WinStatusBar";
        // Escala calculada pelo DPI da tela (as fontes em pontos já acompanham o DPI; tamanhos e posições não).
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(S(560), S(440));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _back;
        ForeColor = _text;
        Font = new Font(_family, 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var icon = new PictureBox { Location = new Point(S(32), S(28)), Size = new Size(S(48), S(48)), SizeMode = PictureBoxSizeMode.Zoom };
        try { icon.Image = Icon?.ToBitmap(); } catch { }
        _title.Location = new Point(S(96), S(36));
        _title.AutoSize = true;
        _title.Font = new Font(FontFamily.Families.Any(f => f.Name == "Segoe UI Semibold") ? "Segoe UI Semibold" : _family, 16f);
        _content.Location = new Point(S(32), S(96));
        _content.Size = new Size(S(ContentWidth), S(260));
        _content.FlowDirection = FlowDirection.TopDown;
        _content.WrapContents = false;
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(64), BackColor = _footer };
        _buttons.Dock = DockStyle.Right;
        _buttons.Width = S(400);
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.Padding = new Padding(0, S(14), S(24), 0);
        footer.Controls.Add(_buttons);
        Controls.AddRange([icon, _title, _content, footer]);
        FormClosing += (_, e) => { if (_installing) e.Cancel = true; };
        ShowAbout();
    }

    // ---------------------------------------------------------------- etapas
    private void ShowAbout()
    {
        _title.Text = "WinStatusBar " + _version;
        Clear();
        AddText("Barra no topo da tela com CPU, RAM, GPU e rede, as cotas e tarefas das IAs e um chat com várias IAs (Claude, Codex, Gemini, Copilot e outras) que usa o contexto da janela ativa.", _text, bottom: 10);
        AddText("• Instala só para este usuário, sem pedir administrador.", _text);
        AddText("• Cria o atalho no menu Iniciar e abre junto com o Windows.", _text);
        AddText("• Atualiza-se sozinha pelo GitHub, sem interromper você.", _text, bottom: 10);
        if (_installedVersion == _version)
            AddText($"A versão {_version} já está instalada. Ela será instalada de novo; suas configurações são mantidas.", _text, bottom: 10);
        else if (_installedVersion is not null)
            AddText($"Versão instalada: {_installedVersion}. Ela será substituída pela {_version}; suas configurações são mantidas.", _text, bottom: 10);
        AddText("Requer Windows 10 (19041) ou 11, 64 bits. O Microsoft Edge WebView2 é instalado se faltar.", _secondary);
        SetButtons((_installedVersion is null ? "Instalar" : "Atualizar", true, StartInstall), ("Cancelar", false, Close));
    }

    private void StartInstall()
    {
        _installing = true;
        _title.Text = "Instalando…";
        Clear();
        var step = AddText("Preparando…", _text, bottom: 12);
        var bar = new ProgressBar { Width = S(ContentWidth), Height = S(8), Maximum = StepCount, Style = ProgressBarStyle.Continuous };
        _content.Controls.Add(bar);
        SetButtons();
        int done = 0;
        var worker = new Thread(() =>
        {
            Exception? error = null;
            try
            {
                _install(text => BeginInvoke((MethodInvoker)(() =>
                {
                    step.Text = text;
                    bar.Value = Math.Min(StepCount, ++done);
                })));
            }
            catch (Exception ex) { error = ex; }
            BeginInvoke((MethodInvoker)(() => ShowFinished(error)));
        }) { IsBackground = true, Name = "Instalação" };
        worker.SetApartmentState(ApartmentState.STA); // o atalho do menu Iniciar usa COM
        worker.Start();
    }

    private void ShowFinished(Exception? error)
    {
        _installing = false;
        Clear();
        if (error is null)
        {
            _title.Text = "Pronto";
            AddText("O WinStatusBar foi instalado.", _text, bottom: 14);
            _launch = new CheckBox { Text = "Abrir o WinStatusBar agora", Checked = true, AutoSize = true, ForeColor = _text };
            _content.Controls.Add(_launch);
            SetButtons(("Concluir", true, () => { LaunchRequested = _launch.Checked; Close(); }));
        }
        else
        {
            _title.Text = "A instalação não foi concluída";
            AddText(error.Message, _text, bottom: 10); // já vem explicado pelo Program (SetupErrorText)
            SetButtons(("Ver registro", false, OpenLog), ("Fechar", true, Close));
        }
    }

    private void OpenLog()
    {
        try { if (File.Exists(_logPath)) Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _logPath + "\"") { UseShellExecute = true }); } catch { }
    }

    // ---------------------------------------------------------------- montagem
    private void Clear()
    {
        foreach (Control c in _content.Controls.Cast<Control>().ToArray()) c.Dispose();
        _content.Controls.Clear();
    }

    private Label AddText(string text, Color color, int bottom = 4)
    {
        var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(S(ContentWidth), 0), ForeColor = color, Margin = new Padding(0, 0, 0, S(bottom)) };
        _content.Controls.Add(label);
        return label;
    }

    /// <summary>Botões da esquerda para a direita, na ordem dada (o painel cresce da direita).</summary>
    private void SetButtons(params (string Text, bool Primary, Action Click)[] buttons)
    {
        foreach (Control c in _buttons.Controls.Cast<Control>().ToArray()) c.Dispose();
        _buttons.Controls.Clear();
        foreach (var (text, primary, click) in buttons.Reverse())
        {
            var button = new Button
            {
                Text = text, Size = new Size(S(120), S(34)), Margin = new Padding(S(8), 0, 0, 0), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? _accent : _buttonBack, ForeColor = primary ? _accentText : _text, UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderColor = primary ? _accent : _buttonBorder;
            button.Click += (_, _) => click();
            _buttons.Controls.Add(button);
            if (primary) { AcceptButton = button; ActiveControl = button; }
        }
    }

    // ---------------------------------------------------------------- tema
    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return false; }
    }

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    /// <summary>Tamanho de projeto (96 DPI) convertido para o DPI da tela.</summary>
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_dark) { int on = 1; _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } // barra de título escura
    }
}
