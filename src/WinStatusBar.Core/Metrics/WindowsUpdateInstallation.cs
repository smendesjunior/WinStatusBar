using System.Runtime.InteropServices;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Core.Metrics;

/// <summary>One explicitly selected update, executed in the elevated helper. Never reboots.</summary>
public static class WindowsUpdateInstallation
{
    public static int InstallSelection(string selection)
    {
        var entries = selection.Split(',');
        if (entries.Length is < 1 or > 500) return 2;
        var parsed = new List<(Guid Id, int Revision)>();
        foreach (string entry in entries)
        {
            var pair = entry.Split(':');
            if (pair.Length != 2 || !Guid.TryParse(pair[0], out var id) || !int.TryParse(pair[1], out int revision) || revision < 0) return 2;
            parsed.Add((id, revision));
        }
        bool installed = false;
        foreach (var (id, revision) in parsed.Distinct())
        {
            int result = Install(id.ToString("D"), revision);
            if (result is not (0 or 20)) return result;
            installed |= result == 0;
        }
        return installed ? 0 : 20;
    }

    public static int Install(string id, int revision)
    {
        if (!Guid.TryParse(id, out var guid) || revision < 0) return 2;
        if (!SensorPermissionTask.IsAdministrator()) return 5;
        using var mutex = new Mutex(false, "Global\\WinStatusBar.WindowsUpdate");
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 6;
        var objects = new Stack<object>();
        dynamic Keep(object value) { objects.Push(value); return value; }
        try
        {
            dynamic session = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true)!)!);
            session.ClientApplicationID = "WinStatusBar";
            dynamic searcher = Keep(session.CreateUpdateSearcher());
            searcher.Online = false;
            dynamic result = Keep(searcher.Search($"IsInstalled=0 and IsHidden=0 and UpdateID='{guid:D}' and RevisionNumber={revision}"));
            if ((int)result.ResultCode != 2) return 40;
            dynamic updates = Keep(result.Updates);
            if ((int)updates.Count != 1) return 20;
            dynamic update = Keep(updates.Item(0));
            // Legal terms and interactive installers stay in the Windows interface.
            if (!(bool)update.EulaAccepted) return 30;
            dynamic behavior = Keep(update.InstallationBehavior);
            if ((bool)behavior.CanRequestUserInput) return 30;
            if (!(bool)update.IsDownloaded)
            {
                dynamic downloader = Keep(session.CreateUpdateDownloader());
                downloader.Updates = updates;
                dynamic downloaded = Keep(downloader.Download());
                if ((int)downloaded.ResultCode != 2 || !(bool)update.IsDownloaded) return 40;
            }
            dynamic installer = Keep(session.CreateUpdateInstaller());
            installer.Updates = updates;
            if ((bool)installer.IsBusy) return 6;
            dynamic installed = Keep(installer.Install());
            if ((int)installed.ResultCode != 2) return 40;
            return (bool)installed.RebootRequired ? 10 : 0;
        }
        catch { return 40; }
        finally
        {
            while (objects.TryPop(out var value))
                if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            mutex.ReleaseMutex();
        }
    }
}
