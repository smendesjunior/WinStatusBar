using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.ViewModels;

public sealed class SessionItem
{
    public required string Id { get; init; }
    public required AgentKind Kind { get; init; }
    public required string Agent { get; init; }
    public required string Title { get; init; }
    public required string Caption { get; init; }
    public required string State { get; init; }
    public required string When { get; init; }
    public required Brush DotBrush { get; init; }
    public ImageSource? Icon { get; init; }
    public string Project { get; init; } = string.Empty;
}

/// <summary>Linha do menu de agentes: "Automático" ou um agente instalado (Codex, Claude Code, IA local).</summary>
public sealed class AgentOption
{
    public required string Key { get; init; }           // auto | codex | claude | local
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public string Glyph { get; init; } = "";
    public required string Title { get; init; }
    public required string Caption { get; init; }
    public required string State { get; init; }
    public required Brush DotBrush { get; init; }
    public string Detail { get; init; } = string.Empty;  // "3 sessões · agora"
    /// <summary>Cota/saldo completo do serviço (dica da coluna da direita e comparação de mudanças).</summary>
    public string Quota { get; init; } = string.Empty;
    public string QuotaTip => string.IsNullOrEmpty(Quota) ? Status : Quota;

    // Menu v3: selo ao lado do nome, duas linhas de texto e até dois anéis de cota em colunas fixas.
    public string Status { get; init; } = string.Empty;
    public Brush? StatusForeground { get; init; }
    public Brush? StatusBackground { get; init; }
    public Visibility StatusVisible => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
    private readonly string? _line1, _line2;
    /// <summary>Estado · atividade · quando (ou "Mostraria…" no Automático).</summary>
    public string Line1 { get => _line1 ?? (Key == "auto" ? Title : State + " · " + Title); init => _line1 = value; }
    /// <summary>Plano · API (ou a explicação do Automático).</summary>
    public string Line2 { get => _line2 ?? Caption; init => _line2 = value; }
    public Visibility Line2Visible => string.IsNullOrEmpty(Line2) ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Resumo breve da última tarefa executada ("Executando comandos…", "Resposta concluída…").</summary>
    public string Summary { get; init; } = string.Empty;
    public Visibility SummaryVisible => string.IsNullOrEmpty(Summary) ? Visibility.Collapsed : Visibility.Visible;
    public string Ring1Label { get; init; } = string.Empty;
    public string Ring1Center { get; init; } = string.Empty;
    public string Ring1Reset { get; init; } = string.Empty;
    public double Ring1Value { get; init; }
    public Brush? Ring1Brush { get; init; }
    public Visibility Ring1Visible => string.IsNullOrEmpty(Ring1Label) ? Visibility.Collapsed : Visibility.Visible;
    public string Ring2Label { get; init; } = string.Empty;
    public string Ring2Center { get; init; } = string.Empty;
    public string Ring2Reset { get; init; } = string.Empty;
    public double Ring2Value { get; init; }
    public Brush? Ring2Brush { get; init; }
    public Visibility Ring2Visible => string.IsNullOrEmpty(Ring2Label) ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Texto no lugar dos anéis quando o serviço não tem cota ("Sem cota · roda no PC").</summary>
    public string RingNote { get; init; } = string.Empty;
    public Visibility RingNoteVisible => string.IsNullOrEmpty(RingNote) ? Visibility.Collapsed : Visibility.Visible;
    public bool NeedsSetup { get; init; }
    public Visibility ConfigureVisible => NeedsSetup ? Visibility.Visible : Visibility.Collapsed;
    public bool IsSelected { get; init; }
    /// <summary>A IA está trabalhando agora (executando ou aguardando a resposta do serviço): o logo fica animado.</summary>
    public bool Working { get; init; }
    // Codex, Claude e IA local usam as animações próprias da barra; as demais, AgentMotion (sem deformar o logo).
    public Visibility CodexAnimVisible => Working && Key == "codex" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClaudeAnimVisible => Working && Key == "claude" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OllamaAnimVisible => Working && Key == "local" ? Visibility.Visible : Visibility.Collapsed;
    private bool OwnAnimation => Working && Key is "codex" or "claude" or "local";
    public bool MotionActive => Working && !OwnAnimation;
    public string MotionKind => global::WinStatusBar.Services.AgentMotion.KindFor(Key);
    public Visibility IconVisible => Icon is null || OwnAnimation ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisible => Icon is null && !OwnAnimation ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckVisible => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DotVisible => Key == "auto" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailVisible => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Linha sutil entre os serviços no menu (todas as linhas, menos "Automático").</summary>
    public Visibility DividerVisible => Key == "auto" ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Mesmo conteúdo visível (decide se a linha do menu precisa ser redesenhada).</summary>
    public bool SameContent(AgentOption o) =>
        Key == o.Key && Working == o.Working && Quota == o.Quota && Status == o.Status && Line1 == o.Line1 && Line2 == o.Line2
        && Summary == o.Summary && Ring1Center == o.Ring1Center && Ring2Center == o.Ring2Center && Ring1Reset == o.Ring1Reset && Ring2Reset == o.Ring2Reset
        && RingNote == o.RingNote && Title == o.Title && Caption == o.Caption && State == o.State && Detail == o.Detail
        && IsSelected == o.IsSelected && NeedsSetup == o.NeedsSetup && Icon == o.Icon;
}

