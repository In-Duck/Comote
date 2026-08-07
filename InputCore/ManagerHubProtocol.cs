using System.Security.Cryptography;
using System.Text;

namespace Comote.Input;

public static class ManagerHubProtocol
{
    public const int RoutingIdBytes = 16;
    public const string RoutingIdPrefix = "cmt2-";
    public const int RoutingIdLength = 5 + (RoutingIdBytes * 2);
    public const int RoutingPrefaceLength = 8 + RoutingIdBytes;

    private static readonly byte[] RoutingPrefaceMagic =
        "CMTHR002"u8.ToArray();
    private static readonly byte[] RoutingDerivationLabel =
        Encoding.ASCII.GetBytes("Comote.ManagerHub.RoutingId.v2");

    public static string DeriveRoutingId(string accessKey)
    {
        if (!ComoteAccessKey.TryParse(accessKey, out var key))
        {
            throw new ArgumentException(
                "The access key must be a canonical CMT1 256-bit key.",
                nameof(accessKey));
        }

        try
        {
            return DeriveRoutingId(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static string DeriveRoutingId(ReadOnlySpan<byte> accessKey)
    {
        ValidateAccessKey(accessKey);
        var digest = HMACSHA256.HashData(
            accessKey,
            RoutingDerivationLabel);
        try
        {
            return FormatRoutingId(digest.AsSpan(0, RoutingIdBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public static string CreateSecureContext(string routingId)
    {
        if (!IsCanonicalRoutingId(routingId))
        {
            throw new ArgumentException(
                "The Manager Hub routing ID is invalid.",
                nameof(routingId));
        }

        return $"comote-manager-hub-v2:{routingId}";
    }

    public static async Task WriteRoutingPrefaceAsync(
        Stream stream,
        string routingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var preface = CreateRoutingPreface(routingId);
        try
        {
            await stream.WriteAsync(preface, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preface);
        }
    }

    public static async Task<string> ReadRoutingPrefaceAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var preface = new byte[RoutingPrefaceLength];
        try
        {
            await ReadExactAsync(stream, preface, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    preface.AsSpan(0, RoutingPrefaceMagic.Length),
                    RoutingPrefaceMagic))
            {
                throw new IOException(
                    "The Manager Hub routing preface is invalid.");
            }

            return FormatRoutingId(
                preface.AsSpan(
                    RoutingPrefaceMagic.Length,
                    RoutingIdBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preface);
        }
    }

    public static bool IsCanonicalRoutingId(string? routingId)
    {
        if (routingId is null ||
            routingId.Length != RoutingIdLength ||
            !routingId.StartsWith(
                RoutingIdPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in routingId.AsSpan(RoutingIdPrefix.Length))
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] CreateRoutingPreface(string routingId)
    {
        if (!IsCanonicalRoutingId(routingId))
        {
            throw new ArgumentException(
                "The Manager Hub routing ID is invalid.",
                nameof(routingId));
        }

        var preface = new byte[RoutingPrefaceLength];
        byte[]? routingBytes = null;
        try
        {
            RoutingPrefaceMagic.CopyTo(preface, 0);
            routingBytes = Convert.FromHexString(
                routingId[RoutingIdPrefix.Length..]);
            if (routingBytes.Length != RoutingIdBytes)
            {
                throw new ArgumentException(
                    "The Manager Hub routing ID is invalid.",
                    nameof(routingId));
            }
            routingBytes.CopyTo(preface, RoutingPrefaceMagic.Length);
            return preface;
        }
        catch (FormatException ex)
        {
            CryptographicOperations.ZeroMemory(preface);
            throw new ArgumentException(
                "The Manager Hub routing ID is invalid.",
                nameof(routingId),
                ex);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(preface);
            throw;
        }
        finally
        {
            if (routingBytes is not null)
            {
                CryptographicOperations.ZeroMemory(routingBytes);
            }
        }
    }

    private static string FormatRoutingId(ReadOnlySpan<byte> routingBytes)
    {
        if (routingBytes.Length != RoutingIdBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(routingBytes));
        }

        return RoutingIdPrefix +
            Convert.ToHexString(routingBytes).ToLowerInvariant();
    }

    private static void ValidateAccessKey(ReadOnlySpan<byte> accessKey)
    {
        if (accessKey.Length != ComoteAccessKey.KeySize)
        {
            throw new ArgumentException(
                "The access key must contain exactly 32 bytes.",
                nameof(accessKey));
        }
    }

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer[offset..],
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException(
                    "The Manager Hub routing preface ended unexpectedly.");
            }

            offset += count;
        }
    }
}
