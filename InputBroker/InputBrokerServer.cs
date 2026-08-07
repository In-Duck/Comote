using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Comote.Input;

namespace Comote.InputBroker;

internal sealed class InputBrokerServer(Action<string> log)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = BrokerPipeSecurity.CreateServer();
                await pipe.WaitForConnectionAsync(
                    stoppingToken).ConfigureAwait(false);
                if (!IsAuthorizedLocalClient(pipe, out var identity))
                {
                    log("Rejected an unauthorized input-broker client.");
                    pipe.Disconnect();
                    continue;
                }

                log($"Input controller connected as {identity}.");
                await ServeClientAsync(
                    pipe,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log($"Input broker server loop failed: {ex.Message}");
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ServeClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        VirtualHidDevice? device = null;
        try
        {
            while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
            {
                using var watchdog =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken);
                watchdog.CancelAfter(
                    BrokerProtocol.WatchdogTimeoutMilliseconds);

                BrokerFrame? frame;
                try
                {
                    frame = await BrokerProtocol.ReadFrameAsync(
                        pipe,
                        watchdog.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    log("Input controller heartbeat timed out.");
                    break;
                }
                catch (InvalidDataException ex)
                {
                    log(
                        "Malformed broker frame revoked the controller " +
                        $"lease: {ex.Message}");
                    break;
                }

                if (frame is null)
                {
                    break;
                }

                var (response, revokeLease) = HandleFrame(
                    frame.Value,
                    ref device);
                await BrokerProtocol.WriteResponseAsync(
                    pipe,
                    frame.Value.RequestId,
                    frame.Value.Command,
                    response,
                    stoppingToken).ConfigureAwait(false);
                if (revokeLease)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            device?.Dispose();
            log("Input controller disconnected; all inputs were released.");
        }
    }

    private (BrokerResponse Response, bool RevokeLease) HandleFrame(
        BrokerFrame frame,
        ref VirtualHidDevice? device)
    {
        try
        {
            if (frame.Command == BrokerCommand.GetStatus)
            {
                if (frame.Payload.Length != 0)
                {
                    return Invalid("GetStatus requires an empty payload.");
                }
                device ??= VirtualHidDevice.Open();
                return (BrokerResponse.Success("DriverReady"), false);
            }
            if (frame.Command == BrokerCommand.Heartbeat)
            {
                if (frame.Payload.Length != 0)
                {
                    return Invalid("Heartbeat requires an empty payload.");
                }
                return (BrokerResponse.Success(), false);
            }

            device ??= VirtualHidDevice.Open();
            switch (frame.Command)
            {
                case BrokerCommand.SetKeyboardState:
                    if (frame.Payload.Length !=
                        BrokerProtocol.KeyboardPayloadSize)
                    {
                        return Invalid("Invalid keyboard payload size.");
                    }
                    device.SetKeyboardState(frame.Payload);
                    break;

                case BrokerCommand.MouseRelative:
                    if (frame.Payload.Length !=
                        BrokerProtocol.RelativeMousePayloadSize)
                    {
                        return Invalid("Invalid relative-mouse payload size.");
                    }
                    device.MouseRelative(frame.Payload);
                    break;

                case BrokerCommand.MouseAbsolute:
                    if (frame.Payload.Length !=
                        BrokerProtocol.AbsoluteMousePayloadSize)
                    {
                        return Invalid("Invalid absolute-mouse payload size.");
                    }
                    device.MouseAbsolute(frame.Payload);
                    break;

                case BrokerCommand.ReleaseAll:
                    if (frame.Payload.Length != 0)
                    {
                        return Invalid("ReleaseAll requires an empty payload.");
                    }
                    device.ReleaseAll();
                    break;

                default:
                    return Invalid("Unsupported broker command.");
            }

            return (BrokerResponse.Success(), false);
        }
        catch (ArgumentException ex)
        {
            return (
                new BrokerResponse(
                    BrokerStatus.InvalidRequest,
                    ex.Message),
                true);
        }
        catch (Exception ex) when (
            ex is Win32Exception or
            InvalidDataException or
            InvalidOperationException)
        {
            log(
                "The Virtual HID driver rejected a broker command: " +
                ex.Message);
            return (
                new BrokerResponse(
                    BrokerStatus.DriverRejected,
                    ex.Message),
                true);
        }
    }

    private static (BrokerResponse, bool) Invalid(string detail) =>
        (new BrokerResponse(BrokerStatus.InvalidRequest, detail), true);

    private static bool IsAuthorizedLocalClient(
        NamedPipeServerStream pipe,
        out string identityName)
    {
        identityName = "";
        const int computerNameCapacity = 256;
        var computerName = new StringBuilder(computerNameCapacity);

        // The Win32 buffer length is counted in bytes, not characters.
        if (!GetNamedPipeClientComputerName(
                pipe.SafePipeHandle,
                computerName,
                computerNameCapacity * sizeof(char)))
        {
            return false;
        }
        if (!IsLocalComputerName(computerName.ToString()))
        {
            return false;
        }

        var authorized = false;
        var capturedIdentityName = "";
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(
                ifImpersonating: true) ??
                throw new InvalidOperationException(
                    "The named-pipe client identity was unavailable.");
            capturedIdentityName = identity.Name;
            var localSystemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            if (identity.User?.Equals(localSystemSid) == true)
            {
                authorized = true;
                return;
            }

            var controllerSid = TryGetControllerGroupSid();
            if (controllerSid is null)
            {
                authorized = false;
                return;
            }
            authorized = new WindowsPrincipal(identity).IsInRole(
                controllerSid);
        });
        identityName = capturedIdentityName;
        return authorized;
    }

    internal static bool IsLocalComputerName(string? clientComputer)
    {
        var normalized = NormalizeComputerName(clientComputer);
        if (normalized == ".")
        {
            return true;
        }
        if (normalized.Length == 0)
        {
            return false;
        }

        var candidates = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            NormalizeComputerName(Environment.MachineName),
        };

        try
        {
            candidates.Add(NormalizeComputerName(Dns.GetHostName()));
        }
        catch (System.Net.Sockets.SocketException)
        {
        }

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var hostName = NormalizeComputerName(properties.HostName);
            var domainName = NormalizeComputerName(properties.DomainName);
            candidates.Add(hostName);
            if (hostName.Length > 0 && domainName.Length > 0)
            {
                candidates.Add($"{hostName}.{domainName}");
            }
        }
        catch (NetworkInformationException)
        {
        }

        return candidates.Contains(normalized);
    }

    internal static string NormalizeComputerName(string? computerName)
    {
        if (string.IsNullOrWhiteSpace(computerName))
        {
            return "";
        }

        var trimmed = computerName.Trim();
        if (trimmed == ".")
        {
            return trimmed;
        }

        return trimmed
            .TrimStart((char)0x5C)
            .TrimEnd('.');
    }
    private static SecurityIdentifier? TryGetControllerGroupSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(
                Environment.MachineName,
                BrokerProtocol.ControllerGroupName)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        StringBuilder clientComputerName,
        int clientComputerNameLength);
}
