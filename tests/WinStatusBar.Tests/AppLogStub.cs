namespace WinStatusBar;

internal static class App
{
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
    public static void Log(string message) => Messages.Enqueue(message);
}
