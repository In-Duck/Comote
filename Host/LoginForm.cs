using System;
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

        public string AccessToken { get; private set; } = "";
        public string RefreshToken { get; private set; } = "";
        public string UserEmail { get; private set; } = "";
        public string UserId { get; private set; } = "";

        public LoginForm(AppSettings settings)
        {
            _settings = settings;

            Text = "Comote Host 로그인";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(390, 300);
            BackColor = Color.FromArgb(18, 18, 18);
            ForeColor = Color.WhiteSmoke;
            Font = new Font("Segoe UI", 10);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // The dialog remains usable without an icon.
            }

            var title = new Label
            {
                Text = "COMOTE HOST",
                Location = new Point(28, 22),
                AutoSize = true,
                Font = new Font("Segoe UI", 17, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 176, 0),
            };
            var emailLabel = new Label
            {
                Text = "이메일",
                Location = new Point(30, 72),
                AutoSize = true,
            };
            _emailTextBox = new TextBox
            {
                Location = new Point(30, 94),
                Width = 330,
            };
            var passwordLabel = new Label
            {
                Text = "비밀번호",
                Location = new Point(30, 132),
                AutoSize = true,
            };
            _passwordTextBox = new TextBox
            {
                Location = new Point(30, 154),
                Width = 330,
                UseSystemPasswordChar = true,
            };
            _saveCheckBox = new CheckBox
            {
                Text = "로그인 저장 및 무인 서비스 연결",
                Location = new Point(30, 190),
                AutoSize = true,
            };
            _statusLabel = new Label
            {
                Location = new Point(30, 218),
                Width = 330,
                Height = 34,
                ForeColor = Color.OrangeRed,
            };
            _loginButton = new Button
            {
                Text = "로그인",
                Location = new Point(270, 254),
                Width = 90,
                Height = 32,
                BackColor = Color.FromArgb(255, 176, 0),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
            };
            _loginButton.Click += async (_, _) => await LoginAsync();

            Controls.AddRange(new Control[]
            {
                title,
                emailLabel,
                _emailTextBox,
                passwordLabel,
                _passwordTextBox,
                _saveCheckBox,
                _statusLabel,
                _loginButton,
            });

            AcceptButton = _loginButton;
            LoadSavedCredentials();
            ShowConfigurationErrorIfNeeded();
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
            _statusLabel.Text = "서버 설정 필요: " + string.Join(", ", errors);
        }

        private async Task LoginAsync()
        {
            var email = _emailTextBox.Text.Trim();
            var password = _passwordTextBox.Text;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                _statusLabel.Text = "이메일과 비밀번호를 입력해 주세요.";
                return;
            }

            _loginButton.Enabled = false;
            _statusLabel.ForeColor = Color.LightBlue;
            _statusLabel.Text = "로그인 중...";

            try
            {
                var result = await SignInWithEmailPassword(email, password);
                if (result == null)
                {
                    _statusLabel.ForeColor = Color.OrangeRed;
                    _statusLabel.Text = "로그인에 실패했습니다.";
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
                    ServiceCredentialStore.Delete();
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.ForeColor = Color.OrangeRed;
                _statusLabel.Text = "로그인 오류: " + ex.Message;
            }
            finally
            {
                _loginButton.Enabled = true;
            }
        }

        private async Task<(string AccessToken, string RefreshToken, string UserId)?>
            SignInWithEmailPassword(string email, string password)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"{_settings.SupabaseUrl.TrimEnd('/')}/auth/v1/token?grant_type=password";
            using var content = new StringContent(
                JsonConvert.SerializeObject(new { email, password }),
                Encoding.UTF8,
                "application/json");
            client.DefaultRequestHeaders.Add("apikey", _settings.SupabaseAnonKey);

            using var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Login] Supabase returned {response.StatusCode}");
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
