using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinStatusBar.Core.Settings;

public enum ThemeMode { System, Light, Dark }
public enum BackdropKind { Mica, MicaAlt, Acrylic, Solid, Adaptive }
public enum MonitorMode { Primary, All }
public enum FullscreenBehavior { HideBehind, StayOnTop }
public enum QuotaDisplay { Remaining, Used }
public enum GpuPercentMode { Vram, Utilization }

public sealed class AppSettings
{
    public string? Language { get; set; }

    // Aparência
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool EstimateProgressWithoutPlan { get; set; } = true;
    public BackdropKind Backdrop { get; set; } = BackdropKind.MicaAlt;
    public double TintOpacity { get; set; } = 0.0; // 0 = efeito padrão do sistema
    public double Scale { get; set; } = 1.0;
    public List<string> ModuleOrder { get; set; } = new(DefaultOrder);
    public List<string> ShortcutOrder { get; set; } = new(DefaultShortcutOrder);
    public static readonly string[] DefaultShortcutOrder = ["clipboard", "taskmanager", "settings", "store", "explorer", "documents", "downloads", "personal"];
    public HashSet<string> HiddenModules { get; set; } = new();

    // Sistema
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Histórico da área de transferência e emojis integrados (Win+V / Win+.).</summary>
    public bool ClipboardEnabled { get; set; } = true;
    /// <summary>Win+V abre o histórico do WinStatusBar (desligado: abre o painel nativo do Windows).</summary>
    public bool ClipboardWinV { get; set; } = true;
    /// <summary>Win+. e Win+; abrem os emojis do WinStatusBar (desligado: painel nativo do Windows).</summary>
    public bool ClipboardEmojiKeys { get; set; } = true;
    /// <summary>Ao escolher um item, cola no app que estava em uso (desligado: só copia).</summary>
    public bool ClipboardAutoPaste { get; set; } = true;
    public bool ClipboardPaused { get; set; }
    public bool ClipboardCaptureImages { get; set; } = true;
    public bool ClipboardCaptureFiles { get; set; } = true;
    /// <summary>Imagens maiores que isto (PNG) não são guardadas. 0 = sem limite.</summary>
    public int ClipboardMaxImageMb { get; set; } = 20;
    /// <summary>Cópia de segurança dos arquivos copiados (para colar mesmo se o original sumir). 0 = desligada.</summary>
    public int ClipboardBackupLimitMb { get; set; } = 25;
    /// <summary>Processos cujas cópias nunca entram no histórico (ex.: gerenciadores de senha), separados por vírgula.</summary>
    public string ClipboardIgnoredApps { get; set; } = "KeePass, KeePassXC, 1Password, Bitwarden";
    /// <summary>Limites do histórico (itens fixados nunca são apagados). 0 = sem limite.</summary>
    public int ClipboardMaxItems { get; set; }
    public int ClipboardMaxAgeDays { get; set; }
    public int ClipboardMaxStorageMb { get; set; }
    /// <summary>Brilho que percorre o contorno do módulo do agente enquanto ele trabalha.</summary>
    public bool AgentGlow { get; set; } = true;
    /// <summary>Linha sutil na base da barra no fundo adaptativo com janela maximizada.</summary>
    public bool AdaptiveDivider { get; set; } = true;
    /// <summary>Clique em CPU, RAM, GPU e rede abre o painel de detalhes (desligado: só a dica ao passar o mouse).</summary>
    public bool DetailPanels { get; set; } = true;
    /// <summary>Lista de aplicativos abertos no painel da CPU.</summary>
    public bool CpuPanelApps { get; set; } = true;
    /// <summary>Botão "Finalizar tarefa" na lista de aplicativos.</summary>
    public bool AllowEndTask { get; set; } = true;
    /// <summary>Teste de velocidade no painel de rede (usa os servidores públicos da Cloudflare só quando clicado).</summary>
    public bool SpeedTestEnabled { get; set; } = true;
    /// <summary>Botões de atalho à direita da barra.</summary>
    public bool ShortcutClipboard { get; set; } = true;
    public bool ShortcutTaskManager { get; set; } = true;
    public bool ShortcutSettings { get; set; } = true;
    public bool ShortcutStore { get; set; } = true;
    public bool ShortcutExplorer { get; set; } = true;
    public bool ShortcutDocuments { get; set; } = true;
    public bool ShortcutDownloads { get; set; } = true;
    public bool ShortcutPersonal { get; set; } = true;

