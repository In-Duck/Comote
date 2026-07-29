using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Host;

internal static class SecureDesktopService
{
    internal const string ServiceName = "ComoteHost";

    public static Task RunAsync(string[] args) =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(Array.Empty<string>())
            .UseWindowsService(options => options.ServiceName = ServiceName)
            .ConfigureServices(services =>
                services.AddHostedService<SecureDesktopAgentSupervisor>())
            .Build()
            .RunAsync();

    public static bool IsSystemAgent() => WindowsIdentity.GetCurrent().IsSystem;

    public static bool IsInstalled() =>
        Program.RunServiceControl("query", ServiceName) == 0;

    public static bool Restart()
    {
        Program.RunServiceControl("stop", ServiceName);
        Thread.Sleep(750);
        return Program.RunServiceControl("start", ServiceName) == 0;
    }
}

internal sealed class SecureDesktopAgentSupervisor : BackgroundService
{
    private readonly IntPtr _job = NativeMethods.CreateKillOnCloseJob();
    private Process? _agent;
    private uint _sessionId = uint.MaxValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeSession = NativeMethods.WTSGetActiveConsoleSessionId();
                if (activeSession != 0xFFFFFFFF &&
                    (_agent == null || _agent.HasExited || activeSession != _sessionId))
                {
                    StopAgent();
                    _agent = SystemSessionProcess.Start(activeSession, _job);
                    _sessionId = activeSession;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Service] Agent supervisor: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopAgent();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        StopAgent();
        if (_job != IntPtr.Zero) NativeMethods.CloseHandle(_job);
        base.Dispose();
    }

    private void StopAgent()
    {
        try
        {
            if (_agent is { HasExited: false }) _agent.Kill(true);
            _agent?.Dispose();
        }
        catch { }
        finally
        {
            _agent = null;
            _sessionId = uint.MaxValue;
        }
    }
}

internal static class SystemSessionProcess
{
    public static Process Start(uint sessionId, IntPtr job)
    {
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Executable path is unavailable.");

        using var processToken = WindowsHandle.From(
            NativeMethods.OpenProcessTokenForCurrentProcess());
        NativeMethods.EnablePrivilege(processToken.Value, "SeTcbPrivilege");
        NativeMethods.EnablePrivilege(processToken.Value, "SeAssignPrimaryTokenPrivilege");
        NativeMethods.EnablePrivilege(processToken.Value, "SeIncreaseQuotaPrivilege");

        if (!NativeMethods.DuplicateTokenEx(
                processToken.Value, NativeMethods.MAXIMUM_ALLOWED, IntPtr.Zero,
                2, 1, out var duplicated))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using var primaryToken = WindowsHandle.From(duplicated);
        var tokenSession = sessionId;
        if (!NativeMethods.SetTokenInformation(
                primaryToken.Value, 12, ref tokenSession, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!NativeMethods.CreateEnvironmentBlock(
                    out environment, primaryToken.Value, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new NativeMethods.STARTUPINFO
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };
            var commandLine = $"\"{executable}\" --system-agent --nogui";
            if (!NativeMethods.CreateProcessAsUser(
                    primaryToken.Value, executable, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    0x00000004 | 0x00000400 | 0x08000000, environment,
                    Path.GetDirectoryName(executable), ref startup,
                    out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                if (!NativeMethods.AssignProcessToJobObject(
                        job, processInfo.hProcess))
                {
                    NativeMethods.TerminateProcess(processInfo.hProcess, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (NativeMethods.ResumeThread(processInfo.hThread) == uint.MaxValue)
                {
                    NativeMethods.TerminateProcess(processInfo.hProcess, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return Process.GetProcessById((int)processInfo.dwProcessId);
            }
            finally
            {
                NativeMethods.CloseHandle(processInfo.hThread);
                NativeMethods.CloseHandle(processInfo.hProcess);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
                NativeMethods.DestroyEnvironmentBlock(environment);
        }
    }
}

internal sealed class WindowsHandle : IDisposable
{
    private WindowsHandle(IntPtr value) => Value = value;
    public IntPtr Value { get; }
    public static WindowsHandle From(IntPtr value) =>
        value == IntPtr.Zero
            ? throw new Win32Exception(Marshal.GetLastWin32Error())
            : new WindowsHandle(value);
    public void Dispose() => NativeMethods.CloseHandle(Value);
}

internal static class NativeMethods
{
    internal const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }


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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(
        IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int informationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information,
        int informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(
        IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    internal static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        information.BasicLimitInformation.LimitFlags = 0x00002000;
        if (SetInformationJobObject(
                job, 9, ref information,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            return job;
        var error = Marshal.GetLastWin32Error();
        CloseHandle(job);
        throw new Win32Exception(error);
    }
    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    internal static IntPtr OpenProcessTokenForCurrentProcess()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                MAXIMUM_ALLOWED | TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return token;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool SetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass,
        ref uint tokenInformation, int tokenInformationLength);
    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(
        out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(
        string? systemName, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength,
        IntPtr previousState, IntPtr returnLength);

    internal static void EnablePrivilege(IntPtr token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var privileges = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED,
        };
        if (!AdjustTokenPrivileges(
                token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
