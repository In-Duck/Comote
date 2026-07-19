using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
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

            var configurationErrors = _settings.GetConfigurationErrors();
            if (configurationErrors.Count > 0)
            {
                btnLogin.IsEnabled = false;
                lblStatus.Text =
                    "서버 설정이 필요합니다: " +
                    string.Join(", ", configurationErrors);
                lblStatus.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void LoadSavedCredentials()
        {
            if (UserCredentialStore.TryLoad(out var email, out var password))
            {
                txtEmail.Text = email;
                txtPassword.Password = password;
                chkSave.IsChecked = true;
            }
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            btnLogin.IsEnabled = false;
            lblStatus.Text = "로그인 중...";
            lblStatus.Foreground = System.Windows.Media.Brushes.LightBlue;

            var email = txtEmail.Text.Trim();
            var password = txtPassword.Password;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                lblStatus.Text = "이메일과 비밀번호를 입력해 주세요.";
                btnLogin.IsEnabled = true;
                return;
            }

            try
            {
                var result = await SignInWithEmailPassword(email, password);
                if (result == null)
                {
                    lblStatus.Text = "로그인에 실패했습니다. 계정 정보를 확인해 주세요.";
                    lblStatus.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }

                AccessToken = result.Value.Token;
                UserId = result.Value.UserId;
                UserEmail = email;

                if (chkSave.IsChecked == true)
                    UserCredentialStore.Save(email, password);
                else
                    UserCredentialStore.Delete();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "로그인 오류: " + ex.Message;
                lblStatus.Foreground = System.Windows.Media.Brushes.Red;
            }
            finally
            {
                btnLogin.IsEnabled = true;
            }
        }

        private async Task<(string Token, string UserId)?> SignInWithEmailPassword(
            string email,
            string password)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"{_settings.SupabaseUrl.TrimEnd('/')}/auth/v1/token?grant_type=password";
            var body = new { email, password };
            using var content = new StringContent(
                JsonConvert.SerializeObject(body),
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
