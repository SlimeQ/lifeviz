using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace lifeviz;

/// <summary>
/// Owns every FFmpeg process started by LifeViz. Windows Job Object membership is
/// the primary crash-containment mechanism; tracked process-tree termination is
/// retained as a fallback and for orderly shutdown.
/// </summary>
internal sealed class FfmpegProcessManager : IDisposable
{
    private const int DefaultTerminationTimeoutMilliseconds = 2000;
    private static readonly TimeSpan DefaultTerminationTimeout =
        TimeSpan.FromMilliseconds(DefaultTerminationTimeoutMilliseconds);
    private static readonly Lazy<string> ResolvedDefaultExecutable = new(
        () => ResolveExecutableCore("ffmpeg"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _lifecycleLock = new();
    private readonly ConcurrentDictionary<int, OwnedProcess> _processes = new();
    private readonly KillOnCloseJob? _job;
    private int _disposed;
    private int _jobFallbackWasLogged;

    private FfmpegProcessManager()
    {
        _job = KillOnCloseJob.TryCreate(out string? error);
        if (_job == null && !string.IsNullOrWhiteSpace(error))
        {
            Logger.Warn($"FFmpeg Job Object containment is unavailable; process-tree cleanup remains enabled: {error}");
        }
    }

    public static FfmpegProcessManager Shared { get; } = new();

    internal bool UsesKillOnCloseJob => _job != null;

    internal int TrackedProcessCount => _processes.Count;

    /// <summary>
    /// Creates an independent owner for containment smoke tests. Disposing this
    /// instance closes its Job Object without affecting the application-wide owner.
    /// </summary>
    internal static FfmpegProcessManager CreateIsolatedForSmokeTest() => new();

    internal static string ResolveFfmpegExecutable(string requestedExecutable = "ffmpeg")
    {
        if (string.IsNullOrWhiteSpace(requestedExecutable))
        {
            throw new ArgumentException("An FFmpeg executable is required.", nameof(requestedExecutable));
        }

        string fileName = Path.GetFileName(requestedExecutable);
        if (!fileName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{requestedExecutable}' is not an FFmpeg executable.",
                nameof(requestedExecutable));
        }

        return requestedExecutable.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
               requestedExecutable.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            ? ResolvedDefaultExecutable.Value
            : ResolveExecutableCore(requestedExecutable);
    }

    /// <summary>
    /// Starts FFmpeg, tracks it, and assigns it to the kill-on-close Job Object
    /// before returning control to the caller.
    /// </summary>
    internal Process Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            startInfo.FileName = ResolveFfmpegExecutable(startInfo.FileName);
            startInfo.UseShellExecute = false;
            if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
                // Direct shortcuts intentionally run LifeViz from its versioned
                // install directory. Do not let FFmpeg inherit a directory handle
                // that could block a later transactional installer swap.
                startInfo.WorkingDirectory = Path.GetTempPath();
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            var owned = new OwnedProcess(process);
            process.Exited += (_, _) => OnProcessExited(owned);

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start FFmpeg.");
                }

                owned.ProcessId = process.Id;
                if (!_processes.TryAdd(owned.ProcessId, owned))
                {
                    TryKillProcessTree(process);
                    throw new InvalidOperationException(
                        $"Could not register FFmpeg process {owned.ProcessId} for lifecycle management.");
                }

                if (_job != null && !_job.TryAssign(process, out string? assignmentError) &&
                    Interlocked.Exchange(ref _jobFallbackWasLogged, 1) == 0)
                {
                    Logger.Warn(
                        $"FFmpeg process {owned.ProcessId} could not join the kill-on-close Job Object; " +
                        $"process-tree cleanup remains enabled: {assignmentError}");
                }

                // The process can exit between Start and registration. Reconcile the
                // registry after assignment so a short-lived probe is never retained.
                if (HasExited(process))
                {
                    OnProcessExited(owned);
                }

