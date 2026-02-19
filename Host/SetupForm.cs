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
        private List<MonitorInfo> _monitors;
        private Button _closeBtn;

        public string HostName => _nameBox.Text.Trim();
        public string? Password => string.IsNullOrWhiteSpace(_passwordBox.Text) ? null : _passwordBox.Text;

        /// <summary>선택된 모니터의 어댑터 인덱스</summary>
        public int SelectedAdapterIndex => _monitors.Count > 0 ? _monitors[_monitorCombo.SelectedIndex].AdapterIndex : 0;
        /// <summary>선택된 모니터의 출력 인덱스</summary>
        public int SelectedOutputIndex => _monitors.Count > 0 ? _monitors[_monitorCombo.SelectedIndex].OutputIndex : 0;

        public SetupForm()
        {
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Text = "KYMOTE 호스트 설정";
            Size = new Size(400, 380); // Slightly taller for better spacing
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None; // Borderless
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(5, 5, 5); // Black
            ForeColor = Color.FromArgb(255, 176, 0); // Amber
            Font = new Font("Consolas", 10);
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
                Font = new Font("Consolas", 10, FontStyle.Bold)
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
                Text = "KYMOTE :: SETUP",
                Font = new Font("Consolas", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 176, 0), // Amber
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
                ForeColor = Color.Gray
            };
            Controls.Add(nameLabel);

            _nameBox = new TextBox
            {
                Text = Environment.MachineName,
                Location = new Point(20, 85),
                Size = new Size(360, 30),
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 11)
            };
            Controls.Add(_nameBox);

            // 비밀번호
            var pwdLabel = new Label
            {
                Text = "접속 비밀번호 (공란 시 비밀번호 없음)",
                Location = new Point(20, 125),
                AutoSize = true,
                ForeColor = Color.Gray
            };
            Controls.Add(pwdLabel);

            _passwordBox = new TextBox
            {
                Location = new Point(20, 150),
                Size = new Size(360, 30),
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 11),
                UseSystemPasswordChar = true
            };
            Controls.Add(_passwordBox);

            // 모니터 선택
            var monLabel = new Label
            {
                Text = "캡처 대상 모니터",
                Location = new Point(20, 190),
                AutoSize = true,
                ForeColor = Color.Gray
            };
            Controls.Add(monLabel);

            _monitorCombo = new ComboBox
            {
                Location = new Point(20, 215),
                Size = new Size(360, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10),
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

            // 시작 버튼
            var startBtn = new Button
            {
                Text = "[ 시스템 가동 시작 ]",
                Location = new Point(20, 310),
                Size = new Size(360, 45),
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(0, 255, 65), // Phosphor Green
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Consolas", 12, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            startBtn.FlatAppearance.BorderColor = Color.FromArgb(0, 255, 65);
            startBtn.FlatAppearance.BorderSize = 1;
            startBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 255, 65);
            
            startBtn.MouseEnter += (s, e) => { startBtn.ForeColor = Color.Black; };
            startBtn.MouseLeave += (s, e) => { startBtn.ForeColor = Color.FromArgb(0, 255, 65); };

            startBtn.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
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
