using System.Drawing;
using System.Windows.Forms;

namespace Host;

internal sealed class UpdateProgressDialog : Form
{
    private readonly Label _statusLabel;
    private readonly Label _percentLabel;
    private readonly ProgressBar _progressBar;

    public UpdateProgressDialog(Version version)
    {
        Text = "Comote 업데이트";
        Width = 420;
        Height = 168;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 20, 34);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            AutoSize = false,
            Text = $"Comote {version} 업데이트",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248),
            Location = new Point(24, 18),
            Size = new Size(350, 26),
        };
        _statusLabel = new Label
        {
            AutoSize = false,
            Text = "업데이트 준비 중…",
            Location = new Point(24, 54),
            Size = new Size(300, 22),
        };
        _percentLabel = new Label
        {
            AutoSize = false,
            Text = "0%",
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(326, 54),
            Size = new Size(50, 22),
        };
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            Location = new Point(24, 84),
            Size = new Size(352, 18),
        };
        Controls.AddRange(new Control[] { title, _statusLabel, _percentLabel, _progressBar });
    }

    public void Report(ClientUpdateProgress progress)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => Report(progress));
            return;
        }

        _statusLabel.Text = progress.Status;
        if (progress.Percent is int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = percent;
            _percentLabel.Text = $"{percent}%";
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _percentLabel.Text = "";
        }
    }
}