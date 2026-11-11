using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>Claude no seletor do Codex: false = só texto (padrão); true = o Claude Code edita arquivos e executa comandos na pasta da conversa.</summary>
    public bool CodexClaudeCommands { get; set; } = false;
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
    /// <summary>Continuar pela API ligado (padrão): a seção no menu da cota, o acompanhamento no painel de tarefas e o serviço.</summary>
    public bool CodexApiResumeEnabled { get; set; } = true;
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
        ApiCredentialResource ??= "WinStatusBar.CommandApi"; ApiCredentialEndpoint ??= string.Empty;
        AllowedConnectors ??= new();
        CommandChatGptVoiceHotkey ??= string.Empty; CommandClaudeVoiceHotkey ??= string.Empty;
        CustomApis = (CustomApis ?? new()).Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Resource)).GroupBy(a => a.Resource).Select(g => g.First()).ToList();
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
public partial class AppSettingsContext : JsonSerializerContext;

public sealed class SettingsService
{
    /// <summary>Só para testes: pasta de dados no lugar de %LOCALAPPDATA%\WinStatusBar (os testes nunca tocam o perfil real).</summary>
    internal static string? DataDirOverride { get; set; }
    /// <summary>Só para testes: cópia do OneDrive no lugar da real (pode devolver null = sem OneDrive pessoal).</summary>
    internal static Func<string?>? CloudSettingsPathOverride { get; set; }

    public static string DataDir => DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string LogPath => Path.Combine(DataDir, "winstatusbar.log");

    public AppSettings Current { get; private set; } = new();
    public event EventHandler? Changed;

    /// <summary>Cópia das preferências no OneDrive pessoal (nunca no da empresa); null sem OneDrive pessoal.</summary>
    public static string? CloudSettingsPath =>
        CloudSettingsPathOverride is { } custom ? custom()
        : OneDrivePaths.Personal is { } od ? Path.Combine(od, "WinStatusBar", "configuracoes.json") : null;

    /// <summary>Preferências restauradas do OneDrive nesta abertura (instalação limpa).</summary>
    public bool RestoredFromCloud { get; private set; }

    /// <summary>
    /// Instalação limpa sem backup encontrado ainda: o OneDrive pode estar sincronizando (comum logo após formatar).
    /// Enquanto durar a espera, nada é gravado no OneDrive — antes, os padrões eram gravados por cima do backup.
    /// </summary>
    public bool PendingRestore => _pendingUntil is { } until && DateTime.UtcNow < until;
    private DateTime? _pendingUntil;
    public static readonly TimeSpan RestoreWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// O que aconteceu na última leitura quando o settings.json existia mas não pôde ser lido (para o log do app);
    /// null numa leitura normal.
    /// </summary>
    public static string? LoadNote { get; private set; }

    /// <summary>Uma gravação por vez: Save é chamado da thread de UI e do pool de threads, com o mesmo arquivo temporário.</summary>
    private static readonly Lock SaveGate = new();
    /// <summary>O settings.json em disco é confiável (lido sem erro ou gravado por este processo): pode virar o .bak.</summary>
    private bool _diskGood;
    /// <summary>O arquivo estava ilegível e não havia cópia: os padrões em uso não podem apagar a cópia do OneDrive sem guardá-la.</summary>
    private bool _recoveredWithDefaults;
    /// <summary>A cópia do OneDrive existe mas não pôde ser lida (baixando, incompleta ou danificada).</summary>
    private bool _cloudUnreadable;

    public AppSettings Load()
    {
        LoadNote = null;
        string path = SettingsPath;
        if (!File.Exists(path))
        {
            try
            {
                if (TryRestoreFromCloud()) return Current;
                _pendingUntil = DateTime.UtcNow + RestoreWait; // aguarda o backup aparecer (TryLateRestore)
            }
            catch { Current = new AppSettings(); }
            Current.Normalize();
            return Current;
        }

        string? text = null;
        AppSettings? loaded = null;
        string reason = "conteúdo vazio";
        try
        {
            text = ReadAllTextWithRetry(path);
            loaded = JsonSerializer.Deserialize(text, AppSettingsContext.Default.AppSettings);
        }
        catch (Exception ex) { reason = ex.GetType().Name; }
        if (loaded is null) return Recover(path, text, reason);
        Current = loaded;
        _diskGood = true;
        Current.Normalize();
        return Current;
    }

