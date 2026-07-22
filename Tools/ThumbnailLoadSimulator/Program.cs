using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Comote.Shared;

ProtocolSelfTest.Run();
var options = SimulatorOptions.Parse(args);
var profile = options.UseAutomaticProfile
    ? MonitoringProfile.CreateAutomatic(options.Hosts)
    : new MonitoringProfile(
        options.Width,
        options.Height,
        options.FramesPerSecond,
        options.BitrateKbps * 1_000);

Console.WriteLine(
    $"Comote thumbnail load simulation: {options.Hosts} hosts, " +
    $"{profile.Width}x{profile.Height}, {profile.FramesPerSecond} FPS, " +
    $"{profile.TargetBitrateBitsPerSecond / 1000} Kbps/host, " +
    $"{options.DurationSeconds}s");

var simulator = new ThumbnailLoadSimulator(options.Hosts, profile);
var report = await simulator.RunAsync(TimeSpan.FromSeconds(options.DurationSeconds));
report.Print();
return report.ProtocolErrors == 0 && report.ConsumedFrames > 0 ? 0 : 1;

internal static class ProtocolSelfTest
{
    public static void Run()
    {
        const string deviceId = "self-test-client";
        var payload = new byte[] { 1, 2, 3, 4 };
        var packet = new byte[MonitoringProtocol.GetPacketSize(
            deviceId, payload.Length)];
        var header = new MonitoringPacketHeader(
            MonitoringMessageType.VideoFrame,
            MonitoringPacketFlags.Keyframe,
            MonitoringCodec.H264AnnexB,
            15,
            160,
            90,
            deviceId,
            1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload.Length);
        MonitoringProtocol.WritePacket(packet, header, payload);
        RequireValid(packet, "round trip");

        var corrupted = packet.ToArray();
        corrupted[0] ^= 0xff;
        RequireInvalid(corrupted, "bad magic");
        RequireInvalid(packet.AsSpan(0, packet.Length - 1), "truncated payload");

        corrupted = packet.ToArray();
        corrupted[6] = 0xff;
        RequireInvalid(corrupted, "unknown message type");

        corrupted = packet.ToArray();
        corrupted[MonitoringProtocol.FixedHeaderSize] = 0xff;
        RequireInvalid(corrupted, "invalid UTF-8 device id");
        Console.WriteLine("Monitoring protocol guard tests: PASS");
    }

    private static void RequireValid(ReadOnlySpan<byte> packet, string caseName)
    {
        if (!MonitoringProtocol.TryReadHeader(
                packet, out _, out _, out _, out var error))
            throw new InvalidOperationException(
                $"Protocol self-test '{caseName}' failed: {error}");
    }

    private static void RequireInvalid(ReadOnlySpan<byte> packet, string caseName)
    {
        if (MonitoringProtocol.TryReadHeader(
                packet, out _, out _, out _, out _))
            throw new InvalidOperationException(
                $"Protocol self-test '{caseName}' accepted invalid data.");
    }
}
internal sealed record SimulatorOptions(
    int Hosts,
    int DurationSeconds,
    ushort Width,
    ushort Height,
    byte FramesPerSecond,
    int BitrateKbps,
    bool UseAutomaticProfile)
{
    public static SimulatorOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[index][2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--")
                ? args[++index]
                : "true";
            values[key] = value;
        }

        var hosts = ReadInt(values, "hosts", 100, 1, 2_000);
        var seconds = ReadInt(values, "seconds", 10, 1, 600);
        var automatic = !values.ContainsKey("fps") &&
                        !values.ContainsKey("width") &&
                        !values.ContainsKey("bitrate-kbps");
        return new SimulatorOptions(
            hosts,
            seconds,
            (ushort)ReadInt(values, "width", 160, 64, 1920),
            (ushort)ReadInt(values, "height", 90, 36, 1080),
            (byte)ReadInt(values, "fps", 15, 1, 60),
            ReadInt(values, "bitrate-kbps", 80, 10, 8_000),
            automatic);
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException(
                $"--{key} must be between {minimum} and {maximum}.");
        return parsed;
    }
}

internal sealed class ThumbnailLoadSimulator
{
    private readonly int _hostCount;
    private readonly MonitoringProfile _profile;
    private readonly SimulatedFrame?[] _latestFrames;
    private readonly string[] _deviceIds;
    private readonly ConcurrentBag<double> _latencies = new();
    private long _producedFrames;
    private long _consumedFrames;
    private long _droppedFrames;
    private long _protocolErrors;
    private long _wireBytes;
    private long _renderBatches;

