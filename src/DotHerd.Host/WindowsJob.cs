using System.Diagnostics;
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

    public static Process Start(SafeFileHandle job, string exe, string commandLine, string? cwd = null,
        string? environment = null, nint[]? stdHandles = null)
    {
        int count = stdHandles is null ? 1 : 2;
        nuint size = 0;
        if (InitializeProcThreadAttributeList(0, count, 0, ref size) != 0 || Marshal.GetLastPInvokeError() != 122)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint attributes = Marshal.AllocHGlobal(checked((nint)size));
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, count, 0, ref size));
            try
            {
                nint handle = job.DangerousGetHandle();
                Check(UpdateProcThreadAttribute(attributes, 0, JobListAttribute, &handle, (nuint)sizeof(nint), 0, 0));
                var startup = new StartupInfoEx { Info = new StartupInfo { Size = (uint)sizeof(StartupInfoEx) }, Attributes = attributes };
                fixed (nint* handles = stdHandles)
                fixed (char* env = environment)
                fixed (char* command = (commandLine + '\0').ToCharArray())
                {
                    if (stdHandles is not null)
                    {
                        Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * sizeof(nint)), 0, 0));
                        startup.Info.Flags = 0x100; // STARTF_USESTDHANDLES; only these three handles are inherited.
                        startup.Info.StdInput = handles[0];
                        startup.Info.StdOutput = handles[1];
                        startup.Info.StdError = handles[2];
                    }
                    const uint suspended = 0x4, unicodeEnvironment = 0x400, noWindow = 0x08000000;
                    Check(CreateProcessW(exe, command, 0, 0, stdHandles is null ? 0 : 1,
                        ExtendedStartupInfoPresent | suspended | unicodeEnvironment | noWindow, (nint)env, cwd, &startup, out var child));
                    using var processHandle = new SafeFileHandle(child.Process, true);
                    using var thread = new SafeFileHandle(child.Thread, true);
                    var process = Process.GetProcessById(child.ProcessId);
                    try
                    {
                        _ = process.Handle; // Retain the actual process even if it exits immediately on resume.
                        if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
                        return process;
                    }
                    catch { process.Dispose(); throw; }
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeFileHandle thread);

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
