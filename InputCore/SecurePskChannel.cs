using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Comote.Input;

public static class ComoteAccessKey
{
    public const string Prefix = "CMT1-";
    public const int KeySize = 32;
    private const int EncodedLength = 43;

    public static string Generate()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            return Prefix + Convert.ToBase64String(key)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool TryParse(string? text, out byte[] key)
    {
        key = [];
        if (text is null ||
            !text.StartsWith(Prefix, StringComparison.Ordinal) ||
            text.Length != Prefix.Length + EncodedLength)
        {
            return false;
        }

        var payload = text[Prefix.Length..];
        foreach (var character in payload)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_'))
            {
                return false;
            }
        }

        var encoded = payload
            .Replace('-', '+')
            .Replace('_', '/') + "=";
        try
        {
            var decoded = Convert.FromBase64String(encoded);
            var canonical = Convert.ToBase64String(decoded)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            if (decoded.Length != KeySize ||
                !string.Equals(
                    canonical,
                    payload,
                    StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class SecurePskChannel : IAsyncDisposable
{
    public const int MaximumMessageSize = 1024 * 1024;

    private const int NonceSize = 32;
    private const int ProofSize = 32;
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const int FrameHeaderSize = 12;

    private static readonly byte[] ServerHelloMagic =
        "CMTSH001"u8.ToArray();
    private static readonly byte[] ClientAuthMagic =
        "CMTCA001"u8.ToArray();
    private static readonly byte[] ServerAuthMagic =
        "CMTSA001"u8.ToArray();
    private static readonly byte[] FrameMagic =
        "CMTF"u8.ToArray();

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly uint _sendNoncePrefix;
    private readonly uint _receiveNoncePrefix;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private ulong _sendSequence;
    private ulong _receiveSequence;
    private int _disposed;
    private int _faulted;

    private SecurePskChannel(
        Stream stream,
        bool leaveOpen,
        byte[] sendKey,
        byte[] receiveKey,
        uint sendNoncePrefix,
        uint receiveNoncePrefix)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _sendKey = sendKey;
        _receiveKey = receiveKey;
        _sendNoncePrefix = sendNoncePrefix;
        _receiveNoncePrefix = receiveNoncePrefix;
    }

    public static async Task<SecurePskChannel> AuthenticateClientAsync(
        Stream stream,
        ReadOnlyMemory<byte> accessKey,
        string context,
        CancellationToken cancellationToken = default,
        bool leaveOpen = false)
    {
        ValidateArguments(stream, accessKey.Span, context);
        var serverHello = new byte[ServerHelloMagic.Length + NonceSize];
        var clientNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[]? transcript = null;
        byte[]? clientProof = null;
        byte[]? expectedServerProof = null;
        byte[]? clientAuth = null;
        byte[]? serverAuth = null;
        try
        {
            await ReadExactAsync(stream, serverHello, cancellationToken);
            RequireMagic(serverHello, ServerHelloMagic);
            transcript = CreateTranscript(
                context,
                serverHello.AsSpan(ServerHelloMagic.Length),
                clientNonce);
            clientProof = ComputeMac(
                accessKey.Span,
                "client-proof"u8,
                transcript);
            expectedServerProof = ComputeMac(
                accessKey.Span,
                "server-proof"u8,
                transcript);

            clientAuth = new byte[
                ClientAuthMagic.Length + NonceSize + ProofSize];
            ClientAuthMagic.CopyTo(clientAuth, 0);
            clientNonce.CopyTo(clientAuth, ClientAuthMagic.Length);
            clientProof.CopyTo(
                clientAuth,
                ClientAuthMagic.Length + NonceSize);
            await stream.WriteAsync(clientAuth, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            serverAuth = new byte[
                ServerAuthMagic.Length + ProofSize];
            await ReadExactAsync(stream, serverAuth, cancellationToken);
            RequireMagic(serverAuth, ServerAuthMagic);
            if (!CryptographicOperations.FixedTimeEquals(
                    serverAuth.AsSpan(ServerAuthMagic.Length),
                    expectedServerProof))
            {
                throw new UnauthorizedAccessException(
                    "The remote endpoint did not prove possession of the Comote access key.");
            }

            return CreateChannel(
                stream,
                leaveOpen,
                accessKey.Span,
                transcript,
                isClient: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serverHello);
            CryptographicOperations.ZeroMemory(clientNonce);
            Zero(transcript);
            Zero(clientProof);
            Zero(expectedServerProof);
            Zero(clientAuth);
            Zero(serverAuth);
        }
    }

    public static async Task<SecurePskChannel> AuthenticateServerAsync(
        Stream stream,
        ReadOnlyMemory<byte> accessKey,
        string context,
        CancellationToken cancellationToken = default,
        bool leaveOpen = false)
    {
        ValidateArguments(stream, accessKey.Span, context);
        var serverNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var clientAuth = new byte[
            ClientAuthMagic.Length + NonceSize + ProofSize];
        byte[]? transcript = null;
        byte[]? expectedClientProof = null;
        byte[]? serverProof = null;
        byte[]? serverHello = null;
        byte[]? serverAuth = null;
        try
        {
            serverHello = new byte[
                ServerHelloMagic.Length + NonceSize];
            ServerHelloMagic.CopyTo(serverHello, 0);
            serverNonce.CopyTo(serverHello, ServerHelloMagic.Length);
            await stream.WriteAsync(serverHello, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            await ReadExactAsync(stream, clientAuth, cancellationToken);
            RequireMagic(clientAuth, ClientAuthMagic);
            transcript = CreateTranscript(
                context,
                serverNonce,
                clientAuth.AsSpan(ClientAuthMagic.Length, NonceSize));
            expectedClientProof = ComputeMac(
                accessKey.Span,
                "client-proof"u8,
                transcript);
            if (!CryptographicOperations.FixedTimeEquals(
                    clientAuth.AsSpan(
                        ClientAuthMagic.Length + NonceSize,
                        ProofSize),
                    expectedClientProof))
            {
                throw new UnauthorizedAccessException(
                    "The remote endpoint did not prove possession of the Comote access key.");
            }

            serverProof = ComputeMac(
                accessKey.Span,
                "server-proof"u8,
                transcript);
            serverAuth = new byte[
                ServerAuthMagic.Length + ProofSize];
            ServerAuthMagic.CopyTo(serverAuth, 0);
            serverProof.CopyTo(serverAuth, ServerAuthMagic.Length);
            await stream.WriteAsync(serverAuth, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            return CreateChannel(
                stream,
                leaveOpen,
                accessKey.Span,
                transcript,
                isClient: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serverNonce);
            CryptographicOperations.ZeroMemory(clientAuth);
            Zero(transcript);
            Zero(expectedClientProof);
            Zero(serverProof);
            Zero(serverHello);
            Zero(serverAuth);
        }
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (message.Length is <= 0 or > MaximumMessageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        await _sendLock.WaitAsync(linked.Token);
        try
        {
            ThrowIfUnavailable();
            if (_sendSequence == ulong.MaxValue)
            {
                throw Fault(
                    "The secure channel send sequence was exhausted.");
            }

            var sequence = ++_sendSequence;
            var frame = new byte[
                FrameHeaderSize + message.Length + GcmTagSize];
            BinaryPrimitives.WriteUInt32BigEndian(
                frame.AsSpan(0, 4),
                checked((uint)message.Length));
            BinaryPrimitives.WriteUInt64BigEndian(
                frame.AsSpan(4, 8),
                sequence);
            Span<byte> nonce = stackalloc byte[GcmNonceSize];
            CreateNonce(_sendNoncePrefix, sequence, nonce);
            Span<byte> aad = stackalloc byte[FrameHeaderSize + 4];
            CreateAad(
                checked((uint)message.Length),
                sequence,
                aad);
            using var aes = new AesGcm(_sendKey, GcmTagSize);
            aes.Encrypt(
                nonce,
                message.Span,
                frame.AsSpan(FrameHeaderSize, message.Length),
                frame.AsSpan(
                    FrameHeaderSize + message.Length,
                    GcmTagSize),
                aad);

            try
            {
                await _stream.WriteAsync(frame, linked.Token);
                await _stream.FlushAsync(linked.Token);
            }
            catch
            {
                Interlocked.Exchange(ref _faulted, 1);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        await _receiveLock.WaitAsync(linked.Token);
        try
        {
            ThrowIfUnavailable();
            var header = new byte[FrameHeaderSize];
            try
            {
                await ReadExactAsync(
                    _stream,
                    header,
                    linked.Token);
                var length = BinaryPrimitives.ReadUInt32BigEndian(
                    header.AsSpan(0, 4));
                var sequence =
                    BinaryPrimitives.ReadUInt64BigEndian(
                        header.AsSpan(4, 8));
                if (length is 0 or > MaximumMessageSize)
                {
                    throw Fault(
                        "The secure channel frame length is invalid.");
                }
                if (_receiveSequence == ulong.MaxValue ||
                    sequence != _receiveSequence + 1)
                {
                    throw Fault(
                        "The secure channel frame sequence is invalid.");
                }

                var encrypted = new byte[
                    checked((int)length) + GcmTagSize];
                await ReadExactAsync(
                    _stream,
                    encrypted,
                    linked.Token);
                var plaintext = new byte[length];
                Span<byte> nonce = stackalloc byte[GcmNonceSize];
                CreateNonce(_receiveNoncePrefix, sequence, nonce);
                Span<byte> aad = stackalloc byte[FrameHeaderSize + 4];
                CreateAad(length, sequence, aad);
                try
                {
                    using var aes =
                        new AesGcm(_receiveKey, GcmTagSize);
                    aes.Decrypt(
                        nonce,
                        encrypted.AsSpan(0, checked((int)length)),
                        encrypted.AsSpan(
                            checked((int)length),
                            GcmTagSize),
                        plaintext,
                        aad);
                    _receiveSequence = sequence;
                    return plaintext;
                }
                catch (CryptographicException ex)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    throw Fault(
                        "The secure channel frame authentication failed.",
                        ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                }
            }
            catch
            {
                Interlocked.Exchange(ref _faulted, 1);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(header);
            }
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    private static SecurePskChannel CreateChannel(
        Stream stream,
        bool leaveOpen,
        ReadOnlySpan<byte> accessKey,
        ReadOnlySpan<byte> transcript,
        bool isClient)
    {
        var clientToServer = Derive(
            accessKey,
            "client-to-server-key"u8,
            transcript);
        var serverToClient = Derive(
            accessKey,
            "server-to-client-key"u8,
            transcript);
        var clientNonce = Derive(
            accessKey,
            "client-to-server-nonce"u8,
            transcript);
        var serverNonce = Derive(
            accessKey,
            "server-to-client-nonce"u8,
            transcript);
        try
        {
            var clientPrefix =
                BinaryPrimitives.ReadUInt32BigEndian(clientNonce);
            var serverPrefix =
                BinaryPrimitives.ReadUInt32BigEndian(serverNonce);
            if (isClient)
            {
                return new SecurePskChannel(
                    stream,
                    leaveOpen,
                    clientToServer,
                    serverToClient,
                    clientPrefix,
                    serverPrefix);
            }

            return new SecurePskChannel(
                stream,
                leaveOpen,
                serverToClient,
                clientToServer,
                serverPrefix,
                clientPrefix);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(clientToServer);
            CryptographicOperations.ZeroMemory(serverToClient);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clientNonce);
            CryptographicOperations.ZeroMemory(serverNonce);
        }
    }

    private static byte[] CreateTranscript(
        string context,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> clientNonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(context);
        try
        {
            var transcript = new byte[
                1 + contextBytes.Length + NonceSize + NonceSize];
            transcript[0] = checked((byte)contextBytes.Length);
            contextBytes.CopyTo(transcript, 1);
            serverNonce.CopyTo(
                transcript.AsSpan(1 + contextBytes.Length, NonceSize));
            clientNonce.CopyTo(
                transcript.AsSpan(
                    1 + contextBytes.Length + NonceSize,
                    NonceSize));
            return transcript;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contextBytes);
        }
    }

    private static byte[] ComputeMac(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> transcript)
    {
        var input = new byte[label.Length + 1 + transcript.Length];
        label.CopyTo(input);
        input[label.Length] = 0;
        transcript.CopyTo(input.AsSpan(label.Length + 1));
        try
        {
            return HMACSHA256.HashData(key, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static byte[] Derive(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> transcript) =>
        ComputeMac(key, label, transcript);

    private static void CreateNonce(
        uint prefix,
        ulong sequence,
        Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[..4],
            prefix);
        BinaryPrimitives.WriteUInt64BigEndian(
            destination[4..],
            sequence);
    }

    private static void CreateAad(
        uint length,
        ulong sequence,
        Span<byte> destination)
    {
        FrameMagic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(4, 4),
            length);
        BinaryPrimitives.WriteUInt64BigEndian(
            destination.Slice(8, 8),
            sequence);
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
                    "The secure channel ended unexpectedly.");
            }
            offset += count;
        }
    }

    private static void RequireMagic(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> expected)
    {
        if (message.Length < expected.Length ||
            !message[..expected.Length].SequenceEqual(expected))
        {
            throw new IOException(
                "The remote endpoint does not support the required secure Comote protocol.");
        }
    }

    private static void ValidateArguments(
        Stream stream,
        ReadOnlySpan<byte> accessKey,
        string context)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException(
                "The stream must be readable and writable.",
                nameof(stream));
        }
        if (accessKey.Length != ComoteAccessKey.KeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(accessKey));
        }
        var contextLength = Encoding.UTF8.GetByteCount(context);
        if (contextLength is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }
    }

    private IOException Fault(
        string message,
        Exception? inner = null)
    {
        Interlocked.Exchange(ref _faulted, 1);
        return new IOException(message, inner);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new IOException(
                "The secure channel is faulted and cannot be reused.");
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        try
        {
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync();
            }
        }
        finally
        {
            await _sendLock.WaitAsync();
            try
            {
                await _receiveLock.WaitAsync();
                try
                {
                    CryptographicOperations.ZeroMemory(_sendKey);
                    CryptographicOperations.ZeroMemory(_receiveKey);
                }
                finally
                {
                    _receiveLock.Release();
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