    public ThumbnailLoadSimulator(int hostCount, MonitoringProfile profile)
    {
        _hostCount = hostCount;
        _profile = profile;
        _latestFrames = new SimulatedFrame[hostCount];
        _deviceIds = Enumerable.Range(1, hostCount)
            .Select(index => $"load-host-{index:D4}")
            .ToArray();
    }

    public async Task<SimulationReport> RunAsync(TimeSpan duration)
    {
        var process = Process.GetCurrentProcess();
        var startCpu = process.TotalProcessorTime;
        var startAllocated = GC.GetTotalAllocatedBytes();
        var startCollections = new[]
        {
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
        };
        var stopwatch = Stopwatch.StartNew();

        await Task.WhenAll(
            ProduceAsync(duration, stopwatch),
            ConsumeAsync(duration, stopwatch));
        DrainLatestFrames();

        stopwatch.Stop();
        process.Refresh();
        var cpuPercent =
            (process.TotalProcessorTime - startCpu).TotalSeconds /
            stopwatch.Elapsed.TotalSeconds /
            Environment.ProcessorCount * 100;
        var latencyValues = _latencies.OrderBy(value => value).ToArray();

        return new SimulationReport(
            _hostCount,
            _profile,
            stopwatch.Elapsed,
            Interlocked.Read(ref _producedFrames),
            Interlocked.Read(ref _consumedFrames),
            Interlocked.Read(ref _droppedFrames),
            Interlocked.Read(ref _protocolErrors),
            Interlocked.Read(ref _wireBytes),
            Interlocked.Read(ref _renderBatches),
            cpuPercent,
            process.WorkingSet64,
            GC.GetTotalAllocatedBytes() - startAllocated,
            GC.CollectionCount(0) - startCollections[0],
            GC.CollectionCount(1) - startCollections[1],
            GC.CollectionCount(2) - startCollections[2],
            Percentile(latencyValues, 0.50),
            Percentile(latencyValues, 0.95),
            Percentile(latencyValues, 0.99));
    }

    private async Task ProduceAsync(TimeSpan duration, Stopwatch stopwatch)
    {
        var interval = TimeSpan.FromSeconds(1d / _profile.FramesPerSecond);
        var nextFrameAt = TimeSpan.Zero;
        long sequence = 0;
        var averagePayload = Math.Max(
            64,
            _profile.TargetBitrateBitsPerSecond /
            8 /
            _profile.FramesPerSecond);

        while (stopwatch.Elapsed < duration)
        {
            var tick = Interlocked.Increment(ref sequence);
            var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var hostIndex = 0; hostIndex < _hostCount; hostIndex++)
            {
                var jitter = 0.75 + ((hostIndex * 17 + tick * 13) % 51) / 100d;
                var payloadLength = Math.Max(64, (int)(averagePayload * jitter));
                var id = _deviceIds[hostIndex];
                var idLength = Encoding.UTF8.GetByteCount(id);
                var packetLength = MonitoringProtocol.GetPacketSize(id, payloadLength);
                var buffer = ArrayPool<byte>.Shared.Rent(packetLength);
                var payload = buffer.AsSpan(
                    MonitoringProtocol.FixedHeaderSize + idLength,
                    payloadLength);
                payload.Fill((byte)((hostIndex + tick) & 0xff));

                var header = new MonitoringPacketHeader(
                    MonitoringMessageType.VideoFrame,
                    tick % (_profile.FramesPerSecond * 3) == 1
                        ? MonitoringPacketFlags.Keyframe
                        : MonitoringPacketFlags.None,
                    MonitoringCodec.H264AnnexB,
                    _profile.FramesPerSecond,
                    _profile.Width,
                    _profile.Height,
                    id,
                    tick,
                    capturedAt,
                    payloadLength);
                MonitoringProtocol.WritePacket(buffer, header, payload);
                var frame = new SimulatedFrame(
                    buffer,
                    packetLength,
                    Stopwatch.GetTimestamp());
                var replaced = Interlocked.Exchange(
                    ref _latestFrames[hostIndex], frame);
                if (replaced != null)
                {
                    replaced.Dispose();
                    Interlocked.Increment(ref _droppedFrames);
                }
                Interlocked.Increment(ref _producedFrames);
                Interlocked.Add(ref _wireBytes, packetLength);
            }

            nextFrameAt += interval;
            var delay = nextFrameAt - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            else
                await Task.Yield();
        }
    }

    private async Task ConsumeAsync(TimeSpan duration, Stopwatch stopwatch)
    {
        var renderInterval = TimeSpan.FromSeconds(1d / 60);
        var nextRenderAt = TimeSpan.Zero;
        while (stopwatch.Elapsed < duration || HasPendingFrames())
        {
            var consumedThisBatch = 0;
            for (var hostIndex = 0; hostIndex < _hostCount; hostIndex++)
            {
                var frame = Interlocked.Exchange(
                    ref _latestFrames[hostIndex], null);
                if (frame == null) continue;
                try
                {
                    if (!MonitoringProtocol.TryReadHeader(
                            frame.Buffer.AsSpan(0, frame.Length),
                            out var header,
                            out var payloadOffset,
                            out var packetLength,
                            out _) ||
                        header.DeviceId != _deviceIds[hostIndex] ||
                        packetLength != frame.Length ||
                        payloadOffset + header.PayloadLength != frame.Length)
                    {
                        Interlocked.Increment(ref _protocolErrors);
                        continue;
                    }

                    var payload = frame.Buffer.AsSpan(
                        payloadOffset, header.PayloadLength);
                    var checksum = 0;
                    for (var offset = 0; offset < payload.Length; offset += 64)
                        checksum ^= payload[offset];
                    GC.KeepAlive(checksum);

                    _latencies.Add(
                        Stopwatch.GetElapsedTime(frame.CreatedTimestamp)
                            .TotalMilliseconds);
                    Interlocked.Increment(ref _consumedFrames);
                    consumedThisBatch++;
                }
                finally
                {
                    frame.Dispose();
                }
            }

            if (consumedThisBatch > 0)
                Interlocked.Increment(ref _renderBatches);
            nextRenderAt += renderInterval;
            var delay = nextRenderAt - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            else
                await Task.Yield();
        }
    }

    private bool HasPendingFrames()
    {
        for (var index = 0; index < _latestFrames.Length; index++)
        {
            if (Volatile.Read(ref _latestFrames[index]) != null) return true;
        }
        return false;
    }

    private void DrainLatestFrames()
    {
        for (var index = 0; index < _latestFrames.Length; index++)
            Interlocked.Exchange(ref _latestFrames[index], null)?.Dispose();
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}

