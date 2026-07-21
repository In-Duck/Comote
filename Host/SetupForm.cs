using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Host
{
    /// <summary>
    /// Host 실행 시 PC 이름, 비밀번호, 캡처 모니터를 설정하는 초기 설정 다이얼로그.
    /// </summary>
    public class SetupForm : Form
    {
        // Dragging logic
        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();

        private TextBox _nameBox;
        private TextBox _passwordBox;
        private ComboBox _monitorCombo;
        private ComboBox _inputCombo;
        private List<MonitorInfo> _monitors;
        private Button _closeBtn;

        public string HostName => _nameBox.Text.Trim();
        public string? Password => string.IsNullOrWhiteSpace(_passwordBox.Text) ? null : _passwordBox.Text;
        public InputBackendMode SelectedInputBackendMode =>
            _inputCombo.SelectedIndex == 0
                ? InputBackendMode.VirtualHid
                : InputBackendMode.SendInput;

        /// <summary>선택된 모니터의 어댑터 인덱스</summary>
        public int SelectedAdapterIndex => _monitors.Count > 0 ? _monitors[_monitorCombo.SelectedIndex].AdapterIndex : 0;
        /// <summary>선택된 모니터의 출력 인덱스</summary>
        public int SelectedOutputIndex => _monitors.Count > 0 ? _monitors[_monitorCombo.SelectedIndex].OutputIndex : 0;

        public SetupForm(
            InputBackendMode inputBackendMode = InputBackendMode.SendInput)
        {
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Text = "Comote Client 설정";
            Size = new Size(400, 455); // Slightly taller for better spacing
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; // Borderless
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(11, 18, 32); // Black
            ForeColor = Color.FromArgb(248, 250, 252); // Amber
            Font = new Font("Segoe UI", 10);
            Padding = new Padding(1); // Border

            // Dragging
            _closeBtn = new Button {
                Text = "X",
                Location = new Point(360, 10),
                Size = new Size(30, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Red,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            _closeBtn.FlatAppearance.BorderSize = 0;
            _closeBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_closeBtn);

            this.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
            };

            // 타이틀
            var titleLabel = new Label
            {
                Text = "Client 초기 설정",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248), // Amber
                Location = new Point(20, 15),
                AutoSize = true
            };
            Controls.Add(titleLabel);

            // PC 이름
            var nameLabel = new Label
            {
                Text = "시스템 식별명 (PC Name)",
                Location = new Point(20, 60),
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(nameLabel);

            _nameBox = new TextBox
            {
                Text = Environment.MachineName,
                Location = new Point(20, 85),
                Size = new Size(360, 30),
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = Color.FromArgb(15, 23, 42),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11)
            };
            Controls.Add(_nameBox);

            // 비밀번호
            var pwdLabel = new Label
            {
                Text = "직접 연결 암호 (최소 8자, 12자 이상 권장)",
                Location = new Point(20, 125),
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(pwdLabel);

            _passwordBox = new TextBox
            {
                Location = new Point(20, 150),
                Size = new Size(360, 30),
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = Color.FromArgb(15, 23, 42),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11),
                UseSystemPasswordChar = true
            };
            Controls.Add(_passwordBox);

            // 모니터 선택
            var monLabel = new Label
            {
                Text = "캡처 대상 모니터",
                Location = new Point(20, 190),
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(monLabel);

            _monitorCombo = new ComboBox
            {
                Location = new Point(20, 215),
                Size = new Size(360, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 10),
                FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_monitorCombo);

            // 모니터 목록 로드
            _monitors = ScreenCapture.GetMonitors();
            int primaryIdx = 0;
            for (int i = 0; i < _monitors.Count; i++)
            {
                var m = _monitors[i];
                string label = $"DISPLAY {i+1}: {m.Width}x{m.Height}{(m.IsPrimary ? " [MAIN]" : "")}";
                _monitorCombo.Items.Add(label);
                if (m.IsPrimary) primaryIdx = i;
            }
            if (_monitorCombo.Items.Count > 0)
                _monitorCombo.SelectedIndex = primaryIdx;
            else
                _monitorCombo.Items.Add("감지된 모니터 없음");

            // 입력 방식
            var inputLabel = new Label
            {
                Text = "원격 입력 방식",
                Location = new Point(20, 255),
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(inputLabel);

            _inputCombo = new ComboBox
            {
                Location = new Point(20, 280),
                Size = new Size(360, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 10),
                FlatStyle = FlatStyle.Flat
            };
            _inputCombo.Items.Add("FakerInput 가상 HID (모드 2 · 게임 정책 확인)");
            _inputCombo.Items.Add("Windows SendInput (호환 모드)");
            var isVirtualHidReady = FakerInputBackend.TryGetDriverStatus(out var driverStatus);
            _inputCombo.SelectedIndex =
                inputBackendMode == InputBackendMode.VirtualHid && isVirtualHidReady
                    ? 0
                    : 1;
            Controls.Add(_inputCombo);

            var inputHint = new Label
            {
                Text = isVirtualHidReady
                    ? "FakerInput 드라이버 준비됨"
                    : driverStatus,
                Location = new Point(20, 314),
                Size = new Size(360, 42),
                AutoEllipsis = true,
                ForeColor = isVirtualHidReady
                    ? Color.FromArgb(34, 197, 94)
                    : Color.FromArgb(251, 191, 36)
            };
            Controls.Add(inputHint);

            // 시작 버튼
            var startBtn = new Button
            {
                Text = "설정 저장하고 시작",
                Location = new Point(20, 365),
                Size = new Size(360, 45),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White, // Phosphor Green
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            startBtn.FlatAppearance.BorderColor = Color.FromArgb(59, 130, 246);
            startBtn.FlatAppearance.BorderSize = 1;
            startBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 99, 235);


            startBtn.Click += (s, e) =>
            {
                if (_passwordBox.Text.Length < 8)
                {
                    MessageBox.Show(
                        "직접 연결 암호는 최소 8자 이상이어야 합니다.",
                        "Comote Direct Host");
                    _passwordBox.Focus();
                    return;
                }

                if (SelectedInputBackendMode == InputBackendMode.VirtualHid &&
                    !InputBackendFactory.EnsureVirtualHidReady(this))
                {
                    return;
                }

                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(startBtn);

            AcceptButton = startBtn;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ControlPaint.DrawBorder(e.Graphics, this.ClientRectangle, Color.FromArgb(60, 60, 60), ButtonBorderStyle.Solid);
        }
    }
}