    /// <summary>Outro processo pode estar com o arquivo aberto por um instante (antivírus, sincronização): tenta mais uma vez.</summary>
    private static string ReadAllTextWithRetry(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { Thread.Sleep(150); return File.ReadAllText(path); }
    }

    /// <summary>
    /// O settings.json existe mas não pôde ser lido (truncado, em uso, valor desconhecido). Antes, isso zerava todas as
    /// preferências em silêncio e a gravação seguinte levava os padrões para o disco e para o OneDrive. Agora: guarda
    /// uma cópia do arquivo ruim e tenta, nesta ordem, aproveitar o próprio arquivo (sem a opção inválida), o
    /// settings.json.bak, a cópia do OneDrive e, só então, os padrões — sem gravar no OneDrive durante a espera.
    /// </summary>
    private AppSettings Recover(string path, string? text, string reason)
    {
        _diskGood = false; // o arquivo ruim não pode virar o .bak
        string note = $"settings.json ilegível ({reason})";
        try
        {
            string kept = Path.Combine(DataDir, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, kept, overwrite: true);
            note += "; cópia guardada em " + Path.GetFileName(kept);
            PruneCorruptCopies();
        }
        catch { note += "; não foi possível guardar uma cópia dele"; }

        if (text is not null && Salvage(text, out string dropped) is { } salvaged)
        {
            Current = salvaged;
            Current.Normalize();
            try { Save(salvaged); } catch { }
            LoadNote = note + "; preferências mantidas, menos: " + dropped;
            return Current;
        }

        try
        {
            if (JsonSerializer.Deserialize(ReadAllTextWithRetry(path + ".bak"), AppSettingsContext.Default.AppSettings) is { } bak)
            {
                Current = bak;
                Current.Normalize();
                try { Save(bak); } catch { }
                LoadNote = note + "; preferências restauradas de settings.json.bak";
                return Current;
            }
        }
        catch { }

        try
        {
            if (TryRestoreFromCloud()) { LoadNote = note + "; preferências restauradas da cópia do OneDrive"; return Current; }
        }
        catch { }

        Current = new AppSettings();
        Current.Normalize();
        _recoveredWithDefaults = true;
        _pendingUntil = DateTime.UtcNow + RestoreWait; // a cópia do OneDrive pode estar baixando: TryLateRestore
        LoadNote = note + "; sem cópia utilizável: padrões aplicados, aguardando a cópia do OneDrive (nada é gravado nela até lá)";
        return Current;
    }

    /// <summary>
    /// JSON bem formado em que uma opção tem valor que esta versão não entende (ex.: nome de enum desconhecido):
    /// lê sem essa opção, que volta ao padrão, em vez de perder todas as outras.
    /// </summary>
    private static AppSettings? Salvage(string text, out string dropped)
    {
        dropped = string.Empty;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject obj) return null;
            var names = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                try
                {
                    var settings = obj.Deserialize(AppSettingsContext.Default.AppSettings);
                    dropped = string.Join(", ", names);
                    return settings;
                }
                catch (JsonException ex)
                {
                    if (TopLevelProperty(ex.Path) is not { } name || !obj.Remove(name)) return null;
                    names.Add(name);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>"$.Backdrop" → "Backdrop"; "$.CustomApis[0].Kind" → "CustomApis"; outro formato → null.</summary>
    private static string? TopLevelProperty(string? jsonPath)
    {
        if (jsonPath is null || !jsonPath.StartsWith("$.", StringComparison.Ordinal)) return null;
        int end = jsonPath.IndexOfAny(['.', '['], 2);
        string name = end < 0 ? jsonPath[2..] : jsonPath[2..end];
        return name.Length > 0 ? name : null;
    }

    /// <summary>Fica só com as 5 cópias mais novas de arquivos ilegíveis (settings.corrupt-*.json).</summary>
    private static void PruneCorruptCopies()
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(DataDir, "settings.corrupt-*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Skip(5).ToList())
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch { }
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
            // Primeira cópia no OneDrive. Se lá existe um arquivo que não pôde ser lido, ou se os padrões em uso vieram
            // de um settings.json ilegível, SaveCloudCopy guarda antes uma cópia com data do que está lá.
            try { Save(); } catch { }
        }
        return false;
    }

