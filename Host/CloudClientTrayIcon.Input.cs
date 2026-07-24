using System.Diagnostics;
using System.Windows.Forms;

namespace Host;

internal sealed partial class CloudClientTrayIcon
{
    private static void AddInputControls(
        ContextMenuStrip menu,
        InputBackendMode currentMode)
    {
        var inputMenu = new ToolStripMenuItem("입력 모드");
        var sendInput = new ToolStripMenuItem("Windows SendInput")
        {
            Checked = currentMode == InputBackendMode.SendInput,
        };
        var virtualHid = new ToolStripMenuItem("FakerInput 가상 HID")
        {
            Checked = currentMode == InputBackendMode.VirtualHid,
        };
        sendInput.Click += (_, _) => ChangeInputMode(InputBackendMode.SendInput);
        virtualHid.Click += (_, _) => ChangeInputMode(InputBackendMode.VirtualHid);
        inputMenu.DropDownItems.Add(sendInput);
        inputMenu.DropDownItems.Add(virtualHid);
        menu.Items.Add(inputMenu);

        var install = new ToolStripMenuItem("FakerInput 설치/복구");
        install.Click += (_, _) =>
        {
            if (!InputBackendFactory.EnsureVirtualHidReady(null)) return;
            ChangeInputMode(InputBackendMode.VirtualHid, driverReady: true);
        };
        menu.Items.Add(install);
        menu.Items.Add(new ToolStripSeparator());
    }

    private static void ChangeInputMode(
        InputBackendMode mode,
        bool driverReady = false)
    {
        if (mode == InputBackendMode.VirtualHid &&
            !driverReady &&
            !InputBackendFactory.EnsureVirtualHidReady(null))
        {
            return;
        }

        var settings = AppSettings.Load();
        settings.InputBackendMode = mode;
        settings.Save();

        MessageBox.Show(
            mode == InputBackendMode.VirtualHid
                ? "입력 모드를 FakerInput 가상 HID로 변경했습니다.\nClient가 자동으로 다시 시작됩니다."
                : "입력 모드를 Windows SendInput으로 변경했습니다.\nClient가 자동으로 다시 시작됩니다.",
            "Comote · 입력 모드",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        RestartClientAgent();
    }

    private static void RestartClientAgent()
    {
        // A SYSTEM agent is supervised by the Windows service and is
        // relaunched automatically after exit with the newly saved mode.
        if (SecureDesktopService.IsInstalled())
        {
            Environment.Exit(0);
            return;
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
            });
        }
        Environment.Exit(0);
    }
}
