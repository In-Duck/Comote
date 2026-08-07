using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Comote.VirtualHidE2E;

internal sealed class RawInputValidationForm : Form
{
    private readonly object _eventLock = new();
    private readonly List<RawInputEvidence> _events = [];
    private string _stage = "initializing";
    private bool _registered;

    public RawInputValidationForm()
    {
        Text = "Comote Virtual HID — VM E2E validation";
        Width = 540;
        Height = 170;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text =
                "Disposable VMware validation is running.\r\n" +
                "Do not type, click, switch windows, suspend, or power off.",
        });
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Stage
    {
        set
        {
            lock (_eventLock)
            {
                _stage = value;
            }
        }
    }

    public IReadOnlyList<RawInputEvidence> Snapshot()
    {
        lock (_eventLock)
        {
            return _events
                .Select(CloneEvidence)
                .ToArray();
        }
    }

    public int EventCount
    {
        get
        {
            lock (_eventLock)
            {
                return _events.Count;
            }
        }
    }

    public bool RemoveRegistration()
    {
        if (!_registered)
        {
            return true;
        }

        var devices = CreateRegistrationDevices(
            IntPtr.Zero,
            NativeMethods.RidevRemove);
        var succeeded = NativeMethods.RegisterRawInputDevices(
            devices,
            checked((uint)devices.Length),
            checked((uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()));
        if (succeeded)
        {
            _registered = false;
        }
        return succeeded;
    }

    public void AssertOwnsForeground()
    {
        Activate();
        BringToFront();
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The foreground window was unavailable.");
        }

        _ = NativeMethods.GetWindowThreadProcessId(
            foreground,
            out var processId);
        if (processId != checked((uint)Environment.ProcessId))
        {
            throw new InvalidOperationException(
                "The validation window could not acquire the foreground. " +
                "No input was generated.");
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var flags =
            NativeMethods.RidevInputSink |
            NativeMethods.RidevNoLegacy;
        var devices = CreateRegistrationDevices(Handle, flags);
        if (!NativeMethods.RegisterRawInputDevices(
                devices,
                checked((uint)devices.Length),
                checked((uint)Marshal.SizeOf<
                    NativeMethods.RawInputDevice>())))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "RegisterRawInputDevices failed.");
        }
        _registered = true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmInput)
        {
            TryRecordRawInput(message.LParam);
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = RemoveRegistration();
        }
        base.Dispose(disposing);
    }

    private void TryRecordRawInput(IntPtr rawInputHandle)
    {
        var headerSize = checked((uint)Marshal.SizeOf<
            NativeMethods.RawInputHeader>());
        uint requiredSize = 0;
        if (NativeMethods.GetRawInputData(
                rawInputHandle,
                NativeMethods.RidInput,
                IntPtr.Zero,
                ref requiredSize,
                headerSize) != 0 ||
            requiredSize < headerSize ||
            requiredSize > 64 * 1024)
        {
            return;
        }

        var memory = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            var copiedSize = requiredSize;
            if (NativeMethods.GetRawInputData(
                    rawInputHandle,
                    NativeMethods.RidInput,
                    memory,
                    ref copiedSize,
                    headerSize) != copiedSize ||
                copiedSize != requiredSize)
            {
                return;
            }

            var header = Marshal.PtrToStructure<
                NativeMethods.RawInputHeader>(memory);
            var data = IntPtr.Add(memory, checked((int)headerSize));
            var devicePath = GetDevicePath(header.Device);
            var evidence = new RawInputEvidence
            {
                ObservedUtc = DateTime.UtcNow,
                DevicePath = devicePath,
                NormalizedInstanceId =
                    NormalizeRawInputDevicePath(devicePath),
            };
            lock (_eventLock)
            {
                evidence.Stage = _stage;
            }

            if (header.Type == NativeMethods.RimTypeKeyboard)
            {
                var keyboard = Marshal.PtrToStructure<
                    NativeMethods.RawKeyboard>(data);
                evidence.Kind = RawInputKind.Keyboard;
                evidence.Flags = keyboard.Flags;
                evidence.VirtualKey = keyboard.VirtualKey;
                evidence.MakeCode = keyboard.MakeCode;
                evidence.KeyBreak =
                    (keyboard.Flags & NativeMethods.RiKeyBreak) != 0;
            }
            else if (header.Type == NativeMethods.RimTypeMouse)
            {
                var mouse = Marshal.PtrToStructure<
                    NativeMethods.RawMouse>(data);
                evidence.Kind = RawInputKind.Mouse;
                evidence.Flags = mouse.Flags;
                evidence.DeltaX = mouse.LastX;
                evidence.DeltaY = mouse.LastY;
                evidence.MouseAbsolute =
                    (mouse.Flags & NativeMethods.MouseMoveAbsolute) != 0;
                evidence.MouseButtonFlags =
                    unchecked((ushort)(mouse.Buttons & 0xFFFF));
                evidence.MouseButtonData =
                    unchecked((short)(mouse.Buttons >> 16));
            }
            else
            {
                return;
            }

            lock (_eventLock)
            {
                _events.Add(evidence);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static NativeMethods.RawInputDevice[]
        CreateRegistrationDevices(
            IntPtr target,
            uint flags) =>
        [
            new()
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = flags,
                Target = target,
            },
            new()
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = flags,
                Target = target,
            },
        ];

    private static string GetDevicePath(IntPtr device)
    {
        uint characterCount = 0;
        _ = NativeMethods.GetRawInputDeviceInfo(
            device,
            NativeMethods.RidiDeviceName,
            null,
            ref characterCount);
        if (characterCount is 0 or > 32768)
        {
            return "";
        }

        var builder = new StringBuilder(checked((int)characterCount + 1));
        var copied = NativeMethods.GetRawInputDeviceInfo(
            device,
            NativeMethods.RidiDeviceName,
            builder,
            ref characterCount);
        return copied == uint.MaxValue ? "" : builder.ToString();
    }

    internal static string NormalizeRawInputDevicePath(string devicePath)
    {
        var value = devicePath.Trim();
        if (value.StartsWith(
                @"\\?\",
                StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        var classGuidStart = value.LastIndexOf("#{", StringComparison.Ordinal);
        if (classGuidStart >= 0)
        {
            value = value[..classGuidStart];
        }

        return value
            .Replace('#', '\\')
            .TrimEnd('\\')
            .ToUpperInvariant();
    }

    private static RawInputEvidence CloneEvidence(
        RawInputEvidence value) =>
        new()
        {
            ObservedUtc = value.ObservedUtc,
            Stage = value.Stage,
            DevicePath = value.DevicePath,
            NormalizedInstanceId = value.NormalizedInstanceId,
            Kind = value.Kind,
            Flags = value.Flags,
            VirtualKey = value.VirtualKey,
            MakeCode = value.MakeCode,
            KeyBreak = value.KeyBreak,
            DeltaX = value.DeltaX,
            DeltaY = value.DeltaY,
            MouseAbsolute = value.MouseAbsolute,
            MouseButtonFlags = value.MouseButtonFlags,
            MouseButtonData = value.MouseButtonData,
        };
}
