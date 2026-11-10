using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexIAs;

/// <summary>
/// Grupo de processos do Windows (job object) com encerramento ao fechar: quando o componente fecha ou é atualizado, o
/// handle some e o Windows encerra o claude e todos os comandos que ele abriu, em vez de ficarem rodando sem dono.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private IntPtr _handle;
    private readonly object _gate = new();

    private KillOnCloseJob(IntPtr handle) => _handle = handle;

    public static KillOnCloseJob? Create()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(job);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return new KillOnCloseJob(job);
    }

    /// <summary>Põe o processo no grupo; false depois de fechado ou se o Windows recusar.</summary>
    public bool Add(Process process)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return false;
            try { return AssignProcessToJobObject(_handle, process.Handle); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