    // Painéis e integrações recentes
    public bool ApiUsageVisible { get; set; } = true;
    public int ApiUsageRefreshMinutes { get; set; } = 5;
    public bool ApiShowCosts { get; set; } = true;
    public bool ApiShowTokens { get; set; } = true;
    public bool ApiShowRequests { get; set; } = true;
    public bool NetworkConnectionsVisible { get; set; } = true;
    public bool ContextActionsEnabled { get; set; } = true;
    public int BatteryHistoryDays { get; set; } = 1;
    public bool BatteryPowerControlsVisible { get; set; } = true;

    // Barra de comando da IA
    public bool CommandBar { get; set; } = true;
    public bool TaskBriefingEnabled { get; set; } = true;
    public int TaskBriefingIntervalSeconds { get; set; } = 30;
    /// <summary>Destino escolhido no balão: "claude", "codex", "local" ou "api" (vazio = segue o agente da barra).</summary>
    public string CommandTarget { get; set; } = string.Empty;
    /// <summary>Autenticação do Gemini nas conversas da barra: "login" (conta Google do CLI) ou "api" (chave do AI Studio).</summary>
    public string GeminiAuth { get; set; } = "login";
    /// <summary>Ordem das IAs no menu de agentes e nas cotas da barra (arrastável nas Configurações).</summary>
    public List<string> AiOrder { get; set; } = new(DefaultAiOrder);
    /// <summary>IAs com módulo de cota na barra, na ordem de <see cref="AiOrder"/>.</summary>
    public List<string> BarQuotaServices { get; set; } = new() { "codex", "claude" };
    public static readonly string[] DefaultAiOrder = ["codex", "claude", "gemini", "copilot", "local", "deepseek", "grok", "qira"];
    /// <summary>IAs que têm cota em porcentagem para mostrar na barra.</summary>
    public static readonly string[] QuotaCapable = ["codex", "claude", "gemini", "copilot", "qira"];
    /// <summary>Projeto do Google Cloud do login do Gemini CLI (contas Code Assist Standard/Enterprise). Vazio: não usa.</summary>
    public string GeminiCloudProject { get; set; } = string.Empty;
    /// <summary>Modelo e esforço escolhidos por destino (vazio = o configurado no app principal).</summary>
    public Dictionary<string, string> CommandModels { get; set; } = new();
    public Dictionary<string, string> CommandEfforts { get; set; } = new();
    /// <summary>Projeto escolhido por agente ("nome|pasta1;pasta2"; ausente = sem projeto).</summary>
    public Dictionary<string, string> CommandProjects { get; set; } = new();
    /// <summary>Registra as conversas do Claude Code no histórico do app Claude.</summary>
    public bool CommandClaudeHistory { get; set; } = true;
    /// <summary>Pesquisa na web ligada por padrão em cada conversa (Claude Code e Codex).</summary>
    public bool CommandWebDefault { get; set; } = true;
    public bool CommandAllowEditsDefault { get; set; } = false;
    public bool CommandDetectActiveFile { get; set; } = true;
    public string CommandChatGptVoiceHotkey { get; set; } = string.Empty;
    public string CommandClaudeVoiceHotkey { get; set; } = string.Empty;
    /// <summary>Guarda as conversas da barra de comando neste PC (histórico universal).</summary>
    public bool CommandSaveHistory { get; set; } = true;
    /// <summary>Pede respostas organizadas (título, passos, nota) na primeira mensagem de cada conversa.</summary>
    public bool CommandFormatHint { get; set; } = true;
    /// <summary>Conectores do Claude liberados para sempre no chat da barra (regras "mcp__servidor").</summary>
    public List<string> AllowedConnectors { get; set; } = new();
    /// <summary>
    /// Acesso total a plugins e conectores de todas as IAs no chat da barra (diretriz do usuário de 07/11/2026):
    /// conectores do Claude liberados automaticamente, aprovações de ferramentas de plugins do Codex e do Qira aceitas.
    /// Não libera comandos no PC nem edição de arquivos (isso continua com o modo Editar).
    /// </summary>
    public bool ConnectorsFullAccess { get; set; } = true;
    /// <summary>Pasta do histórico (vazio = OneDrive\WinStatusBar\Conversas, ou a pasta local se não houver OneDrive).</summary>
    public string CommandHistoryFolder { get; set; } = string.Empty;
    /// <summary>Guarda uma cópia das preferências no OneDrive pessoal e restaura numa instalação limpa (mesma conta).</summary>
    public bool SyncSettings { get; set; } = true;
    /// <summary>Atalho global que abre a barra de comando com o cursor nela (vazio = desligado).</summary>
    public string CommandHotkey { get; set; } = "Alt+Space";
    /// <summary>API própria: "openai" (compatível com OpenAI) ou "anthropic". A chave fica no Cofre de Credenciais do Windows.</summary>
    public string ApiKind { get; set; } = "openai";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiName { get; set; } = string.Empty;
    public string ApiDefaultModel { get; set; } = string.Empty;
    public string ApiCredentialResource { get; set; } = "WinStatusBar.CommandApi";
    public string ApiCredentialEndpoint { get; set; } = string.Empty;
    /// <summary>APIs próprias cadastradas (várias). A ativa fica espelhada nos campos Api* acima; cada uma tem a própria chave no Cofre.</summary>
    public List<CustomApi> CustomApis { get; set; } = new();
    public MonitorMode Monitors { get; set; } = MonitorMode.Primary;
    public int MetricsIntervalMs { get; set; } = 1000;
    public int GpuIntervalMs { get; set; } = 2000;
    public FullscreenBehavior Fullscreen { get; set; } = FullscreenBehavior.HideBehind;
    public bool Notifications { get; set; } = true;
    public string? NetworkInterface { get; set; }

