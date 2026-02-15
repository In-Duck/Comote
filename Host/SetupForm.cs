using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Host
{
    /// <summary>
    /// Host 실행 시 PC 이름, 비밀번호, 캡처 모니터를 설정하는 초기 설정 다이얼로그.
    /// </summary>
    public class SetupForm : Form
    {
        private TextBox _nameBox;
        private TextBox _passwordBox;
        private ComboBox _monitorCombo;
        private List<MonitorInfo> _monitors;

        public string HostName => _nameBox.Text.Trim();
        public string? Password => string.IsNullOrWhiteSpace(_passwordBox.Text) ? null : _passwordBox.Text;

        /// <summary>선택된 모니터의 어댑터 인덱스</summary>
        public int SelectedAdapterIndex => _monitors.Count > 0 ? _monitors[_monitorCombo.SelectedIndex].AdapterIndex : 0;
        /// <summary>선택된 모니터의 출력 인덱스</summary>
        public int SelectedOutputIndex => _monitors.Count > 0 ? _monitors[_monitorCombo.SelectedIndex].OutputIndex : 0;

        public SetupForm()
        {
            Text = "Comote Host 설정";
            Size = new Size(380, 330);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(30, 30, 35);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10);

            // 타이틀
            var titleLabel = new Label
            {
                Text = "🖥️ Comote Host",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 160, 255),
                Location = new Point(20, 15),
                AutoSize = true
            };
            Controls.Add(titleLabel);

            // PC 이름
            var nameLabel = new Label
            {
                Text = "PC 이름",
                Location = new Point(20, 55),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 210)
            };
            Controls.Add(nameLabel);

            _nameBox = new TextBox
            {
                Text = Environment.MachineName,
                Location = new Point(20, 78),
                Size = new Size(320, 30),
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11)
            };
            Controls.Add(_nameBox);

            // 비밀번호
            var pwdLabel = new Label
            {
                Text = "비밀번호 (빈칸 = 없음)",
                Location = new Point(20, 112),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 210)
            };
            Controls.Add(pwdLabel);

            _passwordBox = new TextBox
            {
                Location = new Point(20, 135),
                Size = new Size(320, 30),
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11),
                UseSystemPasswordChar = true
            };
            Controls.Add(_passwordBox);

            // 모니터 선택
            var monLabel = new Label
            {
                Text = "캡처 모니터",
                Location = new Point(20, 170),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 210)
            };
            Controls.Add(monLabel);

            _monitorCombo = new ComboBox
            {
                Location = new Point(20, 193),
                Size = new Size(320, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11),
                FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_monitorCombo);

            // 모니터 목록 로드
            _monitors = ScreenCapture.GetMonitors();
            int primaryIdx = 0;
            for (int i = 0; i < _monitors.Count; i++)
            {
                var m = _monitors[i];
                string label = $"{m.Name} ({m.Width}x{m.Height}){(m.IsPrimary ? " ★ 기본" : "")}";
                _monitorCombo.Items.Add(label);
                if (m.IsPrimary) primaryIdx = i;
            }
            if (_monitorCombo.Items.Count > 0)
                _monitorCombo.SelectedIndex = primaryIdx;
            else
                _monitorCombo.Items.Add("모니터 없음");

            // 시작 버튼
            var startBtn = new Button
            {
                Text = "▶ 시작",
                Location = new Point(200, 240),
                Size = new Size(140, 36),
                BackColor = Color.FromArgb(60, 110, 200),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            startBtn.FlatAppearance.BorderSize = 0;
            startBtn.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(startBtn);

            AcceptButton = startBtn;
        }
    }
}
