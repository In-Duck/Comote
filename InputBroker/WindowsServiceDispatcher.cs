using System.Runtime.InteropServices;

namespace Comote.InputBroker;

internal static class WindowsServiceDispatcher
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;
    private const uint ServiceControlPreshutdown = 0x0000000F;

    private static ServiceMainDelegate? _serviceMain;
    private static HandlerDelegate? _handler;
    private static CancellationTokenSource? _cancellation;
    private static Func<CancellationToken, Task>? _run;
    private static IntPtr _statusHandle;
    private static uint _checkpoint;

    public static void Run(
        string serviceName,
        Func<CancellationToken, Task> run)
    {
        _run = run;
        _serviceMain = ServiceMain;
        var table = new[]
        {
            new ServiceTableEntry
            {
                ServiceName = serviceName,
                ServiceMain = _serviceMain,
            },
            new ServiceTableEntry(),
        };

        if (!StartServiceCtrlDispatcher(table))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "StartServiceCtrlDispatcher failed.");
        }
    }

    private static void ServiceMain(uint argumentCount, IntPtr arguments)
    {
        _ = argumentCount;
        _ = arguments;
        _handler = Handler;
        _statusHandle = RegisterServiceCtrlHandlerEx(
            "ComoteInputBroker",
            _handler,
            IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        SetStatus(ServiceStartPending, 3000);
        SetStatus(ServiceRunning, 0);
        try
        {
            _run!(_cancellation.Token).GetAwaiter().GetResult();
            SetStatus(ServiceStopped, 0);
        }
        catch (Exception ex)
        {
            BrokerLog.Write($"Service terminated: {ex.Message}");
            SetStatus(ServiceStopped, 0, 1);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private static uint Handler(
        uint control,
        uint eventType,
        IntPtr eventData,
        IntPtr context)
    {
        _ = eventType;
        _ = eventData;
        _ = context;
        if (control is
            ServiceControlStop or
            ServiceControlShutdown or
            ServiceControlPreshutdown)
        {
            SetStatus(ServiceStopPending, 3000);
            _cancellation?.Cancel();
        }
        return 0;
    }

    private static void SetStatus(
        uint state,
        uint waitHint,
        uint win32ExitCode = 0)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        _checkpoint =
            state is ServiceStartPending or ServiceStopPending
                ? _checkpoint + 1
                : 0;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted =
                state == ServiceRunning
                    ? ServiceAcceptStop | ServiceAcceptShutdown
                    : 0,
            Win32ExitCode = win32ExitCode,
            CheckPoint = _checkpoint,
            WaitHint = waitHint,
        };
        _ = SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;
        public ServiceMainDelegate? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(
        uint argumentCount,
        IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerDelegate(
        uint control,
        uint eventType,
        IntPtr eventData,
        IntPtr context);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(
        [In] ServiceTableEntry[] serviceTable);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        HandlerDelegate handler,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(
        IntPtr serviceStatusHandle,
        ref ServiceStatus serviceStatus);
}