    // IA
    public bool CodexEnabled { get; set; } = true;
    public bool ClaudeEnabled { get; set; } = true;
    public bool LocalEnabled { get; set; } = true;
    public bool QiraEnabled { get; set; } = true;
    /// <summary>
    /// Quanto tempo o modelo da IA local (Ollama) fica carregado depois do último uso: keep_alive enviado ao Ollama
    /// ("5m", "1h", "0" = libera logo após responder, "-1" = sempre carregado).
    /// </summary>
    public string LocalModelKeepAlive { get; set; } = WinStatusBar.Core.Ai.LocalKeepAlive.DefaultValue;
    /// <summary>Andamento de conversas no ChatGPT e no Claude (chat), lido da janela dos aplicativos.</summary>
    public bool ApiActivityEnabled { get; set; } = true;
    public List<string> DisabledApiActivityProviders { get; set; } = new();
    public bool ChatActivityEnabled { get; set; } = true;
    /// <summary>Nomes de processo adicionais tratados como servidores de inferência local (ex.: "meu-servidor").</summary>
    public List<string> LocalServerProcesses { get; set; } = new();
    public QuotaDisplay QuotaMode { get; set; } = QuotaDisplay.Remaining;
    public bool ShowTask { get; set; } = true;
    public string? PinnedSession { get; set; } // "auto" | "codex" | "claude" | id
    public bool PrivacyMode { get; set; }
    /// <summary>Minutos entre consultas oficiais das cotas do Codex; 0 = tempo real (eventos + consulta a cada minuto).</summary>
    public int CodexQuotaIntervalMin { get; set; } = 0;
    /// <summary>Continuar pela API: false = uma de cada vez (fila, padrão); true = todas ao mesmo tempo. Sem limite de gasto.</summary>
    public bool CodexApiParallel { get; set; } = false;
    /// <summary>Minutos entre consultas da conta Claude; 0 = tempo real (statusLine + consulta a cada minuto, respeitando Retry-After).</summary>
    public int ClaudeQuotaIntervalMin { get; set; } = 0;
    /// <summary>O usuário removeu a integração com o Claude Code: não reinstalar automaticamente.</summary>
    public bool ClaudeIntegrationDeclined { get; set; }
    /// <summary>Versão do esquema de configurações (migrações de padrões).</summary>
    public int SchemaVersion { get; set; }

    public static TimeSpan QuotaPoll(int minutes) => minutes <= 0 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(minutes);
    /// <summary>
    /// Gemini, Copilot e Qira são lidos nas páginas das próprias contas: no mínimo a cada 5 min ("Tempo real" incluído),
    /// para não sobrecarregar os serviços. Segue a mesma escolha única de atualização das cotas.
    /// </summary>
    public static TimeSpan WebQuotaInterval(int minutes) => TimeSpan.FromMinutes(Math.Max(5, minutes));

    public bool ShowTemperatures { get; set; } = true;

    // GPU
    public string? GpuAdapter { get; set; }
    public bool ShowSharedGpuMemory { get; set; }
    public GpuPercentMode GpuPercent { get; set; } = GpuPercentMode.Vram;
    public bool GpuUtilization { get; set; } = true;

    public static readonly string[] DefaultOrder = ["agent", "quotas", "task", "network", "cpu", "ram", "gpu", "battery", "shortcuts"];

