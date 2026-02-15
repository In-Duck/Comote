
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;

namespace Host
{
    public class LoginForm : Form
    {
        private TextBox txtEmail;
        private TextBox txtPassword;
        private Button btnLogin;
        private Label lblStatus;
        private CheckBox chkSave;

        public string AccessToken { get; private set; }
        public string UserEmail { get; private set; }
        public string UserId { get; private set; }

        private AppSettings _settings;

        public LoginForm(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            LoadSavedCredentials();
        }

        private void InitializeComponent()
        {
            this.Size = new Size(350, 250);
            this.Text = "Login - Comote Host";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            var lblEmail = new Label { Text = "Email:", Location = new Point(20, 20), AutoSize = true };
            txtEmail = new TextBox { Location = new Point(100, 20), Width = 200 };

            var lblPass = new Label { Text = "Password:", Location = new Point(20, 60), AutoSize = true };
            txtPassword = new TextBox { Location = new Point(100, 60), Width = 200, PasswordChar = '*' };

            chkSave = new CheckBox { Text = "Save Login", Location = new Point(100, 90), AutoSize = true };

            btnLogin = new Button { Text = "Login", Location = new Point(100, 130), Width = 100, Height = 30 };
            btnLogin.Click += async (s, e) => await LoginAsync();

            lblStatus = new Label { Text = "", Location = new Point(20, 170), Width = 300, ForeColor = Color.Red };

            this.Controls.Add(lblEmail);
            this.Controls.Add(txtEmail);
            this.Controls.Add(lblPass);
            this.Controls.Add(txtPassword);
            this.Controls.Add(chkSave);
            this.Controls.Add(btnLogin);
            this.Controls.Add(lblStatus);
            
            this.AcceptButton = btnLogin;
        }

        private async void LoadSavedCredentials()
        {
            if (File.Exists("login.dat"))
            {
                try {
                    var lines = File.ReadAllLines("login.dat");
                    if (lines.Length >= 2)
                    {
                        txtEmail.Text = lines[0];
                        txtPassword.Text = lines[1]; // Plaintext for now (PoC)
                        chkSave.Checked = true;
                    }
                } catch { }
            }
        }

        private async Task LoginAsync()
        {
            btnLogin.Enabled = false;
            lblStatus.Text = "Logging in...";
            lblStatus.ForeColor = Color.Blue;

            string email = txtEmail.Text;
            string password = txtPassword.Text;

            try
            {
                // Authenticate with Supabase REST API
                var token = await SignInWithEmailPassword(email, password);
                if (token != null)
                {
                    AccessToken = token;
                    UserEmail = email;
                    
                    if (chkSave.Checked)
                    {
                        File.WriteAllLines("login.dat", new[] { email, password });
                    }
                    else
                    {
                        if (File.Exists("login.dat")) File.Delete("login.dat");
                    }

                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else
                {
                    lblStatus.Text = "Login failed. Check credentials.";
                    lblStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.ForeColor = Color.Red;
            }
            finally
            {
                btnLogin.Enabled = true;
            }
        }

        private async Task<string> SignInWithEmailPassword(string email, string password)
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
                    // user.id를 UserId 프로퍼티에 저장 (Supabase heartbeat용)
                    UserId = (string)json.user?.id ?? "";
                    return (string)json.access_token;
                }
                else
                {
                    Console.WriteLine("Login Error: " + responseString);
                    return null;
                }
            }
        }
    }
}