/// <summary>
/// Linha fixa do menu de agentes. A linha (e o item visual do ListView) continua a mesma enquanto o serviço
/// estiver no menu; só o conteúdo (<see cref="Option"/>) é trocado. Assim o realce do mouse, as animações e os
/// anéis não são recriados a cada atualização, e o menu aberto não pisca.
/// </summary>
public sealed class AgentOptionSlot(AgentOption option) : ObservableObject
{
    private AgentOption _option = option;
    public AgentOption Option { get => _option; set => Set(ref _option, value); }
}

/// <summary>Estado de apresentação da barra (compartilhado por todas as janelas/monitores).</summary>
public sealed class BarViewModel : ObservableObject
{
    // ---------- Agente ----------
    private string _agentName = global::WinStatusBar.Services.Localization.T("IA");
    private string _agentState = global::WinStatusBar.Services.Localization.T("Ocioso");
    private Brush? _agentDot, _agentBorder;
    private double _agentTintOpacity;
    public Brush? AgentBorder { get => _agentBorder; set => Set(ref _agentBorder, value); }
    public double AgentTintOpacity { get => _agentTintOpacity; set => Set(ref _agentTintOpacity, value); }
    private bool _agentActive;
    /// <summary>Agente exibido está trabalhando: liga o brilho que percorre o contorno do módulo.</summary>
    public bool AgentActive { get => _agentActive; set => Set(ref _agentActive, value); }
    private ImageSource? _agentIcon;
    private Visibility _agentIconVisible = Visibility.Collapsed, _agentGlyphVisible = Visibility.Visible;
    public string AgentName { get => _agentName; set => Set(ref _agentName, value); }
    public string AgentState { get => _agentState; set => Set(ref _agentState, value); }
    public Brush? AgentDot { get => _agentDot; set => Set(ref _agentDot, value); }
    public ImageSource? AgentIcon { get => _agentIcon; set { if (Set(ref _agentIcon, value)) RefreshAgentLogo(); } }
    private bool _claudeWorking;
    private Visibility _claudeDotsVisible = Visibility.Collapsed;
    /// <summary>Claude Code exibido e executando: os três pontos animados do Claude substituem o logotipo.</summary>
    public bool ClaudeWorking { get => _claudeWorking; set { if (Set(ref _claudeWorking, value)) RefreshAgentLogo(); } }
    public Visibility ClaudeDotsVisible { get => _claudeDotsVisible; set => Set(ref _claudeDotsVisible, value); }
    private bool _codexWorking, _agentOpenAi;
    private Visibility _openAiLogoVisible = Visibility.Collapsed;
    /// <summary>Codex/ChatGPT exibido e executando: o logotipo vetorial do ChatGPT gira no próprio centro.</summary>
    public bool CodexWorking { get => _codexWorking; set => Set(ref _codexWorking, value); }
    /// <summary>Agente exibido é o Codex/ChatGPT: usa o logotipo vetorial (branco no escuro, preto no claro) no lugar da imagem.</summary>
    public bool AgentOpenAi { get => _agentOpenAi; set { if (Set(ref _agentOpenAi, value)) RefreshAgentLogo(); } }
    public Visibility OpenAiLogoVisible { get => _openAiLogoVisible; set => Set(ref _openAiLogoVisible, value); }
    private bool _agentOllama, _ollamaWorking;
    private Visibility _ollamaLogoVisible = Visibility.Collapsed;
    public bool AgentOllama { get => _agentOllama; set { if (Set(ref _agentOllama, value)) RefreshAgentLogo(); } }
    public bool OllamaWorking { get => _ollamaWorking; set => Set(ref _ollamaWorking, value); }
    private string _extraWorking = string.Empty;
    /// <summary>IA adicional trabalhando agora ("gemini", "copilot", "deepseek", "grok"); vazio = nenhuma. Anima o logo.</summary>
    public string ExtraWorking { get => _extraWorking; set => Set(ref _extraWorking, value); }
    public Visibility OllamaLogoVisible { get => _ollamaLogoVisible; set => Set(ref _ollamaLogoVisible, value); }
    private void RefreshAgentLogo()
    {
        ClaudeDotsVisible = _claudeWorking ? Visibility.Visible : Visibility.Collapsed;
        OpenAiLogoVisible = _agentOpenAi && !_claudeWorking ? Visibility.Visible : Visibility.Collapsed;
        OllamaLogoVisible = _agentOllama ? Visibility.Visible : Visibility.Collapsed;
        AgentIconVisible = _agentIcon is not null && !_agentOpenAi && !_claudeWorking && !_agentOllama ? Visibility.Visible : Visibility.Collapsed;
        AgentGlyphVisible = _agentIcon is null && !_agentOpenAi && !_claudeWorking && !_agentOllama ? Visibility.Visible : Visibility.Collapsed;
    }
    public Visibility AgentIconVisible { get => _agentIconVisible; set => Set(ref _agentIconVisible, value); }
    public Visibility AgentGlyphVisible { get => _agentGlyphVisible; set => Set(ref _agentGlyphVisible, value); }
    private string _agentTooltip = string.Empty;
    public string AgentTooltip { get => _agentTooltip; set => Set(ref _agentTooltip, value); }

