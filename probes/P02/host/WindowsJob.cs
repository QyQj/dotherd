using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static unsafe partial class WindowsJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const nuint JobListAttribute = 0x2000D;
    private const uint ExtendedStartupInfoPresent = 0x80000;

    public static SafeFileHandle Create()
    {
        var job = CreateJobObjectW(0, 0);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimits)) == 0)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            job.Dispose();
            throw error;
        }
        return job;
    }

    public static int StartApp(SafeFileHandle job, string exe, Guid id)
    {
        // PROC_THREAD_ATTRIBUTE_JOB_LIST assigns the process at creation, before any app code can spawn a worker.
        nuint size = 0;
        if (InitializeProcThreadAttributeList(0, 1, 0, ref size) != 0 || Marshal.GetLastPInvokeError() != 122)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint attributes = Marshal.AllocHGlobal(checked((nint)size));
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            try
            {
                nint handle = job.DangerousGetHandle();
                Check(UpdateProcThreadAttribute(attributes, 0, JobListAttribute, &handle, (nuint)sizeof(nint), 0, 0));
                var startup = new StartupInfoEx { Info = new StartupInfo { Size = (uint)sizeof(StartupInfoEx) }, Attributes = attributes };
                char[] command = $"\"{exe}\" app {id:N}\0".ToCharArray();
                fixed (char* commandLine = command)
                {
                    Check(CreateProcessW(exe, commandLine, 0, 0, 0, ExtendedStartupInfoPresent, 0, null, &startup, out var process));
                    using var processHandle = new SafeFileHandle(process.Process, true);
                    using var threadHandle = new SafeFileHandle(process.Thread, true);
                    return process.ProcessId;
                }
            }
            finally { DeleteProcThreadAttributeList(attributes); }
        }
        finally
        {
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(job);
        }
    }

    private static void Check(int success)
    {
        if (success == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public fixed ulong IoCounters[6];
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedData, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObjectW(nint security, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(SafeFileHandle job, int kind, ExtendedLimits* limits, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint* value, nuint size, nint previous, nint returnedSize);
    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProcessW(string app, char* command, nint processSecurity, nint threadSecurity,
        int inheritHandles, uint flags, nint environment, string? directory, StartupInfoEx* startup, out ProcessInfo process);
}
