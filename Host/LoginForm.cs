
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
            this.Size = new Size(360, 300);
            this.Text = "KYMOTE 호스트 로그인";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None; // Borderless
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(5, 5, 5); // Black
            this.ForeColor = Color.FromArgb(255, 176, 0); // Amber
            this.Padding = new Padding(1); // For border

            // Custom Title Bar Area
            var titleLabel = new Label { 
                Text = "KYMOTE :: LOGIN", 
                Location = new Point(20, 15), 
                AutoSize = true, 
                Font = new Font("Consolas", 14, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 176, 0) // Amber
            };
            this.Controls.Add(titleLabel);

            // Close Button
            btnClose = new Button {
                Text = "X",
                Location = new Point(320, 10),
                Size = new Size(30, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Red,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Font = new Font("Consolas", 10, FontStyle.Bold)
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

            var lblEmail = new Label { Text = "이메일 / 아이디", Location = new Point(30, 60), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Consolas", 10) };
            txtEmail = new TextBox { 
                Location = new Point(30, 80), 
                Width = 300, 
                BorderStyle = BorderStyle.FixedSingle, 
                BackColor = Color.FromArgb(20, 20, 20), 
                ForeColor = Color.FromArgb(255, 176, 0),
                Font = new Font("Consolas", 11)
            };

            var lblPass = new Label { Text = "비밀번호", Location = new Point(30, 120), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Consolas", 10) };
            txtPassword = new TextBox { 
                Location = new Point(30, 140), 
                Width = 300, 
                PasswordChar = '*', 
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(20, 20, 20), 
                ForeColor = Color.FromArgb(255, 176, 0),
                Font = new Font("Consolas", 11)
            };

            chkSave = new CheckBox { Text = "로그인 정보 저장", Location = new Point(30, 180), AutoSize = true, ForeColor = Color.LightGray, Font = new Font("Consolas", 9) };

            btnLogin = new Button { 
                Text = "[ 로 그 인 ]", 
                Location = new Point(30, 220), 
                Width = 300, 
                Height = 40,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent, 
                ForeColor = Color.FromArgb(255, 176, 0),
                Cursor = Cursors.Hand,
                Font = new Font("Consolas", 12, FontStyle.Bold)
            };
            btnLogin.FlatAppearance.BorderColor = Color.FromArgb(255, 176, 0);
            btnLogin.FlatAppearance.BorderSize = 1;
            btnLogin.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 176, 0);
            
            // Hover effect for text color change requires manual handling in WinForms or custom control, 
            // but setting MouseOverBackColor handles the background. The text color won't auto-invert easily without custom paint.
            // We'll stick to a simple color change style.
            btnLogin.MouseEnter += (s, e) => { btnLogin.ForeColor = Color.Black; };
            btnLogin.MouseLeave += (s, e) => { btnLogin.ForeColor = Color.FromArgb(255, 176, 0); };

            btnLogin.Click += async (s, e) => await LoginAsync();

            lblStatus = new Label { Text = "", Location = new Point(30, 198), Width = 300, ForeColor = Color.Red, Font = new Font("Consolas", 9) };

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
            lblStatus.Text = "로그인 중...";
            lblStatus.ForeColor = Color.FromArgb(0, 255, 65); // Green

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
                    lblStatus.Text = "로그인 실패. 정보를 확인하세요.";
                    lblStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "오류: " + ex.Message;
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
