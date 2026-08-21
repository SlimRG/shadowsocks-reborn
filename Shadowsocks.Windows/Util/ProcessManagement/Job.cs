#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NLog;

namespace Shadowsocks.Util.ProcessManagement
{
    /// <summary>
    /// Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
    /// All assigned processes are terminated automatically when the job handle is closed.
    /// </summary>
    public sealed class Job : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly SafeFileHandle _handle;
        private bool _disposed;

        public Job()
        {
            nint rawHandle = CreateJobObjectW(nint.Zero, null);
            if (rawHandle == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create process job object.");
            }

            _handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                var information = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };

                int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                if (!SetInformationJobObject(
                    _handle,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    (uint)size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure process job object.");
                }
            }
            catch
            {
                _handle.Dispose();
                throw;
            }
        }

        public bool AddProcess(IntPtr processHandle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool success = AssignProcessToJobObject(_handle, processHandle);
            if (!success)
            {
                Logger.Error("AssignProcessToJobObject failed. GetLastError={Error}", Marshal.GetLastWin32Error());
            }

            return success;
        }

        public bool AddProcess(int processId)
        {
            using Process process = Process.GetProcessById(processId);
            return AddProcess(process.Handle);
        }

        public void AddProcessOrThrow(Process process, string processDescription = "child process")
        {
            ArgumentNullException.ThrowIfNull(process);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Unable to assign {processDescription} to its process job object.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle hJob,
            int jobObjectInformationClass,
            ref JobObjectExtendedLimitInformation lpJobObjectInformation,
            uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, nint hProcess);
    }
}