    // ---------- Tarefa ----------
    private string _taskTitle = global::WinStatusBar.Services.Localization.T("Nenhuma tarefa em execução"), _taskCaption = global::WinStatusBar.Services.Localization.T("Codex e Claude Code ociosos"), _taskTooltip = string.Empty, _progressText = string.Empty;
    private bool _indeterminate;
    private bool _taskRunning;
    public bool TaskRunning { get => _taskRunning; set => Set(ref _taskRunning, value); }
    private double _progress;
    private Visibility _progressVisible = Visibility.Collapsed, _progressTextVisible = Visibility.Collapsed;
    public string TaskTitle { get => _taskTitle; set => Set(ref _taskTitle, value); }
    public string TaskCaption { get => _taskCaption; set => Set(ref _taskCaption, value); }
    public string TaskTooltip { get => _taskTooltip; set => Set(ref _taskTooltip, value); }
    public bool ProgressIndeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public double ProgressValue { get => _progress; set => Set(ref _progress, value); }
    public Visibility ProgressVisible { get => _progressVisible; set => Set(ref _progressVisible, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public Visibility ProgressTextVisible { get => _progressTextVisible; set => Set(ref _progressTextVisible, value); }
    public ObservableCollection<SessionItem> Sessions { get; } = new();
    public ObservableCollection<AgentOptionSlot> AgentOptions { get; } = new();
    private Visibility _noSessions = Visibility.Visible;
    public Visibility NoSessionsVisible { get => _noSessions; set => Set(ref _noSessions, value); }

    // ---------- Rede ----------
    private string _up = "— KB/s", _down = "— KB/s", _netTip = string.Empty, _netGlyph = "";
    /// <summary>Glifo do tipo de conexão ativa: Wi-Fi (E701), cabo/Ethernet (E839), banda larga móvel (EC05 NetworkTower), sem conexão (E968).</summary>
    public string NetGlyph { get => _netGlyph; set => Set(ref _netGlyph, value); }
    public string NetUp { get => _up; set => Set(ref _up, value); }
    public string NetDown { get => _down; set => Set(ref _down, value); }
    public string NetTooltip { get => _netTip; set => Set(ref _netTip, value); }

    // ---------- CPU / RAM / GPU ----------
    private string _cpu = "—", _cpuTip = string.Empty, _ram = "—", _ramTip = string.Empty, _gpu = "—", _gpuTip = string.Empty, _gpuLabel = global::WinStatusBar.Services.Localization.T("GPU");
    public string CpuText { get => _cpu; set => Set(ref _cpu, value); }
    public string CpuTooltip { get => _cpuTip; set => Set(ref _cpuTip, value); }
    private string _ramTitle = "RAM";
    public string RamTitle { get => _ramTitle; set => Set(ref _ramTitle, value); }
    public string RamText { get => _ram; set => Set(ref _ram, value); }
    public string RamTooltip { get => _ramTip; set => Set(ref _ramTip, value); }
    public string GpuText { get => _gpu; set => Set(ref _gpu, value); }
    public string GpuTooltip { get => _gpuTip; set => Set(ref _gpuTip, value); }
    public string GpuLabel { get => _gpuLabel; set => Set(ref _gpuLabel, value); }

    private string _cpuTemperatureSource = "";
    public string CpuTemperatureSource { get => _cpuTemperatureSource; set => Set(ref _cpuTemperatureSource, value); }
    private string _cpuTemperature = "", _gpuTemperature = "";
    public string CpuTemperature { get => _cpuTemperature; set => Set(ref _cpuTemperature, value); }
    public string GpuTemperature { get => _gpuTemperature; set => Set(ref _gpuTemperature, value); }
    public void UpdateTemperatures(TemperatureSample t, bool enabled)
    {
        CpuTemperatureSource = enabled ? t.CpuSource : global::WinStatusBar.Services.Localization.T("Temperaturas desativadas");
        // Adaptativo: sem sensor real neste PC, a temperatura simplesmente não aparece (nada de "— °C" permanente).
        CpuTemperature = !enabled ? "" : t.Cpu is double c ? $" · {c:0} °C" : "";
        GpuTemperature = !enabled ? "" : t.Gpu is double g ? $" · {g:0} °C" : "";
    }

    private string _batteryText = "—", _batteryTime = "", _batteryStatus = "", _batteryTooltip = "";
    private bool _batteryAvailable;
    public bool BatteryAvailable { get => _batteryAvailable; set => Set(ref _batteryAvailable, value); }
    public string BatteryText { get => _batteryText; set => Set(ref _batteryText, value); }
    public string BatteryTime { get => _batteryTime; set => Set(ref _batteryTime, value); }
    private int _batteryPercent = -1;
    private bool _batteryCharging, _batterySaver, _batteryPlugged;
    public int BatteryPercent { get => _batteryPercent; set => Set(ref _batteryPercent, value); }
    public bool BatteryCharging { get => _batteryCharging; set => Set(ref _batteryCharging, value); }
    public bool BatteryPlugged { get => _batteryPlugged; set => Set(ref _batteryPlugged, value); }
    public bool BatterySaver { get => _batterySaver; set => Set(ref _batterySaver, value); }
    public string BatteryStatus { get => _batteryStatus; set => Set(ref _batteryStatus, value); }
    public string BatteryTooltip { get => _batteryTooltip; set => Set(ref _batteryTooltip, value); }
    private string _batteryLine1 = "—", _batteryLine2 = string.Empty;
    /// <summary>Estado em duas linhas curtas ao lado da porcentagem ("Conectada" / "sem carregar"), em vez de uma linha longa.</summary>
    public string BatteryLine1 { get => _batteryLine1; set => Set(ref _batteryLine1, value); }
    public string BatteryLine2 { get => _batteryLine2; set => Set(ref _batteryLine2, value); }
    public void UpdateBattery(BatterySample b)
    {
        BatteryAvailable = b.Present != false;
        BatteryText = b.Percent is int p ? $"{p}%" : "—";
        BatteryPercent = b.Percent ?? -1;
        BatteryCharging = b.Charging;
        BatterySaver = b.Saver;
        BatteryPlugged = b.Plugged == true;
        BatteryStatus = global::WinStatusBar.Services.Localization.T(b.Present != true ? "Bateria indisponível" : b.Charging ? "Carregando" : b.Percent == 100 && b.Plugged == true ? "Carga completa" : b.Plugged == true ? "Conectada, sem carregar" : b.Plugged == false ? "Usando bateria" : "Alimentação desconhecida");
        if (b.Remaining is TimeSpan t)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(t.TotalMinutes));
            string duration = minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
            BatteryTime = b.Charging ? $"≈ {duration} até 100%" : $"≈ {duration} restantes";
        }
        else BatteryTime = b.Charging || b.Plugged == false ? global::WinStatusBar.Services.Localization.T("Tempo indisponível") : BatteryStatus;
        BatteryTooltip = $"{BatteryStatus} · {BatteryText}\n{BatteryTime}";
        string L(string t) => global::WinStatusBar.Services.Localization.T(t);
        string? remaining = b.Remaining is TimeSpan r ? (r.TotalMinutes >= 60 ? $"≈ {(int)r.TotalHours} h {r.Minutes:00} min" : $"≈ {Math.Max(1, (int)Math.Ceiling(r.TotalMinutes))} min") : null;
        (BatteryLine1, BatteryLine2) = b.Present != true ? (L("Bateria"), L("indisponível"))
            : b.Charging ? (L("Carregando"), remaining is null ? L("tempo indisponível") : remaining + " " + L("até 100%"))
            : b.Percent == 100 && b.Plugged == true ? (L("Carga completa"), L("na tomada"))
            : b.Plugged == true ? (L("Conectada"), L("sem carregar"))
            : b.Plugged == false ? (b.Saver ? L("Economia de bateria") : L("Na bateria"), remaining is null ? L("tempo indisponível") : remaining + " " + L("restantes"))
            : (L("Alimentação"), L("desconhecida"));
    }

