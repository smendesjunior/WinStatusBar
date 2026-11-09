using Microsoft.Web.WebView2.WinForms;

namespace CodexIAs.Providers;

/// <summary>
/// Janela que hospeda a página do Copilot ou do Gemini. Oculta: fora de todas as telas, sem entrada na barra de
/// tarefas e sem nunca receber ativação (o foco do Windows fica onde estava). Visível: só para a pessoa entrar na conta.
/// </summary>
internal sealed class WebHostForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private readonly bool _hidden;
    public WebView2 Web { get; } = new() { Dock = DockStyle.Fill };

    public WebHostForm(string title, bool hidden)
    {
        _hidden = hidden;
        Text = title;
        ShowInTaskbar = !hidden;
        StartPosition = hidden ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        Size = new Size(1000, 760);
        if (hidden)
        {
            var screen = SystemInformation.VirtualScreen;
            Location = new Point(screen.Left - Width - 1024, screen.Top - Height - 1024);
        }
        Controls.Add(Web);
    }

    protected override bool ShowWithoutActivation => _hidden;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_hidden) cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
