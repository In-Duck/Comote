using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Host
{
    public record MonitorInfo(int AdapterIndex, int OutputIndex, string Name, int Width, int Height, bool IsPrimary);

    public class ScreenCapture : IDisposable
    {
        private ID3D11Device _device = null!;
        private ID3D11DeviceContext _context = null!;
        private IDXGIOutputDuplication? _outputDuplication;
        private ID3D11Texture2D? _stagingTexture;
        private int _width;
        private int _height;
        private int _adapterIndex;
        private int _outputIndex;
        private byte[]? _frameBuffer;

        private IDXGIResource? _desktopResource;
        private OutduplFrameInfo _duplicateFrameInformation;

        public int Width => _width;
        public int Height => _height;
        public int AdapterIndex => _adapterIndex;
        public int OutputIndex => _outputIndex;

        public static List<MonitorInfo> GetMonitors()
        {
            var monitors = new List<MonitorInfo>();
            DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
            if (factory == null) return monitors;

            try
            {
                for (uint ai = 0; ai < 8; ai++)
                {
                    if (factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Failure) break;
                    for (uint oi = 0; oi < 8; oi++)
                    {
                        if (adapter.EnumOutputs(oi, out IDXGIOutput output).Failure) break;
                        var desc = output.Description;
                        var bounds = desc.DesktopCoordinates;
                        int w = bounds.Right - bounds.Left;
                        int h = bounds.Bottom - bounds.Top;
                        bool isPrimary = bounds.Left == 0 && bounds.Top == 0;
                        string name = desc.DeviceName?.Replace("\0", "").Trim() ?? $"Monitor {monitors.Count + 1}";
                        monitors.Add(new MonitorInfo((int)ai, (int)oi, name, w, h, isPrimary));
                        output.Dispose();
                    }
                    adapter.Dispose();
                }
            }
            finally { factory.Dispose(); }
            return monitors;
        }

        public ScreenCapture(int adapterIndex = 0, int outputIndex = 0)
        {
            _adapterIndex = adapterIndex;
            _outputIndex = outputIndex;
            Initialise();
        }

        private void Initialise()
        {
            try
            {
                Cleanup();

                DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
                factory!.EnumAdapters1((uint)_adapterIndex, out IDXGIAdapter1 adapter).CheckError();
                
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out _device).CheckError();
                _context = _device.ImmediateContext;

                adapter.EnumOutputs((uint)_outputIndex, out IDXGIOutput output).CheckError();
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _outputDuplication = output1.DuplicateOutput(_device);

                var bounds = output.Description.DesktopCoordinates;
                _width = bounds.Right - bounds.Left;
                _height = bounds.Bottom - bounds.Top;
                output.Dispose();

                var desc = new Texture2DDescription
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
                    MiscFlags = ResourceOptionFlags.None
                };
                _stagingTexture = _device.CreateTexture2D(desc);

                adapter.Dispose();
                factory.Dispose();
                
                Console.WriteLine($"[Capture] Initialised: {_width}x{_height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Capture] Initialise failed: {ex.Message}");
            }
        }

        public byte[]? Capture()
        {
            try
            {
                // [무인 액세스] 활성 데스크톱 전환 시도
                SessionManager.SwitchToInputDesktop();

                if (_outputDuplication == null) Initialise();
                if (_outputDuplication == null) return null;

                var res = _outputDuplication.AcquireNextFrame(100, out _duplicateFrameInformation, out _desktopResource);
                if (res.Failure || _desktopResource == null) return null;

                using (var texture = _desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    _context.CopyResource(_stagingTexture!, texture);
                }

                _desktopResource.Dispose();
                _outputDuplication.ReleaseFrame();

                var mappedResource = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                
                int bufferSize = _width * _height * 4;
                if (_frameBuffer == null || _frameBuffer.Length != bufferSize) _frameBuffer = new byte[bufferSize];

                Marshal.Copy(mappedResource.DataPointer, _frameBuffer, 0, bufferSize);
                _context.Unmap(_stagingTexture!, 0);

                return _frameBuffer;
            }
            catch (Exception ex)
            {
                // DXGI 에러 코드 확인 (AccessLost: 0x887A0026)
                if (ex.HResult == unchecked((int)0x887A0026) || ex.Message.Contains("AccessLost"))
                {
                    Console.WriteLine("[Capture] Desktop access lost. Re-initialising...");
                    Initialise();
                }
                else if (ex.HResult == unchecked((int)0x887A0001)) // WaitTimeout
                {
                    return null;
                }
                else
                {
                    Console.WriteLine($"[Capture] Runtime error (0x{ex.HResult:X}): {ex.Message}");
                    Cleanup();
                }
                return null;
            }
        }

        private void Cleanup()
        {
            _desktopResource?.Dispose();
            _outputDuplication?.Dispose();
            _stagingTexture?.Dispose();
            _outputDuplication = null;
            _stagingTexture = null;
            _desktopResource = null;
        }

        public void Dispose()
        {
            Cleanup();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