    // ---------- Cotas ----------
    private string _c5 = "—", _c7 = "—", _a5 = "—", _a7 = "—", _codexTip = string.Empty, _claudeTip = string.Empty;
    private ImageSource? _codexIcon, _claudeIcon;
    private Visibility _codexVisible = Visibility.Visible, _claudeVisible = Visibility.Visible;
    public string Codex5h { get => _c5; set => Set(ref _c5, value); }
    private string _gA = "—", _gB = "—", _gTip = "";
    private Visibility _gVisible = Visibility.Collapsed;
    /// <summary>Cotas do plano Gemini (gemini.google.com/usage): uso atual e semanal.</summary>
    public string GeminiCurrent { get => _gA; set => Set(ref _gA, value); }
    public string GeminiWeekly { get => _gB; set => Set(ref _gB, value); }
    public string GeminiQuotaTooltip { get => _gTip; set => Set(ref _gTip, value); }
    public Visibility GeminiQuotaVisible { get => _gVisible; set => Set(ref _gVisible, value); }
    private string _mcA = "—", _mcB = "—", _mcTip = "";
    private Visibility _mcVisible = Visibility.Collapsed;
    /// <summary>Créditos de IA do Copilot (assinatura Microsoft): "60/60" e porcentagem.</summary>
    public string CopilotCredits { get => _mcA; set => Set(ref _mcA, value); }
    public string CopilotCreditsPercent { get => _mcB; set => Set(ref _mcB, value); }
    public string CopilotQuotaTooltip { get => _mcTip; set => Set(ref _mcTip, value); }
    public Visibility CopilotQuotaVisible { get => _mcVisible; set => Set(ref _mcVisible, value); }
    private string _qA = "—", _qB = "—", _qTip = "";
    private Visibility _qVisible = Visibility.Collapsed;
    /// <summary>Créditos da nuvem do Lenovo Qira (modo Híbrido): "97/100" e porcentagem.</summary>
    public string QiraCredits { get => _qA; set => Set(ref _qA, value); }
    public string QiraCreditsPercent { get => _qB; set => Set(ref _qB, value); }
    public string QiraQuotaTooltip { get => _qTip; set => Set(ref _qTip, value); }
    public Visibility QiraQuotaVisible { get => _qVisible; set => Set(ref _qVisible, value); }
    public string Codex7d { get => _c7; set => Set(ref _c7, value); }
    public string Claude5h { get => _a5; set => Set(ref _a5, value); }
    public string Claude7d { get => _a7; set => Set(ref _a7, value); }
    public string CodexQuotaTooltip { get => _codexTip; set => Set(ref _codexTip, value); }
    public string ClaudeQuotaTooltip { get => _claudeTip; set => Set(ref _claudeTip, value); }
    public ImageSource? CodexIcon { get => _codexIcon; set => Set(ref _codexIcon, value); }
    public ImageSource? ClaudeIcon { get => _claudeIcon; set => Set(ref _claudeIcon, value); }
    private ImageSource? _localIcon;
    /// <summary>Ícone do servidor local detectado (ex.: Ollama), obtido do executável instalado.</summary>
    public ImageSource? LocalIcon { get => _localIcon; set => Set(ref _localIcon, value); }
    public Visibility CodexQuotaVisible { get => _codexVisible; set => Set(ref _codexVisible, value); }
    public Visibility ClaudeQuotaVisible { get => _claudeVisible; set => Set(ref _claudeVisible, value); }

