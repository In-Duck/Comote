using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Comote.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Viewer
{
    public partial class LoginWindow : Window
    {
        public string AccessToken { get; private set; } = "";
        public string UserEmail { get; private set; } = "";
        public string UserId { get; private set; } = "";

        private readonly AppSettings _settings;

        public LoginWindow()
        {
            InitializeComponent();
            try
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Kymote.ico"));
            }
            catch
            {
                // The window remains usable without an icon.
            }

            _settings = AppSettings.Load();
            LoadSavedCredentials();
            Loaded += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(txtEmail.Text)) txtEmail.Focus();
                else txtPassword.Focus();
            };

            var configurationErrors = _settings.GetConfigurationErrors();
            if (configurationErrors.Count > 0)
            {
                btnLogin.IsEnabled = false;
                ShowStatus("서버 설정이 필요합니다: " + string.Join(", ", configurationErrors), true);
            }
        }

        private void LoadSavedCredentials()
        {
            if (!UserCredentialStore.TryLoad(out var email, out var password)) return;
            txtEmail.Text = email;
            txtPassword.Password = password;
            chkSave.IsChecked = true;
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            var accountId = txtEmail.Text.Trim();
            var password = txtPassword.Password;
            if (!AccountIdentity.TryNormalize(accountId, out _) || string.IsNullOrWhiteSpace(password))
            {
                ShowStatus("올바른 아이디 또는 이메일과 비밀번호를 입력해 주세요.", true);
                return;
            }

            btnLogin.IsEnabled = false;
            btnLogin.Content = "연결 중";
            ShowStatus("계정을 확인하고 있습니다.", false);
            try
            {
                var result = await SignInWithEmailPassword(accountId, password);
                if (result == null)
                {
                    ShowStatus("아이디 또는 비밀번호가 올바르지 않습니다.", true);
                    return;
                }

                AccessToken = result.Value.Token;
                UserId = result.Value.UserId;
                UserEmail = accountId;

                if (chkSave.IsChecked == true) UserCredentialStore.Save(accountId, password);
                else UserCredentialStore.Delete();

                DialogResult = true;
                Close();
            }
            catch (TaskCanceledException)
            {
                ShowStatus("계정 서버 응답이 늦습니다. 잠시 후 다시 시도해 주세요.", true);
            }
            catch (HttpRequestException)
            {
                ShowStatus("인터넷 연결을 확인한 뒤 다시 시도해 주세요.", true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Login] Unexpected error: {ex.Message}");
                ShowStatus("로그인 중 오류가 발생했습니다.", true);
            }
            finally
            {
                btnLogin.Content = "로그인";
                btnLogin.IsEnabled = true;
            }
        }

        private void ShowStatus(string message, bool isError)
        {
            statusPanel.Visibility = Visibility.Visible;
            lblStatus.Text = message;
            lblStatus.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
                : new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }

        private void CreateAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://comote-remote.dopum54.chatgpt.site/login",
                    UseShellExecute = true,
                });
            }
            catch
            {
                ShowStatus("브라우저를 열 수 없습니다. 계정 웹사이트에 직접 접속해 주세요.", true);
            }
        }

        private async Task<(string Token, string UserId)?> SignInWithEmailPassword(
            string accountId,
            string password)
        {
            if (!AccountIdentity.TryNormalize(accountId, out var accountEmail)) return null;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"{_settings.SupabaseUrl.TrimEnd('/')}/auth/v1/token?grant_type=password";
            using var content = new StringContent(
                JsonConvert.SerializeObject(new { email = accountEmail, password }),
                Encoding.UTF8,
                "application/json");
            client.DefaultRequestHeaders.Add("apikey", _settings.SupabaseAnonKey);

            using var response = await client.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Login] Supabase returned {response.StatusCode}");
                return null;
            }

            var json = JObject.Parse(responseString);
            var token = json.Value<string>("access_token");
            var userId = json["user"]?.Value<string>("id");
            return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(userId)
                ? (token, userId)
                : null;
        }
    }
}