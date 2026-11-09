using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

public sealed record SettingReading(string Id, string? Value, bool CanApply, string Availability, string Evidence);
public interface IWindowsSetting
{
    string Id { get; }
    Task<SettingReading> ReadAsync(CancellationToken ct);
    Task<DesktopActionResult> ApplyAsync(string value, CancellationToken ct);
}
public sealed class SettingsRegistry
{
    private readonly Dictionary<string, IWindowsSetting> settings = new(StringComparer.Ordinal);
    public SettingsRegistry(IEnumerable<IWindowsSetting> initial) { foreach (var s in initial) Register(s); }
    public void Register(IWindowsSetting setting)
    {
        if (!settings.TryAdd(setting.Id, setting)) throw new ArgumentException("Adaptador duplicado: " + setting.Id);
    }
    public async Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        if (r.Name is null || !settings.TryGetValue(r.Name, out var setting))
            return new("Configuração requer adaptador: " + r.Name, Success: false, Status: DesktopStatus.Blocked);
        var before = await setting.ReadAsync(ct);
        if (r.Op == "setting_read")
            return new(System.Text.Json.JsonSerializer.Serialize(before), Method: "native/query");
        if (r.Op != "setting_apply" || r.Value is null) return new("Informe op e value.", Success: false);
        if (!before.CanApply) return new(before.Evidence, Success: false, Status: DesktopStatus.Blocked);
        return await setting.ApplyAsync(r.Value, ct);
    }
}

/// <summary>Verifies stored preference, never claims an Explorer window refreshed or overrides a policy.</summary>
public sealed class ExplorerPreference(string id, string valueName, string enabledValue, int enabled, string disabledValue, int disabled) : IWindowsSetting
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public string Id => id;
    private static bool PolicyBlocks()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
            if (key?.GetValue("NoFolderOptions") is int n && n != 0) return true;
        }
        return false;
    }
    public Task<SettingReading> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return Task.FromResult(new SettingReading(Id, null, false, "unavailable", "Versão não validada pelo adaptador."));
        bool policy = PolicyBlocks();
        using var key = Registry.CurrentUser.OpenSubKey(Advanced);
        object? raw = key?.GetValue(valueName);
        string? value = raw is int n ? n == enabled ? enabledValue : n == disabled ? disabledValue : n.ToString() : null;
        return Task.FromResult(new SettingReading(Id, value, !policy, policy ? "policy_blocked" : "implemented",
            "Preferência HKCU; ausência de política conhecida não prova ausência de toda política MDM. UI pode requerer atualização."));
    }
    public async Task<DesktopActionResult> ApplyAsync(string value, CancellationToken ct)
    {
        int desired = value == enabledValue ? enabled : value == disabledValue ? disabled :
            throw new ArgumentException($"value deve ser {enabledValue}/{disabledValue}.");
        var before = await ReadAsync(ct);
        if (!before.CanApply) return new("Política ou versão impede alteração.", Success: false, Status: DesktopStatus.Blocked);
        if (before.Value == value) return new("Preferência já está no valor pedido.", Method: "registry/HKCU");
        ct.ThrowIfCancellationRequested();
        using (var key = Registry.CurrentUser.CreateSubKey(Advanced, true))
            key.SetValue(valueName, desired, RegistryValueKind.DWord);
        var after = await ReadAsync(ct);
        if (after.Value != value) return new("Valor final não confirmado.", Success: false, Status: DesktopStatus.Partial);
        SendMessageTimeoutW(new nint(0xffff), 0x001A, 0, Advanced, 2, 1000, out _);
        return new($"Preferência gravada e relida: {Id}={after.Value}. Anterior={before.Value ?? "(ausente)"}; atualização visual do Explorer não confirmada.",
            Method: "registry/HKCU+WM_SETTINGCHANGE");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint msg, nuint wparam,
        string lparam, uint flags, uint timeout, out nuint result);
}

public sealed class ActivePowerPlan : IWindowsSetting
{
    public string Id => "power.active_plan";
    private static readonly Regex GuidPattern = new(@"[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}");
    private static Task<ProcessOutput> Run(string[] args, CancellationToken ct) =>
        HiddenProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args, null, ct);
    public async Task<SettingReading> ReadAsync(CancellationToken ct)
    {
        var r = await Run(["/getactivescheme"], ct);
        string? value = GuidPattern.Match(r.Stdout) is { Success: true } m ? m.Value.ToLowerInvariant() : null;
        return new(Id, value, r.ExitCode == 0 && value is not null, r.ExitCode == 0 ? "implemented" : "unavailable",
            "Plano retornado por powercfg; políticas e hardware podem impedir mudança. Energia moderna pode expor apenas um plano.");
    }
    public async Task<DesktopActionResult> ApplyAsync(string value, CancellationToken ct)
    {
        if (!Guid.TryParseExact(value, "D", out var guid)) throw new ArgumentException("Informe GUID de plano existente.");
        string desired = guid.ToString("D");
        var before = await ReadAsync(ct);
        if (before.Value == desired) return new("Plano já ativo.", Method: "powercfg");
        var list = await Run(["/list"], ct);
        if (list.ExitCode != 0 || !GuidPattern.Matches(list.Stdout).Any(m => m.Value.Equals(desired, StringComparison.OrdinalIgnoreCase)))
            return new("Plano não consta em powercfg /list.", Success: false, Status: DesktopStatus.Blocked);
        var result = await Run(["/setactive", desired], ct);
        if (result.ExitCode != 0) return new("Windows negou mudança do plano; código " + result.ExitCode +
            ". Nenhuma elevação automática foi solicitada.", Success: false, Status: DesktopStatus.Blocked);
        var after = await ReadAsync(ct);
        return after.Value == desired ? new($"Plano ativo conferido; anterior={before.Value}.", Method: "powercfg")
            : new("Comando aceito; plano final não confere (possível política).", Success: false, Status: DesktopStatus.Partial, Method: "powercfg");
    }
}