                return process;
            }
            catch
            {
                RemoveTrackedProcess(owned);
                TryKillProcessTree(process);
                TryWaitForExit(process, DefaultTerminationTimeout);
                process.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Kills one FFmpeg process and its descendants, waits for a bounded interval,
    /// and disposes the Process wrapper. On timeout, the manager retains ownership
    /// and disposes the wrapper when the process eventually exits.
    /// </summary>
    internal bool TerminateAndDispose(Process? process, TimeSpan? timeout = null)
    {
        if (process == null)
        {
            return true;
        }

        TimeSpan boundedTimeout = NormalizeTimeout(timeout);
        OwnedProcess? owned = FindTrackedProcess(process);
        TryKillProcessTree(process);

        bool exited = TryWaitForExit(process, boundedTimeout);
        if (exited)
        {
            if (owned != null)
            {
                RemoveTrackedProcess(owned);
            }
            process.Dispose();
            return true;
        }

        if (owned != null)
        {
            Volatile.Write(ref owned.DisposeWhenExited, 1);
            if (HasExited(process))
            {
                OnProcessExited(owned);
                return true;
            }
        }

        Logger.Warn($"FFmpeg process did not exit within {boundedTimeout.TotalMilliseconds:0} ms.");
        return false;
    }

    /// <summary>
    /// Signals an owned FFmpeg tree to exit without waiting or disposing its
    /// Process wrapper. The normal worker/finalizer path retains ownership and
    /// performs bounded cleanup after the process exits.
    /// </summary>
    internal void RequestTermination(Process? process)
    {
        if (process != null)
        {
            TryKillProcessTree(process);
        }
    }

    /// <summary>
    /// Terminates all currently tracked FFmpeg trees within one shared timeout.
    /// The manager remains usable for later starts.
    /// </summary>
    internal bool TerminateAll(TimeSpan? timeout = null)
    {
        TimeSpan boundedTimeout = NormalizeTimeout(timeout);
        OwnedProcess[] snapshot = _processes.Values.ToArray();
        return TerminateSnapshot(snapshot, boundedTimeout);
    }

    /// <summary>
    /// Permanently shuts down this owner. Closing the Job Object kills any assigned
    /// descendants even if normal process-tree termination did not complete.
    /// </summary>
    internal bool Shutdown(TimeSpan? timeout = null)
    {
        OwnedProcess[] snapshot;
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return _processes.IsEmpty;
            }

            snapshot = _processes.Values.ToArray();
            foreach (OwnedProcess owned in snapshot)
            {
                TryKillProcessTree(owned.Process);
            }

            // This is the crash-proof backstop: handle closure terminates every
            // process assigned to this Job, including FFmpeg descendants.
            _job?.Dispose();
        }

        return WaitForAndDisposeSnapshot(snapshot, NormalizeTimeout(timeout));
    }

    public void Dispose()
    {
        Shutdown(DefaultTerminationTimeout);
    }

    internal int[] GetTrackedProcessIdsForSmokeTest() =>
        _processes.Keys.OrderBy(static id => id).ToArray();

    private bool TerminateSnapshot(OwnedProcess[] snapshot, TimeSpan timeout)
    {
        foreach (OwnedProcess owned in snapshot)
        {
            TryKillProcessTree(owned.Process);
        }

        return WaitForAndDisposeSnapshot(snapshot, timeout);
    }

    private bool WaitForAndDisposeSnapshot(OwnedProcess[] snapshot, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        bool allExited = true;

        foreach (OwnedProcess owned in snapshot)
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            if (TryWaitForExit(owned.Process, remaining))
            {
                RemoveTrackedProcess(owned);
                owned.Process.Dispose();
                continue;
            }

            allExited = false;
            Volatile.Write(ref owned.DisposeWhenExited, 1);
            if (HasExited(owned.Process))
            {
                OnProcessExited(owned);
            }
        }

        return allExited;
    }

    private void OnProcessExited(OwnedProcess owned)
    {
        RemoveTrackedProcess(owned);
        if (Volatile.Read(ref owned.DisposeWhenExited) != 0)
        {
            try
            {
                owned.Process.Dispose();
            }
            catch
            {
                // The wrapper may have been disposed concurrently by orderly cleanup.
            }
        }
    }

    private OwnedProcess? FindTrackedProcess(Process process)
    {
        int processId;
        try
        {
            processId = process.Id;
        }
        catch
        {
            return null;
        }

        return _processes.TryGetValue(processId, out OwnedProcess? owned) &&
               ReferenceEquals(owned.Process, process)
            ? owned
            : null;
    }

    private void RemoveTrackedProcess(OwnedProcess owned)
    {
        if (owned.ProcessId <= 0)
        {
            return;
        }

        ((ICollection<KeyValuePair<int, OwnedProcess>>)_processes).Remove(
            new KeyValuePair<int, OwnedProcess>(owned.ProcessId, owned));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            return;
        }
        catch
        {
            // Fall through to a direct kill for platforms/runtimes that do not
            // support process-tree termination.
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Best effort. Job Object closure remains the final Windows backstop.
        }
    }

    private static bool TryWaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            int milliseconds = timeout >= TimeSpan.FromMilliseconds(int.MaxValue)
                ? int.MaxValue
                : (int)Math.Ceiling(timeout.TotalMilliseconds);
            return process.WaitForExit(Math.Max(0, milliseconds));
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static TimeSpan NormalizeTimeout(TimeSpan? timeout)
    {
        TimeSpan value = timeout ?? DefaultTerminationTimeout;
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The termination timeout cannot be negative.");
        }

        return value > TimeSpan.FromMilliseconds(int.MaxValue)
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : value;
    }

    private static string ResolveExecutableCore(string requestedExecutable)
    {
        string? executable = FindExecutable(requestedExecutable);
        if (executable == null)
        {
            return requestedExecutable;
        }

        if (TryResolveChocolateyShim(executable, out string? directExecutable))
        {
            Logger.Info($"Using the direct FFmpeg executable behind the Chocolatey shim: {directExecutable}");
            return directExecutable!;
        }

        return executable;
    }

    private static string? FindExecutable(string requestedExecutable)
    {
        if (Path.IsPathFullyQualified(requestedExecutable) ||
            requestedExecutable.Contains(Path.DirectorySeparatorChar) ||
            requestedExecutable.Contains(Path.AltDirectorySeparatorChar))
        {
            try
            {
                string fullPath = Path.GetFullPath(requestedExecutable);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        string executableName = Path.HasExtension(requestedExecutable)
            ? requestedExecutable
            : requestedExecutable + ".exe";
        IEnumerable<string> searchDirectories = new[] { AppContext.BaseDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => path.Trim().Trim('"')));

        foreach (string directory in searchDirectories)
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static bool TryResolveChocolateyShim(string shimPath, out string? directExecutable)
    {
        directExecutable = null;
        DirectoryInfo? binDirectory = new FileInfo(shimPath).Directory;
        DirectoryInfo? chocolateyRoot = binDirectory?.Parent;
        if (binDirectory == null || chocolateyRoot == null ||
            !binDirectory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            !IsChocolateyShim(shimPath))
        {
            return false;
        }

        string libraryRoot = Path.Combine(chocolateyRoot.FullName, "lib");
        if (!Directory.Exists(libraryRoot))
        {
            return false;
        }

        foreach (string metadataPath in new[]
                 {
                     shimPath + ".shim",
                     Path.ChangeExtension(shimPath, ".shim")
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? metadataTarget = TryReadShimTarget(metadataPath);
            if (IsSafeChocolateyTarget(metadataTarget, libraryRoot))
            {
                directExecutable = Path.GetFullPath(metadataTarget!);
                return true;
            }
        }

        string packageRoot = Path.Combine(libraryRoot, "ffmpeg");
        if (!Directory.Exists(packageRoot))
        {
            return false;
        }

        try
        {
            directExecutable = Directory
                .EnumerateFiles(packageRoot, "ffmpeg.exe", SearchOption.AllDirectories)
                .Where(path => IsSafeChocolateyTarget(path, libraryRoot))
                .OrderByDescending(static path =>
                    path.EndsWith(
                        $"tools{Path.DirectorySeparatorChar}ffmpeg{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}ffmpeg.exe",
                        StringComparison.OrdinalIgnoreCase))
                .ThenBy(static path => path.Length)
                .FirstOrDefault();
            return directExecutable != null;
        }
        catch
        {
            directExecutable = null;
            return false;
        }
    }

    private static bool IsChocolateyShim(string path)
    {
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            return (version.ProductName?.Contains("Chocolatey Shim", StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (version.FileDescription?.Contains("ShimGen", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadShimTarget(string metadataPath)
    {
        try
        {
            foreach (string line in File.ReadLines(metadataPath))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0 ||
                    !line[..separator].Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = line[(separator + 1)..].Trim().Trim('"');
                return Environment.ExpandEnvironmentVariables(value);
            }
        }
        catch
        {
            // Modern Chocolatey shims can embed their target and have no sidecar.
        }

        return null;
    }

    private static bool IsSafeChocolateyTarget(string? candidate, string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            string fullCandidate = Path.GetFullPath(candidate);
            string fullLibraryRoot = Path.GetFullPath(libraryRoot);
            string relative = Path.GetRelativePath(fullLibraryRoot, fullCandidate);
            return File.Exists(fullCandidate) &&
                   Path.GetFileName(fullCandidate).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                   !Path.IsPathRooted(relative) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private sealed class OwnedProcess(Process process)
    {
        internal Process Process { get; } = process;
        internal int ProcessId { get; set; }
        internal int DisposeWhenExited;
    }

    private sealed class KillOnCloseJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private SafeJobHandle? _handle;

        private KillOnCloseJob(SafeJobHandle handle)
        {
            _handle = handle;
        }

        internal static KillOnCloseJob? TryCreate(out string? error)
        {
            error = null;
            if (!OperatingSystem.IsWindows())
            {
                error = "Windows Job Objects are not supported on this operating system.";
                return null;
            }

            SafeJobHandle handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                handle.Dispose();
                return null;
            }

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            int informationLength = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref information,
                    (uint)informationLength))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                handle.Dispose();
                return null;
            }

            return new KillOnCloseJob(handle);
        }

        internal bool TryAssign(Process process, out string? error)
        {
            error = null;
            SafeJobHandle? handle = Volatile.Read(ref _handle);
            if (handle == null || handle.IsClosed || handle.IsInvalid)
            {
                error = "The FFmpeg Job Object is already closed.";
                return false;
            }

            try
            {
                if (NativeMethods.AssignProcessToJobObject(handle, process.SafeHandle))
                {
                    return true;
                }

                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
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

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeJobHandle job,
            JobObjectInformationClass informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeJobHandle job,
            SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
