using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Host;

public record MonitorInfo(
    int AdapterIndex,
    int OutputIndex,
    string Name,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

public sealed class ScreenCapture : IDisposable
{
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

    private readonly int _adapterIndex;
    private readonly int _outputIndex;
    private readonly object _captureSync = new();
    private readonly AutoResetEvent _request = new(false);
    private readonly ManualResetEventSlim _response = new(false);
    private readonly ManualResetEventSlim _initialised = new(false);

    private Thread? _worker;
    private volatile bool _stopping;
    private volatile bool _restartRequested;
    private byte[]? _result;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private byte[]? _frameBuffer;
    private int _width;
    private int _height;
    private int _left;
    private int _top;

    public int Width => _width;
    public int Height => _height;
    public int Left => _left;
    public int Top => _top;
    public int AdapterIndex => _adapterIndex;
    public int OutputIndex => _outputIndex;

    public ScreenCapture(int adapterIndex = 0, int outputIndex = 0)
    {
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
        lock (_captureSync) StartWorker();
    }

    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        if (factory == null) return monitors;
        using (factory)
        {
            for (uint adapterIndex = 0; adapterIndex < 8; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out var adapter).Failure)
                    break;
                using (adapter)
                {
                    for (uint outputIndex = 0; outputIndex < 8; outputIndex++)
                    {
                        if (adapter.EnumOutputs(outputIndex, out var output).Failure)
                            break;
                        using (output)
                        {
                            var description = output.Description;
                            var bounds = description.DesktopCoordinates;
                            monitors.Add(new MonitorInfo(
                                (int)adapterIndex,
                                (int)outputIndex,
                                description.DeviceName?.Replace("\0", "").Trim() ??
                                    $"Monitor {monitors.Count + 1}",
                                bounds.Left,
                                bounds.Top,
                                bounds.Right - bounds.Left,
                                bounds.Bottom - bounds.Top,
                                bounds.Left == 0 && bounds.Top == 0));
                        }
                    }
                }
            }
        }
        return monitors;
    }

    public byte[]? Capture()
    {
        lock (_captureSync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                EnsureWorker();
                _result = null;
                _response.Reset();
                _request.Set();
                if (!_response.Wait(TimeSpan.FromSeconds(3)))
                {
                    _restartRequested = true;
                }

                if (!_restartRequested) return _result;
                RestartWorker();
            }
            return null;
        }
    }

    public byte[]? CaptureThumbnail(int targetWidth = 320, int quality = 75)
    {
        lock (_captureSync)
        {
            var raw = Capture();
            if (raw == null || _width <= 0 || _height <= 0) return null;
            try
            {
                using var bitmap = new Bitmap(
                    _width, _height, PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(
                    new Rectangle(0, 0, _width, _height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                Marshal.Copy(raw, 0, data.Scan0, raw.Length);
                bitmap.UnlockBits(data);

                var targetHeight = Math.Max(
                    1, (int)((double)_height / _width * targetWidth));
                using var thumbnail = new Bitmap(targetWidth, targetHeight);
                using (var graphics = Graphics.FromImage(thumbnail))
                {
                    graphics.InterpolationMode =
                        System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(bitmap, 0, 0, targetWidth, targetHeight);
                }

                using var stream = new MemoryStream();
                var encoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
                if (encoder == null) return null;
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(
                    Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
                thumbnail.Save(stream, encoder, parameters);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Capture] Thumbnail failed: {ex.Message}");
                return null;
            }
        }
    }

    private void EnsureWorker()
    {
        if (_worker is { IsAlive: true } && !_restartRequested) return;
        RestartWorker();
    }

    private void RestartWorker()
    {
        if (_worker is { IsAlive: true })
        {
            _restartRequested = true;
            _request.Set();
            if (!_worker.Join(TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine("[Capture] Previous desktop worker is still stopping.");
                return;
            }
        }
        StartWorker();
    }

    private void StartWorker()
    {
        _restartRequested = false;
        _initialised.Reset();
        _worker = new Thread(CaptureWorker)
        {
            IsBackground = true,
            Name = "Comote Desktop Capture",
        };
        _worker.Start();
        if (!_initialised.Wait(TimeSpan.FromSeconds(5)) || _width <= 0)
            Console.WriteLine("[Capture] The active desktop is not ready yet.");
    }

    private void CaptureWorker()
    {
        try
        {
            if (!SessionManager.SwitchToInputDesktop())
                throw new InvalidOperationException(
                    "The active Windows desktop could not be opened.");
            InitialiseCore();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Capture] Initialise failed: {ex.Message}");
            _restartRequested = true;
        }
        finally
        {
            _initialised.Set();
        }

        try
        {
            while (!_stopping && !_restartRequested)
            {
                _request.WaitOne();
                if (_stopping || _restartRequested) break;
                _result = CaptureCore();
                _response.Set();
            }
        }
        finally
        {
            CleanupCore();
            _response.Set();
        }
    }

    private void InitialiseCore()
    {
        CleanupCore();
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        if (factory == null) throw new InvalidOperationException("DXGI unavailable.");
        using (factory)
        {
            factory.EnumAdapters1((uint)_adapterIndex, out var adapter).CheckError();
            using (adapter)
            {
                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    null,
                    out _device).CheckError();
                if (_device == null)
                    throw new InvalidOperationException("D3D11 device creation returned null.");
                _context = _device.ImmediateContext;
                adapter.EnumOutputs((uint)_outputIndex, out var output).CheckError();
                using (output)
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                {
                    _duplication = output1.DuplicateOutput(_device);
                    var bounds = output.Description.DesktopCoordinates;
                    _width = bounds.Right - bounds.Left;
                    _height = bounds.Bottom - bounds.Top;
                    _left = bounds.Left;
                    _top = bounds.Top;
                }
            }
        }

        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        Console.WriteLine($"[Capture] Active desktop: {_width}x{_height}");
    }

    private byte[]? CaptureCore()
    {
        if (_duplication == null || _context == null || _stagingTexture == null)
            return null;

        IDXGIResource? desktopResource = null;
        var acquired = false;
        try
        {
            var result = _duplication.AcquireNextFrame(
                100, out _, out desktopResource);
            if (result.Failure)
            {
                if (result.Code == DxgiErrorAccessLost)
                {
                    Console.WriteLine("[Capture] Desktop changed; rebuilding capture.");
                    _restartRequested = true;
                }
                return null;
            }
            acquired = true;
            if (desktopResource == null) return null;
            using (var texture = desktopResource.QueryInterface<ID3D11Texture2D>())
                _context.CopyResource(_stagingTexture, texture);

            var mapped = _context.Map(
                _stagingTexture, 0, MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = _width * 4;
                var size = rowBytes * _height;
                if (_frameBuffer == null || _frameBuffer.Length != size)
                    _frameBuffer = new byte[size];
                for (var row = 0; row < _height; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.DataPointer, row * (int)mapped.RowPitch),
                        _frameBuffer,
                        row * rowBytes,
                        rowBytes);
                }
                return _frameBuffer;
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch (Exception ex)
        {
            if (ex.HResult is DxgiErrorAccessLost or DxgiErrorWaitTimeout)
                _restartRequested = ex.HResult == DxgiErrorAccessLost;
            else
            {
                Console.WriteLine(
                    $"[Capture] Runtime error 0x{ex.HResult:X}: {ex.Message}");
                _restartRequested = true;
            }
            return null;
        }
        finally
        {
            desktopResource?.Dispose();
            if (acquired)
            {
                try { _duplication.ReleaseFrame(); } catch { }
            }
        }
    }

    private void CleanupCore()
    {
        _duplication?.Dispose();
        _stagingTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _duplication = null;
        _stagingTexture = null;
        _context = null;
        _device = null;
    }

    public void Dispose()
    {
        lock (_captureSync)
        {
            if (_stopping) return;
            _stopping = true;
            _request.Set();
            _worker?.Join(TimeSpan.FromSeconds(3));
            _request.Dispose();
            _response.Dispose();
            _initialised.Dispose();
        }
    }
}
