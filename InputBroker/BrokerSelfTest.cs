using System.Buffers.Binary;

namespace Comote.InputBroker;

internal static class BrokerSelfTest
{
    public static void Run()
    {
        TestDriverResponses();
        TestComputerNameNormalization();
        Console.WriteLine(
            "InputBroker pure self-test passed. " +
            "No pipe, device, driver, or input API was opened.");
    }

    private static void TestDriverResponses()
    {
        var version = new byte[VirtualHidDevice.VersionResponseSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            version,
            VirtualHidDevice.VersionResponseSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            version.AsSpan(4),
            VirtualHidDevice.ProtocolVersion);
        VirtualHidDevice.ValidateVersionResponse(version);

        var capabilities =
            new byte[VirtualHidDevice.CapabilitiesResponseSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            capabilities,
            VirtualHidDevice.CapabilitiesResponseSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            capabilities.AsSpan(4),
            VirtualHidDevice.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            capabilities.AsSpan(8),
            0x000007FF);
        BinaryPrimitives.WriteUInt32LittleEndian(
            capabilities.AsSpan(12),
            6);
        BinaryPrimitives.WriteUInt32LittleEndian(
            capabilities.AsSpan(16),
            5);
        BinaryPrimitives.WriteInt32LittleEndian(
            capabilities.AsSpan(20),
            32767);
        BinaryPrimitives.WriteInt32LittleEndian(
            capabilities.AsSpan(24),
            127);
        BinaryPrimitives.WriteUInt32LittleEndian(
            capabilities.AsSpan(28),
            VirtualHidDevice.MaximumAbsoluteCoordinate);
        VirtualHidDevice.ValidateCapabilitiesResponse(capabilities);

        ExpectInvalidData(
            () => VirtualHidDevice.ValidateVersionResponse(
                version.AsSpan(0, version.Length - 1)),
            "truncated version response");

        var oldProtocol = version.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            oldProtocol.AsSpan(4),
            1);
        ExpectInvalidData(
            () => VirtualHidDevice.ValidateVersionResponse(oldProtocol),
            "protocol v1");

        var oldCapabilities = capabilities.AsSpan(0, 28).ToArray();
        ExpectInvalidData(
            () => VirtualHidDevice.ValidateCapabilitiesResponse(
                oldCapabilities),
            "28-byte capability response");

        var wrongAbsoluteMaximum = capabilities.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            wrongAbsoluteMaximum.AsSpan(28),
            65535);
        ExpectInvalidData(
            () => VirtualHidDevice.ValidateCapabilitiesResponse(
                wrongAbsoluteMaximum),
            "wrong absolute-coordinate maximum");
    }

    private static void TestComputerNameNormalization()
    {
        var machine = Environment.MachineName;
        Assert(
            InputBrokerServer.IsLocalComputerName($@"\\{machine}"),
            "UNC-style local computer name");
        Assert(
            InputBrokerServer.IsLocalComputerName($"{machine}."),
            "local computer name with a trailing dot");
        Assert(
            InputBrokerServer.IsLocalComputerName("."),
            "dot local-computer alias");
        Assert(
            !InputBrokerServer.IsLocalComputerName(
                "definitely-not-this-computer.invalid"),
            "remote computer rejection");
        Assert(
            InputBrokerServer.NormalizeComputerName($@"  \\{machine}.  ")
                .Equals(machine, StringComparison.OrdinalIgnoreCase),
            "computer-name normalization");
    }

    private static void ExpectInvalidData(
        Action action,
        string description)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Self-test failed: {description} was accepted.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description}.");
        }
    }
}
