using System.Text.Json.Serialization;
namespace WinStatusBar.ExecutorExpansion;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Implementation { Implemented, RequiresAdapter, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationLevel { CompiledOnly, ContractTested, NativeReadTested, ApplicationTested }
public sealed record Capability(string Id, string Domain, string[] Operations, Implementation State,
    string Method, string Requirements, string Verification, string Source,
    VerificationLevel Verified = VerificationLevel.CompiledOnly);

/// <summary>Coverage declarations, not an authorization whitelist and not a claim of successful live testing.</summary>
public static class CapabilityCatalog
{
    public static readonly IReadOnlyList<Capability> All = Build();
    private static Capability[] Build()
    {
        var all = new List<Capability>
        {
            new("network.mapping", "Windows", ["list", "connect", "reconnect", "disconnect", "diagnose"], Implementation.Implemented,
                "MPR Win32; system/network_*", "Mesma sessão Windows do usuário; credencial nativa sob pedido",
                "WNetGetConnection + perfil HKCU; diagnóstico DNS/TCP não prova acesso SMB", "src/NetworkMappings.cs"),
            new("applications.functional", "Windows", ["discover", "query"], Implementation.Implemented,
                "Registry + StartApps + Appx + paths portáteis", "PowerShell Windows e leitura do registro",
                "Identidade registrada/executável existente; revisar candidatos e exclusões", "src/ApplicationDiscovery.cs"),
            new("explorer.file_extensions", "Windows", ["query", "configure"], Implementation.Implemented,
                "system/setting_read|setting_apply", "Windows build >=19041; HKCU; políticas podem bloquear",
                "Valor persistido relido; efeito visual não confirmado", "src/WindowsSettings.cs"),
            new("explorer.hidden_files", "Windows", ["query", "configure"], Implementation.Implemented,
                "system/setting_read|setting_apply", "Windows build >=19041; HKCU",
                "Valor persistido relido; políticas desconhecidas podem prevalecer", "src/WindowsSettings.cs"),
            new("power.active_plan", "Windows", ["query", "configure"], Implementation.Implemented,
                "powercfg get/list/setactive", "Plano existente; edição/hardware/política aplicáveis",
                "GUID ativo relido", "src/WindowsSettings.cs"),
            new("office.word", "Office", ["query", "format", "insert_image"], Implementation.Implemented,
                "COM/STA/ROT; office word state|format|insert_image", "Word desktop aberto na mesma sessão; documento não somente leitura",
                "Formatação, contagem e dimensões relidas; não salva/fecha", "src/OfficeExpansion.cs"),
            new("office.excel", "Office", ["query", "format"], Implementation.Implemented,
                "COM/STA/ROT; office excel state|format", "Excel desktop aberto; intervalo <=10000 células",
                "Propriedades relidas; AutoFit é comando conferido sem QA visual", "src/OfficeExpansion.cs"),
            new("dynamo.graph.inspect", "Dynamo", ["discover", "query", "diagnose"], Implementation.Implemented,
                "system/dynamo_inspect; leitura .dyn JSON", "Arquivo <=20 MiB; nenhum Dynamo/Revit é iniciado",
                "IDs, engines, packages, bindings e inconsistências extraídos; compatibilidade não inferida", "src/DynamoGraphInspection.cs"),
            new("revit.in_process", "Revit", ["query", "configure", "export"], Implementation.Implemented,
                "ExternalEvent, Transaction e pipe local autenticado", "Add-in precisa ser carregado pelo usuário; não instalado nesta tarefa",
                "Base simples compilada sem avisos; transporte testado com host simulado, sem modelo real", "Revit2027/RevitBridge.cs")
        };
        Add("Windows", """
            display.monitors|display.resolution|display.hdr|display.scaling|display.projection|audio.devices|audio.volume|audio.routing|focus.assist|
            power.battery|power.sleep|power.hibernate|power.lid|storage.volumes|storage.disks|storage.cleanup|storage.recovery|
            clipboard.history|multitasking.snap|system.optional_features|bluetooth.devices|usb.devices|printing.queues|printing.defaults|
            scanning.devices|input.keyboard|input.mouse|input.touch|input.pen|camera.devices|devices.autoplay|devices.phone|
            network.wifi|network.ethernet|network.vpn|network.proxy|network.hotspot|network.ip|network.dns|network.routes|
            network.shares|credentials.manager|appearance.theme|appearance.fonts|appearance.lockscreen|shell.start|shell.taskbar|
            explorer.options|apps.defaults|apps.startup|apps.permissions|accounts.users|accounts.hello|accounts.groups|
            accounts.work|accounts.sync|locale.time|locale.language|locale.speech|accessibility.vision|accessibility.hearing|
            accessibility.interaction|privacy.permissions|security.firewall|security.defender|security.certificates|
            security.bitlocker|security.policies|updates.windows|updates.restart|backup.windows|processes.lifecycle|
            services.configuration|tasks.scheduler|events.logs|environment.variables|registry.advanced|virtualization.hyperv|
            development.features|remote.rdp|vendors.lenovo|vendors.nvidia|vendors.intel|vendors.logitech
            """, "APIs/COM/CIM/PowerShell oficiais por recurso; validar versão, edição, hardware e política.");
        Add("Revit", """
            discovery.versions|discovery.api_context|architecture.walls|architecture.rooms|architecture.floors|architecture.roofs|
            structure.framing|structure.columns|structure.reinforcement|mep.systems|mep.connectors|mep.circuits|mep.routing|
            families.load|families.edit|parameters.shared|parameters.project|filters.views|selection.rules|views.create|
            views.templates|sheets.layout|schedules.create|quantities.materials|links.revit|links.cad|coordinates.shared|
            worksharing.sync|worksharing.ownership|export.ifc|export.dwg|export.pdf|import.cad
            """, "Add-in no contexto autorizado Revit, ExternalEvent, transações e verificação específica.");
        Add("Dynamo", """
            discovery.versions|discovery.player|graphs.execute|player.inputs|designscript.execute|python.engines|
            packages.restore|nodes.resolve|bindings.reconcile|batch.execute|diagnostics.runtime
            """, "Contexto Dynamo/Revit e versões/pacotes reais. Reutilizar bindings; nunca recriar elementos silenciosamente.");
        foreach (string name in WindowsQueryCatalog.Names)
            all.Add(new("windows.query." + name, "Windows", ["query"], Implementation.Implemented,
                "system/windows_query; name=" + name, "Cmdlet/provider e permissão disponíveis na máquina",
                "Envelope JSON real; indisponibilidade é reportada sem alterar configuração", "src/WindowsQueryCatalog.cs"));
        all.Add(new("audio.volume", "Windows", ["query", "configure"], Implementation.Implemented,
            "CoreAudio endpoint", "Endpoint padrão ativo; valor absoluto 0–100", "Valor relido no mesmo endpoint; alteração não testada ao vivo", "src/AudioEndpointSetting.cs"));
        all.Add(new("audio.mute", "Windows", ["query", "configure"], Implementation.Implemented,
            "CoreAudio endpoint", "Endpoint padrão ativo; true/false", "Estado relido; alteração não testada ao vivo", "src/AudioEndpointSetting.cs"));
        all.Add(new("commands.general", "General", ["execute"], Implementation.Implemented,
            "run/powershell em segundo plano", "Editar autorizado; processo, argumentos, stdin e cwd explícitos",
            "Argumentos Unicode/literais, cwd e falhas testados em PowerShell real; efeito da tarefa exige conferência", "src/StructuredCommands.cs", VerificationLevel.NativeReadTested));
        all.Add(new("files.batch_rename", "Files", ["rename", "verify"], Implementation.Implemented,
            "system/recipe files.rename", "1–15 pares absolutos; mesma pasta, sem sobrescrita; GUID de execução estável",
            "Preflight, SHA-256, arquivo alterado e replay testados em arquivos descartáveis", "src/VerifiedFileRename.cs", VerificationLevel.ContractTested));
        all.Add(new("clipboard.formats", "General", ["query", "set", "paste"], Implementation.Implemented,
            "Clipboard/STA + hook no runtime", "Texto, arquivos e imagem; formatos opacos podem impedir restauração segura",
            "Adapter testado sem mudar clipboard real; colar genérico devolve evidência para conferência, não sucesso presumido", "src/ClipboardExpansion.cs", VerificationLevel.ContractTested));
        return all.ToArray();
        void Add(string domain, string ids, string requirements)
        {
            foreach (string id in ids.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                all.Add(new(domain.ToLowerInvariant() + "." + id, domain,
                    ["discover", "query", "create", "connect", "configure", "use", "edit", "transform",
                        "import", "export", "transfer", "verify", "repair", "undo", "remove"],
                    Implementation.RequiresAdapter, "Extensão específica, quando aplicável", requirements,
                    "Abrir Settings não configura o recurso; operações não aplicáveis devem retornar Unavailable.", "docs/coverage.md"));
        }
    }
}