    // ---------- Atalhos ----------
    private ImageSource? _tm, _settings, _store;
    public ImageSource? TaskManagerIcon { get => _tm; set => Set(ref _tm, value); }
    public ImageSource? SettingsIcon { get => _settings; set => Set(ref _settings, value); }
    public ImageSource? StoreIcon { get => _store; set => Set(ref _store, value); }

    // ---------- Atualizações ----------
    public void UpdateCpu(CpuSample s)
    {
        CpuText = $"{Formatters.GHz(s.CurrentGHz)}  {Formatters.Percent(s.UsagePercent)}";
        CpuTooltip = global::WinStatusBar.Services.Localization.F("{0}\n{1} processadores lógicos\nUtilização: {2}\n", new object?[] {s.ProcessorName,s.LogicalProcessors,Formatters.Percent(s.UsagePercent)})
                     + global::WinStatusBar.Services.Localization.F("Frequência atual: {0}\nFonte: {1}", new object?[] {Formatters.GHz(s.CurrentGHz),s.Source});
    }

    public void UpdateRam(RamSample s)
    {
        RamTitle = s.TotalBytes == 0 ? "RAM" : $"RAM {s.UsedPercent:0}%";
        RamText = s.TotalBytes == 0 ? "—" : $"{Formatters.Gb(s.UsedBytes)} / {Formatters.Gb(s.TotalBytes)} GB";
        RamTooltip = global::WinStatusBar.Services.Localization.F("Memória física em uso: {0} GB\nDisponível: {1} GB\n", new object?[] {Formatters.Gb(s.UsedBytes),Formatters.Gb(s.AvailableBytes)})
                     + global::WinStatusBar.Services.Localization.F("Utilizável pelo sistema: {0} GB", new object?[] {Formatters.Gb(s.TotalBytes)})
                     + (s.InstalledBytes > 0 ? $"\nInstalada: {Formatters.Gb(s.InstalledBytes)} GB" : string.Empty);
    }

