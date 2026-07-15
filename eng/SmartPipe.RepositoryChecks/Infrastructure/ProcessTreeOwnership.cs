using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartPipe.RepositoryChecks.Infrastructure;

internal interface IProcessTreeOwnershipFactory
{
    ValueTask<IDisposable> InitializeAsync(CancellationToken cancellationToken);
}

internal sealed class ProcessTreeOwnershipFactory : IProcessTreeOwnershipFactory
{
    public ValueTask<IDisposable> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IDisposable>(WindowsJobOwnership.CreateForCurrentProcess());
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return ValueTask.FromResult<IDisposable>(UnixProcessGroupOwnership.CreateForCurrentProcess());
        }

        throw new PlatformNotSupportedException(
            "Repository-check process-tree ownership is supported only on Windows, Linux, and macOS.");
    }
}

internal static class ProcessTreeTerminator
{
    private const int SigKill = 9;

    public static void Kill(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            // The host owns a kill-on-close Job Object. Terminating the host closes
            // its non-inheritable job handle and lets the kernel terminate all members.
            process.Kill();
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (UnixNativeMethods.Kill(-process.Id, SigKill) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return;
        }

        throw new PlatformNotSupportedException(
            "Repository-check process-tree termination is supported only on Windows, Linux, and macOS.");
    }
}

internal sealed class WindowsJobOwnership : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly SafeJobHandle _job;

    private WindowsJobOwnership(SafeJobHandle job)
    {
        _job = job;
    }

    public static WindowsJobOwnership CreateForCurrentProcess()
    {
        var job = WindowsNativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!WindowsNativeMethods.SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            using var currentProcess = Process.GetCurrentProcess();
            if (!WindowsNativeMethods.AssignProcessToJobObject(job, currentProcess.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new WindowsJobOwnership(job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _job.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private static class WindowsNativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return WindowsNativeMethods.CloseHandle(handle);
        }
    }
}

internal sealed class UnixProcessGroupOwnership : IDisposable
{
    private UnixProcessGroupOwnership()
    {
    }

    public static UnixProcessGroupOwnership CreateForCurrentProcess()
    {
        if (UnixNativeMethods.SetProcessGroup(0, 0) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new UnixProcessGroupOwnership();
    }

    public void Dispose()
    {
    }
}

internal static class UnixNativeMethods
{
    [DllImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    public static extern int SetProcessGroup(int processId, int processGroupId);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static extern int Kill(int processId, int signal);
}
