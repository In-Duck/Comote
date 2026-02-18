
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;

namespace Host
{
    public class LoginForm : Form
    {
        // Dragging logic
        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();

        private TextBox txtEmail;
        private TextBox txtPassword;
        private Button btnLogin;
        private Label lblStatus;
        private CheckBox chkSave;
        private Button btnClose; // Custom close button

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
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            this.Size = new Size(360, 280);
            this.Text = "Login - KYMOTE Host";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None; // Borderless
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(25, 25, 28);
            this.ForeColor = Color.WhiteSmoke;
            this.Padding = new Padding(2); // For border

            // Custom Title Bar Area (Logic mostly)
            var titleLabel = new Label { 
                Text = "Login", 
                Location = new Point(20, 15), 
                AutoSize = true, 
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 215, 0) // Kymote Gold
            };
            this.Controls.Add(titleLabel);

            // Close Button
            btnClose = new Button {
                Text = "✕",
                Location = new Point(320, 10),
                Size = new Size(30, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Gray,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 40, 45);
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);

            // Dragging
            this.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left) {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };
            titleLabel.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left) {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };

            var lblEmail = new Label { Text = "Email", Location = new Point(30, 60), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 9) };
            txtEmail = new TextBox { 
                Location = new Point(30, 80), 
                Width = 300, 
                BorderStyle = BorderStyle.FixedSingle, 
                BackColor = Color.FromArgb(40, 40, 45), 
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10)
            };

            var lblPass = new Label { Text = "Password", Location = new Point(30, 120), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 9) };
            txtPassword = new TextBox { 
                Location = new Point(30, 140), 
                Width = 300, 
                PasswordChar = '●', 
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(40, 40, 45), 
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10)
            };

            chkSave = new CheckBox { Text = "Remember me", Location = new Point(30, 180), AutoSize = true, ForeColor = Color.LightGray, Font = new Font("Segoe UI", 9) };

            btnLogin = new Button { 
                Text = "Sign In", 
                Location = new Point(30, 220), 
                Width = 300, 
                Height = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(255, 215, 0), // Kymote Gold
                ForeColor = Color.Black,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            btnLogin.FlatAppearance.BorderSize = 0;
            btnLogin.Click += async (s, e) => await LoginAsync();

            lblStatus = new Label { Text = "", Location = new Point(30, 195), Width = 300, ForeColor = Color.IndianRed, Font = new Font("Segoe UI", 8) };

            this.Controls.Add(lblEmail);
            this.Controls.Add(txtEmail);
            this.Controls.Add(lblPass);
            this.Controls.Add(txtPassword);
            this.Controls.Add(chkSave);
            this.Controls.Add(btnLogin);
            this.Controls.Add(lblStatus);
            
            this.AcceptButton = btnLogin;
        }

        private void LoadSavedCredentials()
        {
            if (!File.Exists("login.dat")) return;
            try
            {
                var lines = File.ReadAllLines("login.dat");
                // [Security Fix] Check version header
                if (lines.Length >= 3 && lines[0] == "KYMOTE_SEC_V1")
                {
                    byte[] encEmail = Convert.FromBase64String(lines[1]);
                    byte[] encPass  = Convert.FromBase64String(lines[2]);
                    txtEmail.Text    = Encoding.UTF8.GetString(ProtectedData.Unprotect(encEmail, null, DataProtectionScope.CurrentUser));
                    txtPassword.Text = Encoding.UTF8.GetString(ProtectedData.Unprotect(encPass,  null, DataProtectionScope.CurrentUser));
                    chkSave.Checked  = true;
                }
                else
                {
                    // Version mismatch or legacy file -> Delete
                    try { File.Delete("login.dat"); } catch { }
                }
            }
            catch
            {
                // Decryption failed -> Delete
                try { File.Delete("login.dat"); } catch { }
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
                        byte[] encEmail = ProtectedData.Protect(Encoding.UTF8.GetBytes(email),    null, DataProtectionScope.CurrentUser);
                        byte[] encPass  = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
                        // [Security Fix] Add version header
                        File.WriteAllLines("login.dat", new[] { "KYMOTE_SEC_V1", Convert.ToBase64String(encEmail), Convert.ToBase64String(encPass) });
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


        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Draw Border
            ControlPaint.DrawBorder(e.Graphics, this.ClientRectangle, Color.FromArgb(60, 60, 60), ButtonBorderStyle.Solid);
        }
    }
}