    public void UpdateGpu(GpuSample s, AppSettings settings)
    {
        if (s.Adapter is null) { GpuText = global::WinStatusBar.Services.Localization.T("Indisponível"); GpuTooltip = global::WinStatusBar.Services.Localization.T("Nenhum adaptador DXGI encontrado"); return; }
        var a = s.Adapter;
        string mem, pct;
        if (a.IsIntegrated && s.SharedUsedBytes is not null && a.SharedSystemMemory > 0)
        {
            // GPU integrada (notebooks sem placa dedicada): a memória de vídeo real é a compartilhada com o sistema.
            double sharedPct = s.SharedUsedBytes.Value * 100.0 / a.SharedSystemMemory;
            mem = $"{Formatters.Gb(s.SharedUsedBytes.Value)} / {Formatters.Gb(a.SharedSystemMemory)} GB";
            pct = settings.GpuPercent == GpuPercentMode.Utilization
                ? (s.UtilizationPercent is null ? "(—)" : $"({Math.Round(s.UtilizationPercent.Value):0}% uso)")
                : $"({Math.Round(sharedPct):0}%)";
            GpuLabel = global::WinStatusBar.Services.Localization.T("GPU integrada");
        }
        else
        {
            mem = s.DedicatedUsedBytes is null ? $"— / {Formatters.Gb(a.DedicatedVideoMemory)} GB"
                : $"{Formatters.Gb(s.DedicatedUsedBytes.Value)} / {Formatters.Gb(a.DedicatedVideoMemory)} GB";
            pct = settings.GpuPercent == GpuPercentMode.Utilization
                ? (s.UtilizationPercent is null ? "(—)" : $"({Math.Round(s.UtilizationPercent.Value):0}% uso)")
                : (s.DedicatedPercent is null ? "(—)" : $"({Math.Round(s.DedicatedPercent.Value):0}%)");
            GpuLabel = global::WinStatusBar.Services.Localization.T("GPU");
        }
        GpuText = $"{mem} {pct}";
        if (settings.ShowSharedGpuMemory && s.SharedUsedBytes is not null) GpuText += $" · comp. {Formatters.Gb(s.SharedUsedBytes.Value)} GB";
        GpuTooltip = global::WinStatusBar.Services.Localization.F("{0}\nMemória dedicada: {1} / {2} GB", new object?[] {a.Name,(s.DedicatedUsedBytes is null ? "—" : Formatters.Gb(s.DedicatedUsedBytes.Value)),Formatters.Gb(a.DedicatedVideoMemory)})
                     + $" ({(s.DedicatedPercent is null ? "—" : Math.Round(s.DedicatedPercent.Value).ToString("0"))}%)"
                     + global::WinStatusBar.Services.Localization.F("\nMemória compartilhada: {0} / {1} GB", new object?[] {(s.SharedUsedBytes is null ? "—" : Formatters.Gb(s.SharedUsedBytes.Value)),Formatters.Gb(a.SharedSystemMemory)})
                     + global::WinStatusBar.Services.Localization.F("\nUtilização da GPU: {0}", new object?[] {(s.UtilizationPercent is null ? "—" : Math.Round(s.UtilizationPercent.Value) + "%")})
                     + (s.Adapters.Count > 1 ? "\nOutros adaptadores: " + string.Join(", ", s.Adapters.Where(x => x.Luid != a.Luid).Select(x => x.Name)) : string.Empty);
    }

