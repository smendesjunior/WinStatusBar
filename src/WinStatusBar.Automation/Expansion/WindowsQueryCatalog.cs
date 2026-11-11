using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>Read-only structured recipes. Unknown resources remain with the original AI executor.</summary>
public sealed class WindowsQueryCatalog
{
    private readonly Func<string, CancellationToken, Task<ProcessOutput>> run;
    public WindowsQueryCatalog(Func<string, CancellationToken, Task<ProcessOutput>>? runner = null)
        => run = runner ?? HiddenProcess.PowerShellAsync;
    private static readonly IReadOnlyDictionary<string, string> Queries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["system.version"] = "Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime",
        ["display.adapters"] = "Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,VideoModeDescription,CurrentHorizontalResolution,CurrentVerticalResolution",
        ["display.monitors"] = "Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorID | Select-Object InstanceName,Active,ManufacturerName,UserFriendlyName",
        ["audio.devices"] = "Get-CimInstance Win32_SoundDevice | Select-Object Name,Status,DeviceID",
        ["power.battery"] = "Get-CimInstance Win32_Battery | Select-Object Name,BatteryStatus,EstimatedChargeRemaining,EstimatedRunTime",
        ["storage.disks"] = "Get-Disk | Select-Object Number,FriendlyName,BusType,PartitionStyle,OperationalStatus,HealthStatus,Size,IsOffline,IsReadOnly",
        ["storage.volumes"] = "Get-Volume | Select-Object DriveLetter,FileSystemLabel,FileSystem,HealthStatus,Size,SizeRemaining",
        ["storage.partitions"] = "Get-Partition | Select-Object DiskNumber,PartitionNumber,DriveLetter,Type,Size",
        ["bluetooth.devices"] = "Get-PnpDevice -Class Bluetooth | Select-Object Status,Class,FriendlyName,InstanceId",
        ["usb.devices"] = "Get-PnpDevice -Class USB | Select-Object Status,Class,FriendlyName,InstanceId",
        ["camera.devices"] = "Get-PnpDevice | Where-Object { $_.Class -in 'Camera','Image' } | Select-Object Status,Class,FriendlyName,InstanceId",
        ["input.devices"] = "Get-PnpDevice | Where-Object { $_.Class -in 'Keyboard','Mouse','HIDClass' } | Select-Object Status,Class,FriendlyName,InstanceId",
        ["printing.queues"] = "Get-Printer | Select-Object Name,DriverName,PortName,PrinterStatus,Shared,Published",
        ["printing.drivers"] = "Get-PrinterDriver | Select-Object Name,Manufacturer,PrinterEnvironment,MajorVersion",
        ["network.adapters"] = "Get-NetAdapter | Select-Object Name,InterfaceDescription,ifIndex,Status,LinkSpeed,MacAddress",
        ["network.ip"] = "Get-NetIPAddress | Select-Object InterfaceIndex,InterfaceAlias,AddressFamily,IPAddress,PrefixLength,AddressState,PrefixOrigin",
        ["network.dns"] = "Get-DnsClientServerAddress | Select-Object InterfaceIndex,InterfaceAlias,AddressFamily,ServerAddresses",
        ["network.routes"] = "Get-NetRoute | Select-Object InterfaceIndex,DestinationPrefix,NextHop,RouteMetric,Protocol",
        ["network.profiles"] = "Get-NetConnectionProfile | Select-Object Name,InterfaceAlias,InterfaceIndex,NetworkCategory,IPv4Connectivity,IPv6Connectivity",
        ["network.vpn"] = "Get-VpnConnection | Select-Object Name,ServerAddress,TunnelType,ConnectionStatus,SplitTunneling",
        ["network.shares"] = "Get-SmbShare | Select-Object Name,Path,Description,ScopeName,Special",
        ["accounts.users"] = "Get-LocalUser | Select-Object Name,Enabled,LastLogon,PasswordRequired,PrincipalSource",
        ["accounts.groups"] = "Get-LocalGroup | Select-Object Name,Description,PrincipalSource",
        ["locale.timezone"] = "Get-TimeZone | Select-Object Id,DisplayName,BaseUtcOffset,SupportsDaylightSavingTime",
        ["locale.languages"] = "Get-WinUserLanguageList | Select-Object LanguageTag,LocalizedName,EnglishName,InputMethodTips,Spellchecking,Handwriting",
        ["locale.culture"] = "Get-Culture | Select-Object Name,DisplayName,EnglishName",
        ["security.firewall"] = "Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultInboundAction,DefaultOutboundAction",
        ["security.defender"] = "Get-MpComputerStatus | Select-Object AMServiceEnabled,AntivirusEnabled,RealTimeProtectionEnabled,AntivirusSignatureVersion,AntivirusSignatureLastUpdated",
        ["security.certificates"] = "Get-ChildItem Cert:\\CurrentUser\\My | Select-Object Subject,Issuer,Thumbprint,NotBefore,NotAfter,HasPrivateKey",
        ["security.bitlocker"] = "Get-BitLockerVolume | Select-Object MountPoint,VolumeStatus,ProtectionStatus,EncryptionPercentage,EncryptionMethod",
        ["services.status"] = "Get-Service | Select-Object Name,DisplayName,Status,StartType",
        ["tasks.scheduler"] = "Get-ScheduledTask | Select-Object TaskName,TaskPath,State,Author",
        ["processes.list"] = "Get-Process | Select-Object Id,ProcessName,Responding,CPU,WorkingSet64,StartTime",
        ["events.channels"] = "Get-WinEvent -ListLog Application,System | Select-Object LogName,RecordCount,IsEnabled,LastWriteTime,FileSize,MaximumSizeInBytes",
        ["apps.startup"] = "Get-CimInstance Win32_StartupCommand | Select-Object Name,Command,Location,User",
        ["system.features"] = "Get-WindowsOptionalFeature -Online | Select-Object FeatureName,State",
        ["virtualization.host"] = "Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,HypervisorPresent,TotalPhysicalMemory",
        ["development.wsl"] = "Get-ItemProperty HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Lxss\\* | Select-Object DistributionName,Version,BasePath"
    };
    public static IReadOnlyCollection<string> Names => Queries.Keys.ToArray();
    /// <summary>
    /// The result goes whole into the next model request and a query may return 500 rows (services, processes, features).
    /// A long answer is first made readable (no \uXXXX escapes) and then cut; the cut text is no longer valid JSON.
    /// </summary>
    private const int MaxResultChars = 12000;
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Cut(string json)
    {
        if (json.Length <= MaxResultChars) return json;
        try { json = System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(Relaxed) ?? json; } catch (JsonException) { }
        return json.Length <= MaxResultChars ? json : json[..MaxResultChars] + "\n…(truncado; para filtrar, use powershell com Where-Object/Select-Object)";
    }
    public async Task<DesktopActionResult?> TryExecuteAsync(DesktopActionRequest request, CancellationToken ct)
    {
        if (request.Action != "system") return null;
        if (request.Op == "windows_query_catalog")
            return new(JsonSerializer.Serialize(Names), Method: "Windows/query-catalog");
        if (request.Op != "windows_query" || request.Name is null || !Queries.TryGetValue(request.Name, out var query))
            return null;
        // Fixed trusted scripts only. Model-supplied text is never concatenated into code.
        string script = "try { $data=@(& { " + query +
            " } | Select-Object -First 501); @{available=$true;truncated=($data.Count -gt 500);data=@($data | Select-Object -First 500)} | ConvertTo-Json -Depth 6 -Compress } " +
            "catch { @{available=$false;errorType=$_.Exception.GetType().Name;errorId=$_.FullyQualifiedErrorId} | ConvertTo-Json -Compress }";
        var result = await run(script, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Truncated)
            return new("Consulta Windows não produziu resultado completo. Use ferramentas gerais para diagnosticar.",
                Success: false, Status: DesktopStatus.Blocked, Method: "Windows/PowerShell/query");
        try
        {
            using var json = JsonDocument.Parse(result.Stdout);
            bool available = json.RootElement.GetProperty("available").GetBoolean();
            return new(Cut(result.Stdout), Success: available, Status: available ? DesktopStatus.Done : DesktopStatus.Blocked,
                Method: "Windows/PowerShell/query");
        }
        catch (JsonException)
        {
            return new("Resposta da consulta não foi validada; nenhuma configuração foi alterada.",
                Success: false, Status: DesktopStatus.Failed, Method: "Windows/PowerShell/query");
        }
    }
}
