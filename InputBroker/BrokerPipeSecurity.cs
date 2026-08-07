using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Comote.InputBroker;

internal static class BrokerPipeSecurity
{
    public static NamedPipeServerStream CreateServer()
    {
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);

        // The owner is set explicitly rather than left to the token default,
        // because clients authenticate this pipe by requiring a LocalSystem
        // owner before they send any input.
        security.SetOwner(localSystem);
        security.AddAccessRule(
            new PipeAccessRule(
                localSystem,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        var controllerSid = TryGetControllerGroupSid();
        if (controllerSid is not null)
        {
            security.AddAccessRule(
                new PipeAccessRule(
                    controllerSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            Comote.Input.BrokerProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
                PipeOptions.WriteThrough |
                PipeOptions.FirstPipeInstance,
            4096,
            4096,
            security,
            HandleInheritability.None,
            PipeAccessRights.ReadWrite);
    }

    private static SecurityIdentifier? TryGetControllerGroupSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(
                Environment.MachineName,
                Comote.Input.BrokerProtocol.ControllerGroupName)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }
}