    public void UpdateNetwork(NetworkSample s)
    {
        NetUp = Formatters.Speed(s.UploadBytesPerSec);
        NetDown = Formatters.Speed(s.DownloadBytesPerSec);
        var active = s.AllInterfaces.Where(n => s.ActiveInterfaces.Contains(n.Alias)).ToList();
        string kind;
        // Wi-Fi (IEEE 802.11 = 71) tem prioridade visual quando ativo; depois cabo (6); depois banda larga móvel (243/244).
        if (active.Any(n => n.Type == 71)) { NetGlyph = ""; kind = "Wi-Fi"; }
        else if (active.Any(n => n.Type == 6)) { NetGlyph = ""; kind = global::WinStatusBar.Services.Localization.T("Cabo (Ethernet)"); }
        else if (active.Any(n => n.Type is 243 or 244)) { NetGlyph = "\uEC05"; kind = global::WinStatusBar.Services.Localization.T("Banda larga móvel"); }
        else if (active.Count > 0) { NetGlyph = ""; kind = global::WinStatusBar.Services.Localization.T("Rede"); }
        else { NetGlyph = "\uE968"; kind = global::WinStatusBar.Services.Localization.T("Sem conexão"); }
        NetTooltip = $"{kind}\nUpload: {NetUp}\nDownload: {NetDown}\nInterfaces: " + (s.ActiveInterfaces.Count == 0 ? "nenhuma ativa" : string.Join(", ", s.ActiveInterfaces));
    }

