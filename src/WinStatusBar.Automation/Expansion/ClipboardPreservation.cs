using System.Drawing;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>Materializes supported clipboard formats before replacement; unknown opaque data blocks before mutation.</summary>
public sealed class ClipboardPreservation : IDisposable
{
    private readonly DataObject data = new();
    private readonly List<IDisposable> owned = [];
    private readonly bool wasEmpty;
    private ClipboardPreservation(bool empty) { wasEmpty = empty; }
    public static ClipboardPreservation Capture()
    {
        var original = Clipboard.GetDataObject();
        var snapshot = new ClipboardPreservation(original is null || original.GetFormats(false).Length == 0);
        try
        {
            foreach (string format in original?.GetFormats(false) ?? [])
            {
                object? value = original!.GetData(format, false);
                object copy = value switch
                {
                    string text => text,
                    string[] paths => paths.Clone(),
                    byte[] bytes => bytes.Clone(),
                    MemoryStream stream => new MemoryStream(stream.ToArray(), false),
                    Bitmap image => new Bitmap(image),
                    int number => number,
                    _ => throw new InvalidOperationException("Formato da área de transferência não pode ser preservado com segurança: " + format)
                };
                if (copy is IDisposable disposable) snapshot.owned.Add(disposable);
                snapshot.data.SetData(format, false, copy);
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }
    public void Restore()
    {
        if (wasEmpty) Clipboard.Clear();
        else Clipboard.SetDataObject(data, true, 5, 60);
    }
    public void Dispose() { foreach (var item in owned) item.Dispose(); owned.Clear(); }
}
