using System.Runtime.InteropServices;

namespace WinStatusBar.Services;

internal static class FolderShellOperations
{
    // Explorer's own dialogs handle collisions, permissions and cancellation. Allow undo/recycle.
    public static Task<bool> TransferAsync(IEnumerable<string> sources, string destination, bool move) => RunAsync(move ? 1u : 2u, sources, destination);
    public static Task<bool> DeleteAsync(string source) => RunAsync(3, [source], null);
    public static Task<bool> DeleteAsync(IEnumerable<string> sources) => RunAsync(3, sources, null);
    public static Task<bool> RenameAsync(string source, string destination) => RunAsync(4, [source], destination);

    private static Task<bool> RunAsync(uint operation, IEnumerable<string> sources, string? destination)
    {
        string[] paths = sources.ToArray();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var info = new FileOperation { Operation = operation, From = string.Join('\0', paths) + "\0\0", To = destination is null ? null : destination + "\0\0", Flags = 0x40 };
                int result = SHFileOperation(ref info);
                if (result != 0 && !info.Aborted) throw new IOException($"O Windows não concluiu a operação (0x{result:X}).");
                completion.SetResult(!info.Aborted && result == 0);
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "WinStatusBar File operation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    public static void Properties(string path)
    {
        var info = new ExecuteInfo { Size = Marshal.SizeOf<ExecuteInfo>(), Mask = 0x0000000C, Verb = "properties", File = path, Show = 1 };
        if (!ShellExecuteEx(ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window; public uint Operation;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr Mappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Window;
        public string? Verb, File, Parameters, Directory; public int Show;
        public IntPtr Instance, IdList; public string? Class; public IntPtr ClassKey; public uint HotKey;
        public IntPtr Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref FileOperation info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ExecuteInfo info);
}