    public void UpdateQuotas(QuotaSnapshot? codex, QuotaSnapshot? claude, AppSettings settings, string? codexError, string? claudeError = null)
    {
        var now = DateTimeOffset.Now;
        bool remaining = settings.QuotaMode == QuotaDisplay.Remaining;
        Codex5h = QuotaService.Text(codex?.Primary, remaining, now);
        Codex7d = QuotaService.Text(codex?.Secondary, remaining, now);
        Claude5h = QuotaService.Text(claude?.Primary, remaining, now);
        Claude7d = QuotaService.Text(claude?.Secondary, remaining, now);
        string mode = remaining ? "restante" : "usado";
        CodexQuotaTooltip = codex?.Primary is null && codex?.Secondary is null
            ? global::WinStatusBar.Services.Localization.T("Codex: cotas indisponíveis") + (codexError is null ? string.Empty : $"\n{codexError}")
            : global::WinStatusBar.Services.Localization.F("Codex — percentual {0}{1}\n", new object?[] {mode,(codex?.PlanType is null ? string.Empty : $" · plano {codex.PlanType}")})
              + QuotaLine("5h", codex?.Primary, remaining, now) + "\n" + QuotaLine("7d", codex?.Secondary, remaining, now)
              + global::WinStatusBar.Services.Localization.F("\nFonte: {0} · {1:HH:mm}", new object?[] {codex?.Primary?.Source ?? codex?.Secondary?.Source,codex?.ObservedAt.ToLocalTime()});
        ClaudeQuotaTooltip = claude?.Primary is null && claude?.Secondary is null
            ? global::WinStatusBar.Services.Localization.T("Claude: ") + (claudeError ?? global::WinStatusBar.Services.Localization.T("Aguardando a consulta automática da conta."))
            : global::WinStatusBar.Services.Localization.F("Claude — percentual {0}\n", new object?[] {mode}) + QuotaLine("5h", claude?.Primary, remaining, now) + "\n" + QuotaLine("7d", claude?.Secondary, remaining, now)
              + global::WinStatusBar.Services.Localization.F("\nÚltimo dado: {0:dd/MM HH:mm} · {1}", new object?[] {claude?.ObservedAt.ToLocalTime(),claude?.Primary?.Source ?? claude?.Secondary?.Source})
              + (claudeError is null ? "" : global::WinStatusBar.Services.Localization.F("\nAtualização pendente: {0}", new object?[] {claudeError}));
        CodexQuotaVisible = settings.CodexEnabled ? Visibility.Visible : Visibility.Collapsed;
        ClaudeQuotaVisible = settings.ClaudeEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string QuotaLine(string label, QuotaWindow? w, bool remaining, DateTimeOffset now) =>
        w is null ? global::WinStatusBar.Services.Localization.F("{0}: indisponível", new object?[] {label})
        : $"{label}: {QuotaService.Text(w, remaining, now)} ({Math.Round(w.UsedPercent)}% usado) — {Formatters.ResetTime(w.ResetsAt)}";
}
