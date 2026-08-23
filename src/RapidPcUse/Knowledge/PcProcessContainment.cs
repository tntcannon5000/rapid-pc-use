using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RapidPcUse.Knowledge;

/// <summary>
/// Places one trusted direct-process step and all descendants in a Windows job
/// whose members are terminated when the driver releases the job handle.
/// </summary>
internal sealed class PcProcessContainment : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private nint _job;

    private PcProcessContainment(nint job) => _job = job;

    internal static PcProcessContainment Attach(Process process)
    {
        var job = CreateJobObject(nint.Zero, null);
        if (job == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            TerminateUncontained(process);
            throw new Win32Exception(error, "Windows could not create containment for the trusted command.");
        }

        var containment = new PcProcessContainment(job);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not configure trusted-command containment.");
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not contain the trusted command.");
            }

            return containment;
        }
        catch
        {
            containment.Dispose();
            TerminateUncontained(process);
            throw;
        }
    }

    internal void TerminateAndWait(Process process, int waitMilliseconds)
    {
        Dispose();
        if (!process.HasExited && !process.WaitForExit(waitMilliseconds))
        {
            throw new InvalidOperationException("Windows did not terminate the trusted command job before its hard cleanup deadline.");
        }
    }

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, nint.Zero);
        if (job != nint.Zero)
        {
            _ = CloseHandle(job);
        }
    }

    private static void TerminateUncontained(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(2_000))
                {
                    throw new InvalidOperationException("Windows did not terminate an uncontained trusted command.");
                }
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between inspection and termination.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
