using System.Drawing;
using System.Windows.Forms;

namespace Host
{
    internal sealed class HubClientSetupForm : Form
    {
        private readonly TextBox _managerBox;
        private readonly NumericUpDown _portBox;
        private readonly TextBox _nameBox;
        private readonly TextBox _passwordBox;
        private readonly TextBox _updateManifestBox;
        private readonly ComboBox _monitorBox;
        private readonly List<MonitorInfo> _monitors;

        public string ManagerAddress => _managerBox.Text.Trim();
        public int ManagerPort => (int)_portBox.Value;
        public string ClientName => _nameBox.Text.Trim();
        public string AccessPassword => _passwordBox.Text;
        public string UpdateManifestUrl => _updateManifestBox.Text.Trim();
        public int AdapterIndex =>
            _monitors.Count > 0
                ? _monitors[_monitorBox.SelectedIndex].AdapterIndex
                : 0;
        public int OutputIndex =>
            _monitors.Count > 0
                ? _monitors[_monitorBox.SelectedIndex].OutputIndex
                : 0;

        public HubClientSetupForm(HubClientSettings? savedSettings = null)
        {
            Text = "Comote Client → Manager Hub";
            Width = 470;
            Height = 640;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(12, 12, 12);
            ForeColor = Color.FromArgb(0, 255, 65);
            Font = new Font("Segoe UI", 10);

            var title = new Label
            {
                Text = "COMOTE CLIENT",
                Font = new Font("Segoe UI", 18, FontStyle.Bold),
                Left = 24,
                Top = 20,
                Width = 400,
                Height = 38,
            };
            Controls.Add(title);

            _managerBox = AddTextBox(
                "Manager 공인 IP 또는 DDNS",
                savedSettings?.ManagerAddress ?? "127.0.0.1",
                72);
            _portBox = new NumericUpDown
            {
                Left = 24,
                Top = 154,
                Width = 400,
                Height = 30,
                Minimum = 1024,
                Maximum = 65535,
                Value = savedSettings?.ManagerPort is >= 1024 and <= 65535 ? savedSettings.ManagerPort : 45820,
            };
            Controls.Add(AddLabel("Manager 포트", 130));
            Controls.Add(_portBox);
            _nameBox = AddTextBox(
                "이 Client 표시 이름",
                savedSettings?.ClientName ?? Environment.MachineName,
                198);
            _passwordBox = AddTextBox(
                "Manager 등록 암호",
                "",
                280);
            _passwordBox.UseSystemPasswordChar = true;

            Controls.Add(AddLabel("캡처 모니터", 362));
            _monitorBox = new ComboBox
            {
                Left = 24,
                Top = 386,
                Width = 400,
                Height = 30,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            Controls.Add(_monitorBox);
            _monitors = ScreenCapture.GetMonitors();
            var primary = 0;
            for (var index = 0; index < _monitors.Count; index++)
            {
                var monitor = _monitors[index];
                _monitorBox.Items.Add(
                    $"DISPLAY {index + 1}: " +
                    $"{monitor.Width}x{monitor.Height}" +
                    (monitor.IsPrimary ? " [MAIN]" : ""));
                if (monitor.IsPrimary) primary = index;
                if (savedSettings != null &&
                    monitor.AdapterIndex == savedSettings.AdapterIndex &&
                    monitor.OutputIndex == savedSettings.OutputIndex)
                    primary = index;
            }
            if (_monitorBox.Items.Count > 0)
                _monitorBox.SelectedIndex = primary;

            _updateManifestBox = AddTextBox(
                "Update manifest HTTPS URL (optional)",
                savedSettings?.UpdateManifestUrl ?? "",
                442);

            var start = new Button
            {
                Text = "Manager에 연결",
                Left = 24,
                Top = 538,
                Width = 400,
                Height = 44,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            start.Click += (_, _) => ValidateAndStart();
            Controls.Add(start);
            AcceptButton = start;
        }

        private Label AddLabel(string text, int top)
        {
            return new Label
            {
                Text = text,
                Left = 24,
                Top = top,
                Width = 400,
                Height = 24,
                ForeColor = Color.LightGray,
            };
        }

        private TextBox AddTextBox(
            string label,
            string value,
            int top)
        {
            Controls.Add(AddLabel(label, top));
            var box = new TextBox
            {
                Text = value,
                Left = 24,
                Top = top + 24,
                Width = 400,
                Height = 30,
            };
            Controls.Add(box);
            return box;
        }

        private void ValidateAndStart()
        {
            if (string.IsNullOrWhiteSpace(ManagerAddress) ||
                string.IsNullOrWhiteSpace(ClientName))
            {
                MessageBox.Show("Manager 주소와 Client 이름을 입력하세요.");
                return;
            }
            if (AccessPassword.Length < 8)
            {
                MessageBox.Show("Manager 등록 암호는 최소 8자 이상이어야 합니다.");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