    public static readonly Dictionary<string, string> ModuleNames = new()
    {
        ["agent"] = global::WinStatusBar.Core.LocalizedText.T("Agente de IA"), ["task"] = global::WinStatusBar.Core.LocalizedText.T("Tarefa atual"), ["network"] = global::WinStatusBar.Core.LocalizedText.T("Rede"), ["cpu"] = global::WinStatusBar.Core.LocalizedText.T("CPU"),
        ["battery"] = global::WinStatusBar.Core.LocalizedText.T("Bateria"), ["ram"] = global::WinStatusBar.Core.LocalizedText.T("RAM"), ["gpu"] = global::WinStatusBar.Core.LocalizedText.T("GPU"), ["quotas"] = global::WinStatusBar.Core.LocalizedText.T("Cotas de IA"), ["shortcuts"] = global::WinStatusBar.Core.LocalizedText.T("Atalhos"),
    };

    public void Normalize()
    {
        ShortcutOrder = (ShortcutOrder ?? []).Where(DefaultShortcutOrder.Contains).Distinct().ToList();
        foreach (var key in DefaultShortcutOrder) if (!ShortcutOrder.Contains(key)) ShortcutOrder.Add(key);
        ModuleOrder = (ModuleOrder ?? []).Where(DefaultOrder.Contains).Distinct().ToList();
        HiddenModules ??= new();
        LocalServerProcesses ??= new();
        DisabledApiActivityProviders ??= new();
        LocalModelKeepAlive = WinStatusBar.Core.Ai.LocalKeepAlive.Normalize(LocalModelKeepAlive);
        foreach (var m in DefaultOrder)
            if (!ModuleOrder.Contains(m))
            {
                int before = m == "battery" ? ModuleOrder.IndexOf("shortcuts") : -1;
                if (before >= 0) ModuleOrder.Insert(before, m); else ModuleOrder.Add(m);
            }
        ApiUsageRefreshMinutes = Math.Clamp(ApiUsageRefreshMinutes, 1, 60);
        BatteryHistoryDays = BatteryHistoryDays == 7 ? 7 : 1;
        Scale = Math.Clamp(Scale, 0.8, 1.5);
        TaskBriefingIntervalSeconds = Math.Clamp(TaskBriefingIntervalSeconds, 15, 300);
        TintOpacity = Math.Clamp(TintOpacity, 0, 1);
        MetricsIntervalMs = Math.Clamp(MetricsIntervalMs, 500, 10000);
        GpuIntervalMs = Math.Clamp(GpuIntervalMs, 1000, 10000);
        // Migração v2: cotas passam a ser em tempo real por padrão (antes: 10 min fixos no Codex, 5 min no Claude).
        if (SchemaVersion < 2) { CodexQuotaIntervalMin = 0; ClaudeQuotaIntervalMin = 0; SchemaVersion = 2; }
        // Ordem solicitada para instalações existentes e novas; alterações posteriores continuam permitidas.
        if (SchemaVersion < 3) { ModuleOrder = new(DefaultOrder); SchemaVersion = 3; }
        // Política do usuário (07/11/2026): acesso total a plugins e conectores de todas as IAs. Aplicada uma vez;
        // Editar e acesso amplo ao PC não mudam.
        if (SchemaVersion < 4) { ConnectorsFullAccess = true; SchemaVersion = 4; }
        CodexQuotaIntervalMin = Math.Clamp(CodexQuotaIntervalMin, 0, 120);
        ClaudeQuotaIntervalMin = Math.Clamp(ClaudeQuotaIntervalMin, 0, 120);
        ClipboardMaxImageMb = Math.Clamp(ClipboardMaxImageMb, 0, 1024);
        ClipboardBackupLimitMb = Math.Clamp(ClipboardBackupLimitMb, 0, 4096);
        ClipboardMaxItems = Math.Max(0, ClipboardMaxItems);
        ClipboardMaxAgeDays = Math.Max(0, ClipboardMaxAgeDays);
        ClipboardMaxStorageMb = Math.Max(0, ClipboardMaxStorageMb);
        ClipboardIgnoredApps ??= string.Empty;
        CommandTarget ??= string.Empty;
        GeminiAuth = GeminiAuth == "api" ? "api" : "login";
        AiOrder = (AiOrder ?? []).Where(DefaultAiOrder.Contains).Distinct().Concat(DefaultAiOrder).Distinct().ToList();
        BarQuotaServices = (BarQuotaServices ?? []).Where(QuotaCapable.Contains).Distinct().ToList();
        GeminiCloudProject = (GeminiCloudProject ?? string.Empty).Trim();
        CommandHotkey ??= string.Empty;
        CommandHistoryFolder ??= string.Empty;
        CommandModels ??= new(); CommandEfforts ??= new(); CommandProjects ??= new();
        // Qira só no modo Híbrido (08/11/2026): a escolha antiga "Local" volta ao padrão. O modelo local é a IA local (Ollama).
        if (CommandModels.TryGetValue("qira", out var qiraModel) && qiraModel != "hybrid") CommandModels.Remove("qira");
        ApiKind = ApiKind == "anthropic" ? "anthropic" : "openai";
        ApiBaseUrl ??= string.Empty; ApiName ??= string.Empty; ApiDefaultModel ??= string.Empty;
        CustomApis = (CustomApis ?? new()).Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Resource)).GroupBy(a => a.Resource).Select(g => g.First()).ToList();
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
public partial class AppSettingsContext : JsonSerializerContext;

