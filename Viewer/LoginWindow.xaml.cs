
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace Viewer
{
    public partial class LoginWindow : Window
    {
        public string AccessToken { get; private set; }
        public string UserEmail { get; private set; }
        public string UserId { get; private set; }
        
        private AppSettings _settings;

        public LoginWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            LoadSavedCredentials();
        }

        private void LoadSavedCredentials()
        {
            if (File.Exists("login.dat"))
            {
                try {
                    var lines = File.ReadAllLines("login.dat");
                    if (lines.Length >= 2)
                    {
                        txtEmail.Text = lines[0];
                        txtPassword.Password = lines[1];
                        chkSave.IsChecked = true;
                    }
                } catch { }
            }
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            btnLogin.IsEnabled = false;
            lblStatus.Text = "Logging in...";
            lblStatus.Foreground = System.Windows.Media.Brushes.LightBlue;

            string email = txtEmail.Text;
            string password = txtPassword.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                lblStatus.Text = "Please enter email and password.";
                btnLogin.IsEnabled = true;
                return;
            }

            try
            {
                var (token, userId) = await SignInWithEmailPassword(email, password);
                if (token != null)
                {
                    AccessToken = token;
                    UserEmail = email;
                    UserId = userId;

                    if (chkSave.IsChecked == true)
                    {
                        File.WriteAllLines("login.dat", new[] { email, password });
                    }
                    else
                    {
                        if (File.Exists("login.dat")) File.Delete("login.dat");
                    }

                    this.DialogResult = true;
                    this.Close();
                }
                else
                {
                    lblStatus.Text = "Login failed. Check credentials.";
                    lblStatus.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.Foreground = System.Windows.Media.Brushes.Red;
            }
            finally
            {
                btnLogin.IsEnabled = true;
            }
        }

        private async Task<(string?, string?)> SignInWithEmailPassword(string email, string password)
        {
            using (var client = new HttpClient())
            {
                var url = $"{_settings.SupabaseUrl}/auth/v1/token?grant_type=password";
                var body = new
                {
                    email = email,
                    password = password
                };
                
                var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.Add("apikey", _settings.SupabaseAnonKey);

                var response = await client.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    dynamic json = JsonConvert.DeserializeObject(responseString);
                    string token = json.access_token;
                    string userId = json.user.id;
                    return (token, userId);
                }
                else
                {
                    Console.WriteLine("Login Error: " + responseString);
                    return (null, null);
                }
            }
        }
    }
}
