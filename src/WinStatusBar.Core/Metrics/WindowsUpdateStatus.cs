using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Metrics;

public enum WindowsUpdateState { Unknown, UpToDate, UpdatesAvailable, RestartRequired, Failed }
public sealed record WindowsUpdateEntry(string Title, string Status, DateTime? Date = null)
{
    public string? UpdateId { get; init; }
    public int Revision { get; init; }
    public bool Downloaded { get; init; }
    public int? ErrorCode { get; init; }
    public bool CanInstall => Guid.TryParse(UpdateId, out _) && Revision >= 0;
    public string ActionLabel => ErrorCode is not null ? "Repetir" : Downloaded ? "Instalar" : "Baixar e instalar";
}

public sealed record WindowsUpdateStatus(WindowsUpdateState State, int PendingCount = 0, DateTime? LastSearch = null)
{
    public IReadOnlyList<WindowsUpdateEntry> Pending { get; init; } = [];
    public IReadOnlyList<WindowsUpdateEntry> History { get; init; } = [];
    public string? Error { get; init; }
    public static WindowsUpdateStatus FromResult(bool? reboot, bool searchSucceeded, int count, DateTime? lastSearch)
        => new(reboot == true ? WindowsUpdateState.RestartRequired
            : !searchSucceeded || reboot is null ? WindowsUpdateState.Unknown
            : count > 0 ? WindowsUpdateState.UpdatesAvailable
            : lastSearch is null ? WindowsUpdateState.Unknown : WindowsUpdateState.UpToDate,
            Math.Max(0, count), lastSearch);
}

public static class WindowsUpdateStatusReader
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim QueryGate = new(1);
    private static Task<WindowsUpdateStatus>? _pending, _scan;
    private static WindowsUpdateStatus? _last;
    private static DateTime _lastScan;
    public static WindowsUpdateStatus? LastKnown { get { lock (Gate) return _last; } }
    public static bool IsChecking { get { lock (Gate) return _scan is { IsCompleted: false }; } }

    public static Task<WindowsUpdateStatus> ReadAsync(bool online = false)
    {
        lock (Gate)
        {
            if (_scan is { IsCompleted: false }) return _scan;
            if (online) return _scan = QueryAsync(true);
            if (_pending is null || _pending.IsCompleted) _pending = QueryAsync(false);
            return _pending;
        }
    }

    private static async Task<WindowsUpdateStatus> QueryAsync(bool online)
    {
        await QueryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => Read(online)).ConfigureAwait(false);
            lock (Gate) _last = result;
            return result;
        }
        finally { QueryGate.Release(); }
    }

    private static WindowsUpdateStatus Read(bool online)
    {
        var objects = new Stack<object>();
        dynamic Keep(object value) { objects.Push(value); return value; }
        dynamic Create(string progId) => Keep(Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!);
        bool? reboot = null;
        DateTime? lastSearch = null;
        var pending = new List<WindowsUpdateEntry>();
        var history = new List<WindowsUpdateEntry>();
        try
        {
            dynamic system = Create("Microsoft.Update.SystemInfo");
            reboot = (bool)system.RebootRequired;
            try
            {
                dynamic automatic = Create("Microsoft.Update.AutoUpdate");
                dynamic results = Keep(automatic.Results);
                object? date = results.LastSearchSuccessDate;
                if (date is DateTime value && value.Year > 2000)
                    lastSearch = DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
            }
            catch { }
            if (_lastScan != default && (lastSearch is null || _lastScan > lastSearch)) lastSearch = _lastScan;
            dynamic session = Create("Microsoft.Update.Session");
            session.ClientApplicationID = "WinStatusBar";
            dynamic searcher = Keep(session.CreateUpdateSearcher());
            searcher.Online = online;
            searcher.CanAutomaticallyUpgradeService = false;
            dynamic result = Keep(searcher.Search("IsInstalled=0 and IsHidden=0"));
            dynamic updates = Keep(result.Updates);
            int count = (int)updates.Count;
            bool success = (int)result.ResultCode == 2;
            if (online && success) lastSearch = _lastScan = DateTime.Now;
            for (int i = 0; i < count; i++)
            {
                dynamic update = updates.Item(i);
                try
                {
                    dynamic identity = Keep(update.Identity);
                    bool downloaded = (bool)update.IsDownloaded;
                    pending.Add(new((string)update.Title, downloaded ? "Pronta para instalar" : "Disponível para baixar")
                    { UpdateId = (string)identity.UpdateID, Revision = (int)identity.RevisionNumber, Downloaded = downloaded });
                }
                finally { Marshal.ReleaseComObject(update); }
            }
            try
            {
                int total = Math.Min((int)searcher.GetTotalHistoryCount(), 5);
                if (total > 0)
                {
                    dynamic entries = Keep(searcher.QueryHistory(0, total));
                    for (int i = 0; i < total; i++)
                    {
                        dynamic entry = entries.Item(i);
                        try
                        {
                            int code = (int)entry.ResultCode;
                            string label = code switch { 2 => (int)entry.Operation == 2 ? "Removida" : "Instalada", 3 => "Concluída com avisos", 4 => "Falhou", 5 => "Cancelada", _ => "Em andamento" };
                            history.Add(new((string)entry.Title, label, DateTime.SpecifyKind((DateTime)entry.Date, DateTimeKind.Utc).ToLocalTime()));
                        }
                        finally { Marshal.ReleaseComObject(entry); }
                    }
                }
            }
            catch { history.Add(new("Histórico indisponível", "Não foi possível consultar o histórico.")); }
            // Recheck after a potentially long scan; a concurrent installation may now require a reboot.
            reboot = (bool)system.RebootRequired;
            return WindowsUpdateStatus.FromResult(reboot, success, count, lastSearch) with
            {
                Pending = pending, History = history,
                Error = success ? null : "O Windows retornou uma verificação incompleta. Tente novamente.",
            };
        }
        catch (Exception ex)
        {
            return WindowsUpdateStatus.FromResult(reboot, false, 0, lastSearch) with
            { Error = $"Não foi possível consultar o Windows Update (0x{ex.HResult:X8}). Tente novamente.", Pending = pending, History = history };
        }
        finally
        {
            while (objects.TryPop(out var instance))
                if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }
}