public sealed class SettingsService
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string LogPath => Path.Combine(DataDir, "winstatusbar.log");

    public AppSettings Current { get; private set; } = new();
    public event EventHandler? Changed;

    /// <summary>Cópia das preferências no OneDrive pessoal (nunca no da empresa); null sem OneDrive pessoal.</summary>
    public static string? CloudSettingsPath =>
        OneDrivePaths.Personal is { } od ? Path.Combine(od, "WinStatusBar", "configuracoes.json") : null;

    /// <summary>Preferências restauradas do OneDrive nesta abertura (instalação limpa).</summary>
    public bool RestoredFromCloud { get; private set; }

    /// <summary>
    /// Instalação limpa sem backup encontrado ainda: o OneDrive pode estar sincronizando (comum logo após formatar).
    /// Enquanto durar a espera, nada é gravado no OneDrive — antes, os padrões eram gravados por cima do backup.
    /// </summary>
    public bool PendingRestore => _pendingUntil is { } until && DateTime.UtcNow < until;
    private DateTime? _pendingUntil;
    public static readonly TimeSpan RestoreWait = TimeSpan.FromMinutes(30);

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Current = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), AppSettingsContext.Default.AppSettings) ?? new AppSettings();
            else if (TryRestoreFromCloud()) return Current;
            else _pendingUntil = DateTime.UtcNow + RestoreWait; // aguarda o backup aparecer (TryLateRestore)
        }
        catch { Current = new AppSettings(); }
        Current.Normalize();
        return Current;
    }

    /// <summary>
    /// Durante a espera de restauração: se o backup do OneDrive apareceu, aplica-o (Changed avisa a barra).
    /// Devolve true quando restaurou. Ao fim da espera sem backup, grava a primeira cópia.
    /// </summary>
    public bool TryLateRestore()
    {
        if (_pendingUntil is null) return false;
        if (TryRestoreFromCloud()) { _pendingUntil = null; return true; }
        if (!PendingRestore)
        {
            _pendingUntil = null;
            try { Save(); } catch { } // primeira cópia no OneDrive, agora que não há backup a preservar
        }
        return false;
    }

    private bool TryRestoreFromCloud()
    {
        // Instalação limpa na mesma conta: volta com as preferências guardadas no OneDrive.
        // Em outro PC/conta, o OneDrive é o da pessoa: sem cópia, começa com os padrões.
        if (CloudSettingsPath is not { } cloud || !File.Exists(cloud)) return false;
        AppSettings? restored;
        try { restored = JsonSerializer.Deserialize(File.ReadAllText(cloud), AppSettingsContext.Default.AppSettings); }
        catch { return false; } // arquivo ainda baixando/incompleto: tenta de novo depois
        if (restored is not { SyncSettings: true }) return false;
        restored.NetworkInterface = null; // placa de rede e GPU são deste hardware: voltam ao automático
        restored.GpuAdapter = null;
        RestoredFromCloud = true;
        _pendingUntil = null;
        Save(restored);
        return true;
    }

    public void Save() => Save(Current);

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DataDir);
        string tmp = SettingsPath + ".tmp";
        string json = JsonSerializer.Serialize(settings, AppSettingsContext.Default.AppSettings);
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);
        Current = settings;
        if (settings.SyncSettings && !PendingRestore) SaveCloudCopy(json);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cópia no OneDrive (sem segredos: a chave da API fica no Cofre de Credenciais do Windows).</summary>
    private static void SaveCloudCopy(string json)
    {
        try
        {
            if (CloudSettingsPath is not { } cloud) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
            string tmp = cloud + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, cloud, overwrite: true);
        }
        catch { }
    }
}

/// <summary>Uma API própria cadastrada (compatível com OpenAI ou Anthropic). A chave fica no Cofre, no recurso <see cref="Resource"/>.</summary>
public sealed class CustomApi
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "openai";
    public string BaseUrl { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public string Resource { get; set; } = "WinStatusBar.CommandApi";
}