    /// <summary>Só para testes: encerra a espera de restauração como se o prazo tivesse passado.</summary>
    internal void ExpireRestoreWait() { if (_pendingUntil is not null) _pendingUntil = DateTime.UtcNow.AddSeconds(-1); }

    private bool TryRestoreFromCloud()
    {
        // Instalação limpa na mesma conta: volta com as preferências guardadas no OneDrive.
        // Em outro PC/conta, o OneDrive é o da pessoa: sem cópia, começa com os padrões.
        if (CloudSettingsPath is not { } cloud || !File.Exists(cloud)) return false;
        AppSettings? restored;
        try
        {
            restored = JsonSerializer.Deserialize(File.ReadAllText(cloud), AppSettingsContext.Default.AppSettings);
            _cloudUnreadable = restored is null;
        }
        catch { _cloudUnreadable = true; return false; } // arquivo ainda baixando/incompleto: tenta de novo depois
        if (restored is not { SyncSettings: true }) return false;
        restored.NetworkInterface = null; // placa de rede e GPU são deste hardware: voltam ao automático
        restored.GpuAdapter = null;
        RestoredFromCloud = true;
        _pendingUntil = null;
        _recoveredWithDefaults = false; // o que vai para o OneDrive agora é a própria cópia de lá
        Save(restored);
        return true;
    }

    public void Save() => Save(Current);

    public void Save(AppSettings settings)
    {
        lock (SaveGate)
        {
            settings.Normalize();
            Directory.CreateDirectory(DataDir);
            string path = SettingsPath;
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(settings, AppSettingsContext.Default.AppSettings);
            // Mesmo conteúdo de File.WriteAllText (UTF-8 sem BOM), mas descarregado no disco antes da troca:
            // uma queda de energia não deixa um settings.json vazio ou pela metade.
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            // Cópia (nunca renomear: outros processos leem o settings.json) da última versão boa, para Load se recuperar.
            if (_diskGood)
            {
                try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { }
            }
            MoveWithRetry(tmp, path);
            _diskGood = true;
            Current = settings;
            if (settings.SyncSettings && !PendingRestore) SaveCloudCopy(json);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Troca atômica; se o destino estiver aberto por outro processo por um instante, tenta mais duas vezes.</summary>
    private static void MoveWithRetry(string tmp, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    /// <summary>Cópia no OneDrive (sem segredos: a chave da API fica no Cofre de Credenciais do Windows).</summary>
    private void SaveCloudCopy(string json)
    {
        try
        {
            if (CloudSettingsPath is not { } cloud) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
            // O que está no OneDrive pode ser a única cópia boa das preferências (settings.json local ilegível e padrões
            // em uso) ou um arquivo que não deu para ler: nunca gravar por cima sem antes guardar uma cópia com data.
            // Se a cópia falhar, a exceção sai daqui e o arquivo do OneDrive fica como está.
            if ((_recoveredWithDefaults || _cloudUnreadable) && File.Exists(cloud))
            {
                string kept = Path.Combine(Path.GetDirectoryName(cloud)!,
                    $"{Path.GetFileNameWithoutExtension(cloud)}.copia-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(cloud)}");
                File.Copy(cloud, kept, overwrite: true);
            }
            _recoveredWithDefaults = false;
            _cloudUnreadable = false;
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