internal sealed class SimulatedFrame(byte[] buffer, int length, long createdTimestamp)
    : IDisposable
{
    public byte[] Buffer { get; } = buffer;
    public int Length { get; } = length;
    public long CreatedTimestamp { get; } = createdTimestamp;

    public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
}

internal sealed record SimulationReport(
    int Hosts,
    MonitoringProfile Profile,
    TimeSpan Elapsed,
    long ProducedFrames,
    long ConsumedFrames,
    long DroppedFrames,
    long ProtocolErrors,
    long WireBytes,
    long RenderBatches,
    double CpuPercent,
    long WorkingSetBytes,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds,
    double P99LatencyMilliseconds)
{
    public void Print()
    {
        var expectedFrames = Hosts * Profile.FramesPerSecond * Elapsed.TotalSeconds;
        var deliveredPercent = expectedFrames <= 0
            ? 0
            : ConsumedFrames / expectedFrames * 100;
        var megabitsPerSecond =
            WireBytes * 8d / 1_000_000d / Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"Produced frames : {ProducedFrames:N0}");
        Console.WriteLine(
            $"Consumed frames : {ConsumedFrames:N0} " +
            $"({deliveredPercent:F1}% of target)");
        Console.WriteLine($"Latest-only drop: {DroppedFrames:N0}");
        Console.WriteLine($"Protocol errors : {ProtocolErrors:N0}");
        Console.WriteLine($"Wire throughput : {megabitsPerSecond:F2} Mbps");
        Console.WriteLine($"Render batches  : {RenderBatches:N0}");
        Console.WriteLine($"Process CPU     : {CpuPercent:F2}%");
        Console.WriteLine(
            $"Working set     : {WorkingSetBytes / 1024d / 1024d:F1} MB");
        Console.WriteLine(
            $"Managed alloc.  : {AllocatedBytes / 1024d / 1024d:F1} MB");
        Console.WriteLine(
            $"GC collections  : {Gen0Collections}/{Gen1Collections}/{Gen2Collections}");
        Console.WriteLine(
            $"Queue latency   : p50 {P50LatencyMilliseconds:F1} ms, " +
            $"p95 {P95LatencyMilliseconds:F1} ms, " +
            $"p99 {P99LatencyMilliseconds:F1} ms");
        Console.WriteLine();
        Console.WriteLine(
            "This measures protocol, scheduling, pooling, and latest-frame " +
            "backpressure. Real H.264 and GPU rendering are measured next.");
    }
}
