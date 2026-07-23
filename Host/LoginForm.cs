using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Comote.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    public sealed class LoginForm : Form
    {
        private readonly AppSettings _settings;
        private readonly TextBox _accountBox;
        private readonly TextBox _passwordBox;
        private readonly CheckBox _rememberBox;
        private readonly Button _loginButton;
        private readonly Label _statusLabel;

        public string AccessToken { get; private set; } = "";
        public string RefreshToken { get; private set; } = "";
        public string UserEmail { get; private set; } = "";
        public string UserId { get; private set; } = "";

        public LoginForm(AppSettings settings)
        {
            _settings = settings;
            Text = "Comote Client";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(400, 438);
            BackColor = Color.FromArgb(247, 247, 245);
            ForeColor = Color.FromArgb(28, 28, 26);
            Font = new Font("Segoe UI", 9.5f);

            Controls.Add(new Label { Text = "Comote Client", Location = new Point(36, 30), AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold) });
            Controls.Add(new Label { Text = "이 PC를 계정에 연결합니다. VPN 주소는 필요하지 않습니다.", Location = new Point(38, 66), Size = new Size(326, 38), ForeColor = Color.FromArgb(94, 94, 88) });
            Controls.Add(FieldLabel("아이디 또는 이메일", 117));
            _accountBox = Input(141);
            _accountBox.PlaceholderText = "ID 또는 email@example.com";
            Controls.Add(_accountBox);
            Controls.Add(FieldLabel("비밀번호", 198));
            _passwordBox = Input(222);
            _passwordBox.UseSystemPasswordChar = true;
            Controls.Add(_passwordBox);

            _rememberBox = new CheckBox { Text = "로그인 정보 저장", Location = new Point(38, 275), AutoSize = true, BackColor = Color.Transparent, ForeColor = Color.FromArgb(73, 73, 68) };
            Controls.Add(_rememberBox);
            var accountLink = new LinkLabel { Text = "계정 만들기", Location = new Point(283, 275), AutoSize = true, LinkColor = Color.FromArgb(35, 73, 132), ActiveLinkColor = Color.FromArgb(23, 52, 94) };
            accountLink.LinkClicked += (_, _) => OpenAccountPage();
            Controls.Add(accountLink);

            _statusLabel = new Label { Location = new Point(38, 307), Size = new Size(326, 38), ForeColor = Color.FromArgb(176, 38, 38), Visible = false };
            Controls.Add(_statusLabel);
            _loginButton = new Button { Text = "로그인", Location = new Point(36, 354), Size = new Size(328, 44), BackColor = Color.FromArgb(31, 41, 55), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
            _loginButton.FlatAppearance.BorderSize = 0;
            _loginButton.Click += async (_, _) => await LoginAsync();
            Controls.Add(_loginButton);

            AcceptButton = _loginButton;
            LoadSavedCredentials();
            ShowConfigurationErrorIfNeeded();
            Shown += (_, _) => { if (string.IsNullOrWhiteSpace(_accountBox.Text)) _accountBox.Focus(); else _passwordBox.Focus(); };
        }

        private static Label FieldLabel(string text, int top) => new() { Text = text, Location = new Point(38, top), AutoSize = true, ForeColor = Color.FromArgb(73, 73, 68) };
        private static TextBox Input(int top) => new() { Location = new Point(36, top), Size = new Size(328, 31), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = Color.FromArgb(28, 28, 26), Font = new Font("Segoe UI", 10.5f) };

        private void LoadSavedCredentials()
        {
            if (!UserCredentialStore.TryLoad(out var account, out var password)) return;
            _accountBox.Text = account;
            _passwordBox.Text = password;
            _rememberBox.Checked = true;
        }

        private void ShowConfigurationErrorIfNeeded()
        {
            var errors = _settings.GetConfigurationErrors();
            if (errors.Count == 0) return;
            _loginButton.Enabled = false;
            ShowStatus("서버 설정이 필요합니다: " + string.Join(", ", errors), true);
        }

        private void ShowStatus(string message, bool isError)
        {
            _statusLabel.Text = message;
            _statusLabel.ForeColor = isError ? Color.FromArgb(176, 38, 38) : Color.FromArgb(73, 73, 68);
            _statusLabel.Visible = true;
        }

        private void OpenAccountPage()
        {
            try { Process.Start(new ProcessStartInfo { FileName = "https://comote-remote.dopum54.chatgpt.site/login", UseShellExecute = true }); }
            catch { ShowStatus("브라우저를 열 수 없습니다. 계정 웹사이트에 직접 접속해 주세요.", true); }
        }

        private async Task LoginAsync()
        {
            var account = _accountBox.Text.Trim();
            var password = _passwordBox.Text;
            if (!AccountIdentity.TryNormalize(account, out _) || string.IsNullOrWhiteSpace(password))
            {
                ShowStatus("올바른 아이디 또는 이메일과 비밀번호를 입력해 주세요.", true);
                return;
            }

            _loginButton.Enabled = false;
            _loginButton.Text = "연결 중";
            ShowStatus("계정을 확인하고 있습니다.", false);
            try
            {
                var result = await SignInWithEmailPassword(account, password);
                if (result == null) { ShowStatus("아이디 또는 비밀번호가 올바르지 않습니다.", true); return; }
                AccessToken = result.Value.AccessToken;
                RefreshToken = result.Value.RefreshToken;
                UserId = result.Value.UserId;
                UserEmail = account;
                if (_rememberBox.Checked) UserCredentialStore.Save(account, password);
                else { UserCredentialStore.Delete(); RefreshToken = ""; ServiceCredentialStore.Delete(); }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (TaskCanceledException) { ShowStatus("계정 서버 응답이 늦습니다. 잠시 후 다시 시도해 주세요.", true); }
            catch (HttpRequestException) { ShowStatus("인터넷 연결을 확인한 뒤 다시 시도해 주세요.", true); }
            catch (Exception ex) { Console.WriteLine($"[Login] Unexpected error: {ex.Message}"); ShowStatus("로그인 중 오류가 발생했습니다.", true); }
            finally { _loginButton.Text = "로그인"; _loginButton.Enabled = true; }
        }

        private async Task<(string AccessToken, string RefreshToken, string UserId)?> SignInWithEmailPassword(string accountId, string password)
        {
            var normalizedAccount = accountId.Trim();
            if (!normalizedAccount.Contains('@'))
                return await SignInWithAccountId(normalizedAccount, password);

            if (!AccountIdentity.TryNormalize(accountId, out var accountEmail)) return null;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"{_settings.SupabaseUrl.TrimEnd('/')}/auth/v1/token?grant_type=password";
            using var content = new StringContent(JsonConvert.SerializeObject(new { email = accountEmail, password }), Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Add("apikey", _settings.SupabaseAnonKey);
            using var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) { Console.WriteLine($"[Login] Supabase returned {response.StatusCode}"); return null; }
            var json = JObject.Parse(responseBody);
            var accessToken = json.Value<string>("access_token");
            var refreshToken = json.Value<string>("refresh_token");
            var userId = json["user"]?.Value<string>("id");
            return !string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(refreshToken) && !string.IsNullOrWhiteSpace(userId)
                ? (accessToken, refreshToken, userId) : null;
        }

        private static async Task<(string AccessToken, string RefreshToken, string UserId)?> SignInWithAccountId(
            string accountId,
            string password)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent(
                JsonConvert.SerializeObject(new { account = accountId, password }),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(
                "https://comote-remote.dopum54.chatgpt.site/api/auth/desktop-login",
                content);
            if (!response.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            var accessToken = json.Value<string>("access_token");
            var refreshToken = json.Value<string>("refresh_token");
            var userId = json.Value<string>("user_id");
            return !string.IsNullOrWhiteSpace(accessToken) &&
                   !string.IsNullOrWhiteSpace(refreshToken) &&
                   !string.IsNullOrWhiteSpace(userId)
                ? (accessToken, refreshToken, userId) : null;
        }
    }
}