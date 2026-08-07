using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    public sealed class LoginForm : Form
    {
        private readonly AppSettings _settings;
        private readonly TextBox _emailTextBox;
        private readonly TextBox _passwordTextBox;
        private readonly CheckBox _saveCheckBox;
        private readonly Button _loginButton;
        private readonly Label _statusLabel;
        private readonly Panel _statusPanel;

        public string AccessToken { get; private set; } = "";
        public string RefreshToken { get; private set; } = "";
        public string UserEmail { get; private set; } = "";
        public string UserId { get; private set; } = "";

        public LoginForm(AppSettings settings)
        {
            _settings = settings;

            Text = "Comote Client 로그인";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(470, 520);
            BackColor = Color.FromArgb(11, 18, 32);
            ForeColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 10);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            var accent = new Panel
            {
                Dock = DockStyle.Top,
                Height = 7,
                BackColor = Color.FromArgb(34, 211, 238),
            };
            Controls.Add(accent);

            var logo = new Label
            {
                Text = "C",
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(42, 36),
                Size = new Size(46, 46),
                BackColor = Color.FromArgb(34, 211, 238),
                ForeColor = Color.FromArgb(8, 47, 73),
                Font = new Font("Segoe UI", 18, FontStyle.Bold),
            };
            Controls.Add(logo);

            var brand = new Label
            {
                Text = "Comote Client",
                Location = new Point(101, 36),
                AutoSize = true,
                Font = new Font("Segoe UI", 15, FontStyle.Bold),
            };
            Controls.Add(brand);

            var brandCaption = new Label
            {
                Text = "이 PC를 내 Comote 계정에 안전하게 연결합니다",
                Location = new Point(103, 64),
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 9),
            };
            Controls.Add(brandCaption);

            var card = new Panel
            {
                Location = new Point(38, 108),
                Size = new Size(394, 360),
                BackColor = Color.FromArgb(17, 28, 46),
                Padding = new Padding(26),
            };
            Controls.Add(card);

            var title = new Label
            {
                Text = "계정 로그인",
                Location = new Point(26, 23),
                AutoSize = true,
                Font = new Font("Segoe UI", 18, FontStyle.Bold),
            };
            card.Controls.Add(title);

            var subtitle = new Label
            {
                Text = "Manager와 동일한 계정을 입력하세요.",
                Location = new Point(28, 57),
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 9),
            };
            card.Controls.Add(subtitle);

            card.Controls.Add(CreateFieldLabel("ID", 91));
            _emailTextBox = CreateInput(113);
            _emailTextBox.PlaceholderText = "Comote account ID";
            card.Controls.Add(_emailTextBox);

            card.Controls.Add(CreateFieldLabel("비밀번호", 166));
            _passwordTextBox = CreateInput(188);
            _passwordTextBox.UseSystemPasswordChar = true;
            _passwordTextBox.PlaceholderText = "8자 이상의 비밀번호";
            card.Controls.Add(_passwordTextBox);

            _saveCheckBox = new CheckBox
            {
                Text = "이 PC에 로그인 정보 저장",
                Location = new Point(28, 239),
                AutoSize = true,
                ForeColor = Color.FromArgb(203, 213, 225),
                BackColor = Color.Transparent,
            };
            card.Controls.Add(_saveCheckBox);

            var accountLink = new LinkLabel
            {
                Text = "계정 만들기",
                Location = new Point(284, 239),
                AutoSize = true,
                LinkColor = Color.FromArgb(125, 211, 252),
                ActiveLinkColor = Color.FromArgb(56, 189, 248),
                VisitedLinkColor = Color.FromArgb(125, 211, 252),
            };
            accountLink.LinkClicked += (_, _) => OpenAccountPage();
            card.Controls.Add(accountLink);

            _statusPanel = new Panel
            {
                Location = new Point(26, 267),
                Size = new Size(342, 36),
                BackColor = Color.FromArgb(30, 41, 59),
                Visible = false,
            };
            _statusLabel = new Label
            {
                Location = new Point(10, 8),
                Size = new Size(322, 22),
                ForeColor = Color.FromArgb(252, 165, 165),
                AutoEllipsis = true,
            };
            _statusPanel.Controls.Add(_statusLabel);
            card.Controls.Add(_statusPanel);

            _loginButton = new Button
            {
                Text = "로그인",
                Location = new Point(26, 312),
                Size = new Size(342, 44),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _loginButton.FlatAppearance.BorderSize = 0;
            _loginButton.FlatAppearance.MouseOverBackColor =
                Color.FromArgb(37, 99, 235);
            _loginButton.Click += async (_, _) => await LoginAsync();
            card.Controls.Add(_loginButton);

            var footer = new Label
            {
                Text = "Comote Remote Fleet · Preview 10",
                Location = new Point(0, 487),
                Size = new Size(ClientSize.Width, 18),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(100, 116, 139),
                Font = new Font("Segoe UI", 8),
            };
            Controls.Add(footer);

            AcceptButton = _loginButton;
            Shown += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_emailTextBox.Text))
                    _emailTextBox.Focus();
                else
                    _passwordTextBox.Focus();
            };

            LoadSavedCredentials();
            ShowConfigurationErrorIfNeeded();
        }

        private static Label CreateFieldLabel(string text, int top)
        {
            return new Label
            {
                Text = text,
                Location = new Point(28, top),
                AutoSize = true,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
            };
        }

        private static TextBox CreateInput(int top)
        {
            return new TextBox
            {
                Location = new Point(26, top),
                Size = new Size(342, 30),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 11),
            };
        }

        private void OpenAccountPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://kymote.vercel.app/login",
                    UseShellExecute = true,
                });
            }
            catch
            {
                ShowStatus(
                    "브라우저를 열 수 없습니다. 계정 페이지에 직접 접속해 주세요.",
                    true);
            }
        }

        private void LoadSavedCredentials()
        {
            if (UserCredentialStore.TryLoad(out var email, out var password))
            {
                _emailTextBox.Text = email;
                _passwordTextBox.Text = password;
                _saveCheckBox.Checked = true;
            }
        }

        private void ShowConfigurationErrorIfNeeded()
        {
            var errors = _settings.GetConfigurationErrors();
            if (errors.Count == 0) return;

            _loginButton.Enabled = false;
            ShowStatus(
                "인증 서버 설정을 확인할 수 없습니다: " +
                string.Join(", ", errors),
                true);
        }

        private void ShowStatus(string message, bool isError)
        {
            _statusPanel.Visible = true;
            _statusLabel.Text = message;
            _statusLabel.ForeColor = isError
                ? Color.FromArgb(252, 165, 165)
                : Color.FromArgb(125, 211, 252);
        }

        private async Task LoginAsync()
        {
            var email = _emailTextBox.Text.Trim();
            var password = _passwordTextBox.Text;
            if (string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(password))
            {
                ShowStatus("이메일과 비밀번호를 입력해 주세요.", true);
                return;
            }

            _loginButton.Enabled = false;
            _loginButton.Text = "로그인 중...";
            ShowStatus("계정 정보를 확인하고 있습니다...", false);

            try
            {
                var result = await SignInWithEmailPassword(email, password);
                if (result == null)
                {
                    ShowStatus(
                        "로그인하지 못했습니다. 이메일 인증 여부와 비밀번호를 확인해 주세요.",
                        true);
                    return;
                }

                AccessToken = result.Value.AccessToken;
                RefreshToken = result.Value.RefreshToken;
                UserId = result.Value.UserId;
                UserEmail = email;

                if (_saveCheckBox.Checked)
                    UserCredentialStore.Save(email, password);
                else
                {
                    UserCredentialStore.Delete();
                    RefreshToken = "";
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch
            {
                ShowStatus(
                    "인증 서버에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.",
                    true);
            }
            finally
            {
                _loginButton.Text = "로그인";
                _loginButton.Enabled = true;
            }
        }

        private static string ToAccountEmail(string accountId)
        {
            var normalized = accountId.Trim().ToLowerInvariant();
            return $"{normalized}@accounts.kymote.app";
        }

        private async Task<(string AccessToken, string RefreshToken, string UserId)?>
            SignInWithEmailPassword(string accountId, string password)
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            var url =
                $"{_settings.SupabaseUrl.TrimEnd('/')}/auth/v1/token" +
                "?grant_type=password";
            using var content = new StringContent(
                JsonConvert.SerializeObject(new { email = ToAccountEmail(accountId), password }),
                Encoding.UTF8,
                "application/json");
            client.DefaultRequestHeaders.Add(
                "apikey",
                _settings.SupabaseAnonKey);

            using var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine(
                    $"[Login] Supabase returned {response.StatusCode}");
                return null;
            }

            var json = JObject.Parse(responseBody);
            var accessToken = json.Value<string>("access_token");
            var refreshToken = json.Value<string>("refresh_token");
            var userId = json["user"]?.Value<string>("id");
            return
                !string.IsNullOrWhiteSpace(accessToken) &&
                !string.IsNullOrWhiteSpace(refreshToken) &&
                !string.IsNullOrWhiteSpace(userId)
                    ? (accessToken, refreshToken, userId)
                    : null;
        }
    }
}

