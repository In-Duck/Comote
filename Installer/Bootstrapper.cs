using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ComoteInstaller
{
    internal sealed class ProductConfiguration
    {
#if CLIENT
        internal const string ProductName = "Comote Client";
        internal const string ProductId = "ComoteClient";
        internal const string ExecutableName = "ComoteClient.exe";
        internal const string ProcessName = "ComoteClient";
        internal const string PackageUrl = InstallerBuildConfiguration.ClientPackageUrl;
        internal const string PackageSha256 =
            InstallerBuildConfiguration.ClientPackageSha256;
        internal const long PackageBytes =
            InstallerBuildConfiguration.ClientPackageBytes;
        internal static bool IsClient { get { return true; } }
#else
        internal const string ProductName = "Comote Manager";
        internal const string ProductId = "ComoteManager";
        internal const string ExecutableName = "ComoteManager.exe";
        internal const string ProcessName = "ComoteManager";
        internal const string PackageUrl = InstallerBuildConfiguration.ManagerPackageUrl;
        internal const string PackageSha256 =
            InstallerBuildConfiguration.ManagerPackageSha256;
        internal const long PackageBytes =
            InstallerBuildConfiguration.ManagerPackageBytes;
        internal static bool IsClient { get { return false; } }
#endif
        internal const string Version = InstallerBuildConfiguration.Version;
        internal const string Publisher = "Comote";
        internal const string SupportUrl = "https://comote-remote.dopum54.chatgpt.site/";
        internal const string ServiceName = "ComoteHost";

        internal static string InstallDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Comote",
                    IsClient ? "Client" : "Manager");
            }
        }

        internal static string InstallerFileName
        {
            get { return ProductId + "_Setup.exe"; }
        }
        internal static string SavedInstallerPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Comote",
                    "Installers",
                    InstallerFileName);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (HasArgument(args, "/verify"))
            {
                return VerifyConfiguration() ? 0 : 1;
            }

            if (HasArgument(args, "/uninstall"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return InstallerOperations.UninstallInteractive() ? 0 : 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
            return Environment.ExitCode;
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (string argument in args)
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool VerifyConfiguration()
        {
            Uri uri;
            byte[] hash;
            return Uri.TryCreate(ProductConfiguration.PackageUrl, UriKind.Absolute, out uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                TryParseHash(ProductConfiguration.PackageSha256, out hash) &&
                hash.Length == 32 &&
                ProductConfiguration.PackageBytes > 0;
        }

        internal static bool TryParseHash(string text, out byte[] bytes)
        {
            bytes = new byte[0];
            if (string.IsNullOrWhiteSpace(text) || text.Length != 64)
            {
                return false;
            }

            try
            {
                bytes = new byte[32];
                for (int index = 0; index < bytes.Length; index++)
                {
                    bytes[index] = Convert.ToByte(text.Substring(index * 2, 2), 16);
                }
                return true;
            }
            catch
            {
                bytes = new byte[0];
                return false;
            }
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Button _installButton;
        private readonly Button _cancelButton;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private bool _installed;

        internal InstallerForm()
        {
            Text = ProductConfiguration.ProductName + " 설치";
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(510, 265);
            BackColor = Color.FromArgb(245, 247, 250);

            _title = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 19F),
                ForeColor = Color.FromArgb(25, 32, 44),
                Location = new Point(30, 28),
                Text = ProductConfiguration.ProductName
            };
            Controls.Add(_title);

            _description = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(76, 86, 106),
                Location = new Point(33, 76),
                Size = new Size(445, 48),
                Text = "최신 버전을 안전하게 다운로드하고 설치합니다.\r\n" +
                    "설치 중에는 인터넷 연결이 필요합니다."
            };
            Controls.Add(_description);

            _status = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(55, 65, 81),
                Location = new Point(33, 137),
                Size = new Size(445, 22),
                Text = "설치 준비 완료"
            };
            Controls.Add(_status);

            _progress = new ProgressBar
            {
                Location = new Point(34, 163),
                Size = new Size(442, 13),
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(_progress);

            _cancelButton = new Button
            {
                Text = "닫기",
                DialogResult = DialogResult.Cancel,
                Location = new Point(298, 205),
                Size = new Size(85, 34),
                FlatStyle = FlatStyle.Flat
            };
            _cancelButton.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 221);
            _cancelButton.Click += delegate
            {
                if (!_installButton.Enabled)
                {
                    _cancellation.Cancel();
                }
            };
            Controls.Add(_cancelButton);

            _installButton = new Button
            {
                Text = "설치",
                Location = new Point(391, 205),
                Size = new Size(85, 34),
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _installButton.FlatAppearance.BorderSize = 0;
            _installButton.Click += async delegate
            {
                if (_installed)
                {
                    InstallerOperations.LaunchInstalledApplication();
                    Close();
                    return;
                }
                await InstallAsync();
            };
            Controls.Add(_installButton);

            AcceptButton = _installButton;
            CancelButton = _cancelButton;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_installButton.Enabled)
            {
                DialogResult result = MessageBox.Show(
                    this,
                    "설치를 취소할까요?",
                    ProductConfiguration.ProductName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancellation.Cancel();
                _status.Text = "설치를 안전하게 취소하는 중…";
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        private async Task InstallAsync()
        {
            _installButton.Enabled = false;
            _cancelButton.Text = "취소";
            UseWaitCursor = true;
            Environment.ExitCode = 1;

            Progress<InstallProgress> progress = new Progress<InstallProgress>(
                delegate(InstallProgress value)
                {
                    _progress.Value = Math.Max(0, Math.Min(100, value.Percent));
                    _status.Text = value.Message;
                });

            try
            {
                await InstallerOperations.InstallAsync(progress, _cancellation.Token);
                _progress.Value = 100;
                _status.Text = "설치가 완료되었습니다.";
                _description.Text =
                    ProductConfiguration.ProductName +
                    " 설치가 완료되었습니다.\r\n프로그램을 바로 실행할 수 있습니다.";
                _cancelButton.Text = "닫기";
                _installButton.Text = "실행";
                _installed = true;
                _installButton.Enabled = true;
                UseWaitCursor = false;
                Environment.ExitCode = 0;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "설치가 취소되었습니다.";
                _cancelButton.Text = "닫기";
                UseWaitCursor = false;
            }
            catch (Exception exception)
            {
                _status.Text = "설치하지 못했습니다.";
                _cancelButton.Text = "닫기";
                UseWaitCursor = false;
                MessageBox.Show(
                    this,
                    exception.Message,
                    "설치 오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class InstallProgress
    {
        internal InstallProgress(int percent, string message)
        {
            Percent = percent;
            Message = message;
        }

        internal int Percent { get; private set; }
        internal string Message { get; private set; }
    }

    internal static class InstallerOperations
    {
        private const string UninstallRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        private const string RunRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static async Task InstallAsync(
            IProgress<InstallProgress> progress,
            CancellationToken cancellationToken)
        {
            string workRoot = Path.Combine(
                Path.GetTempPath(),
                "ComoteInstaller",
                Guid.NewGuid().ToString("N"));
            string packagePath = Path.Combine(workRoot, "package.zip");
            string extractedPath = Path.Combine(workRoot, "extracted");
            string installPath = ProductConfiguration.InstallDirectory;
            string backupPath = installPath + ".previous";
            bool serviceWasInstalled = false;
            bool installMoved = false;

            Directory.CreateDirectory(workRoot);
            try
            {
                progress.Report(new InstallProgress(2, "다운로드 서버에 연결 중…"));
                await DownloadPackageAsync(packagePath, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                progress.Report(new InstallProgress(73, "다운로드 파일을 검증하는 중…"));
                VerifyPackage(packagePath);

                progress.Report(new InstallProgress(78, "설치 파일을 준비하는 중…"));
                ExtractPackageSafely(packagePath, extractedPath, cancellationToken);
                ValidateExtractedPackage(extractedPath);

                progress.Report(new InstallProgress(88, "실행 중인 프로그램을 정리하는 중…"));
                serviceWasInstalled = ProductConfiguration.IsClient &&
                    IsServiceInstalled(ProductConfiguration.ServiceName);
                if (serviceWasInstalled)
                {
                    RunProcess("sc.exe", "stop " + ProductConfiguration.ServiceName, true);
                    Thread.Sleep(800);
                }
                StopProductProcesses();

                progress.Report(new InstallProgress(92, "프로그램을 설치하는 중…"));
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, true);
                }
                if (Directory.Exists(installPath))
                {
                    Directory.Move(installPath, backupPath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(installPath));
                Directory.Move(extractedPath, installPath);
                installMoved = true;

                string savedInstaller = ProductConfiguration.SavedInstallerPath;
                Directory.CreateDirectory(Path.GetDirectoryName(savedInstaller));
                File.Copy(
                    Process.GetCurrentProcess().MainModule.FileName,
                    savedInstaller,
                    true);

                CreateShortcuts(installPath);
                WriteUninstallRegistration(savedInstaller);
                if (ProductConfiguration.IsClient)
                {
                    WriteClientAutoStart(installPath);
                }

                if (serviceWasInstalled)
                {
                    RunProcess("sc.exe", "start " + ProductConfiguration.ServiceName, true);
                }
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, true);
                }

                progress.Report(new InstallProgress(100, "설치 완료"));
            }
            catch
            {
                if (installMoved)
                {
                    TryDeleteDirectory(installPath);
                }
                if (Directory.Exists(backupPath) && !Directory.Exists(installPath))
                {
                    Directory.Move(backupPath, installPath);
                }
                if (serviceWasInstalled)
                {
                    RunProcess("sc.exe", "start " + ProductConfiguration.ServiceName, true);
                }
                throw;
            }
            finally
            {
                TryDeleteDirectory(workRoot);
            }
        }

        private static async Task DownloadPackageAsync(
            string destination,
            IProgress<InstallProgress> progress,
            CancellationToken cancellationToken)
        {
            using (HttpClientHandler handler = new HttpClientHandler())
            {
                handler.AllowAutoRedirect = true;
                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(30);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "ComoteInstaller/" + ProductConfiguration.Version);
                    using (HttpResponseMessage response = await client.GetAsync(
                        ProductConfiguration.PackageUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                        long total = response.Content.Headers.ContentLength ??
                            ProductConfiguration.PackageBytes;
                        if (total <= 0 || total > 500L * 1024L * 1024L)
                        {
                            throw new InvalidDataException("다운로드 파일 크기가 올바르지 않습니다.");
                        }

                        using (Stream input = await response.Content.ReadAsStreamAsync())
                        using (FileStream output = new FileStream(
                            destination,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            1024 * 1024,
                            true))
                        {
                            byte[] buffer = new byte[1024 * 1024];
                            long received = 0;
                            while (true)
                            {
                                int read = await input.ReadAsync(
                                    buffer,
                                    0,
                                    buffer.Length,
                                    cancellationToken);
                                if (read == 0)
                                {
                                    break;
                                }
                                await output.WriteAsync(
                                    buffer,
                                    0,
                                    read,
                                    cancellationToken);
                                received += read;
                                int percent = 3 + (int)Math.Min(
                                    68,
                                    received * 68L / total);
                                progress.Report(new InstallProgress(
                                    percent,
                                    string.Format(
                                        "다운로드 중 · {0:0.0} / {1:0.0} MB",
                                        received / 1048576d,
                                        total / 1048576d)));
                            }
                        }
                    }
                }
            }
        }

        private static void VerifyPackage(string packagePath)
        {
            byte[] expected;
            if (!Program.TryParseHash(ProductConfiguration.PackageSha256, out expected))
            {
                throw new InvalidDataException("내장된 파일 검증값이 올바르지 않습니다.");
            }

            byte[] actual;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(packagePath))
            {
                actual = sha.ComputeHash(stream);
            }

            if (!FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException(
                    "다운로드 파일의 무결성 검증에 실패했습니다. 설치를 중단합니다.");
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        private static void ExtractPackageSafely(
            string packagePath,
            string destination,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destination);
            string destinationRoot =
                Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string targetPath = Path.GetFullPath(
                        Path.Combine(destination, entry.FullName));
                    if (!targetPath.StartsWith(
                        destinationRoot,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("안전하지 않은 압축 파일 경로가 발견되었습니다.");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static void ValidateExtractedPackage(string extractedPath)
        {
            string executable = Path.Combine(
                extractedPath,
                ProductConfiguration.ExecutableName);
            if (!File.Exists(executable))
            {
                throw new InvalidDataException(
                    "패키지에서 " + ProductConfiguration.ExecutableName + "을 찾지 못했습니다.");
            }
        }

        private static void StopProductProcesses()
        {
            foreach (Process process in Process.GetProcessesByName(
                ProductConfiguration.ProcessName))
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2500))
                    {
                        process.Kill();
                        process.WaitForExit(2500);
                    }
                }
                catch
                {
                    // A protected stale process is handled by the file move failure.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool IsServiceInstalled(string serviceName)
        {
            return RunProcess("sc.exe", "query " + serviceName, true) == 0;
        }

        private static int RunProcess(string fileName, string arguments, bool wait)
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                    {
                        return -1;
                    }
                    if (wait)
                    {
                        process.WaitForExit(15000);
                        return process.HasExited ? process.ExitCode : -1;
                    }
                    return 0;
                }
            }
            catch
            {
                return -1;
            }
        }

        private static void CreateShortcuts(string installPath)
        {
            string target = Path.Combine(
                installPath,
                ProductConfiguration.ExecutableName);
            string commonPrograms = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonPrograms);
            string commonDesktop = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonDesktopDirectory);
            CreateShortcut(
                Path.Combine(commonPrograms, ProductConfiguration.ProductName + ".lnk"),
                target,
                installPath);
            CreateShortcut(
                Path.Combine(commonDesktop, ProductConfiguration.ProductName + ".lnk"),
                target,
                installPath);
        }

        private static void CreateShortcut(
            string shortcutPath,
            string targetPath,
            string workingDirectory)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("Windows 바로가기를 만들 수 없습니다.");
            }
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.SetProperty,
                null,
                shortcut,
                new object[] { targetPath });
            shortcutType.InvokeMember(
                "WorkingDirectory",
                BindingFlags.SetProperty,
                null,
                shortcut,
                new object[] { workingDirectory });
            shortcutType.InvokeMember(
                "IconLocation",
                BindingFlags.SetProperty,
                null,
                shortcut,
                new object[] { targetPath + ",0" });
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                null,
                shortcut,
                null);
        }

        private static void WriteUninstallRegistration(string savedInstaller)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
                UninstallRegistryPath + ProductConfiguration.ProductId))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("제거 프로그램을 등록할 수 없습니다.");
                }
                key.SetValue("DisplayName", ProductConfiguration.ProductName);
                key.SetValue("DisplayVersion", ProductConfiguration.Version);
                key.SetValue("Publisher", ProductConfiguration.Publisher);
                key.SetValue("URLInfoAbout", ProductConfiguration.SupportUrl);
                key.SetValue(
                    "DisplayIcon",
                    Path.Combine(
                        ProductConfiguration.InstallDirectory,
                        ProductConfiguration.ExecutableName));
                key.SetValue(
                    "UninstallString",
                    "\"" + savedInstaller + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void WriteClientAutoStart(string installPath)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RunRegistryPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("자동 시작을 등록할 수 없습니다.");
                }
                key.SetValue(
                    ProductConfiguration.ProductName,
                    "\"" + Path.Combine(
                        installPath,
                        ProductConfiguration.ExecutableName) + "\"");
            }
        }

        internal static bool UninstallInteractive()
        {
            DialogResult answer = MessageBox.Show(
                ProductConfiguration.ProductName + "을 제거할까요?\r\n\r\n" +
                "로그인 정보와 사용자 설정은 안전을 위해 유지됩니다.",
                ProductConfiguration.ProductName + " 제거",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return false;
            }

            try
            {
                if (ProductConfiguration.IsClient)
                {
                    RunProcess("sc.exe", "stop " + ProductConfiguration.ServiceName, true);
                    RunProcess("sc.exe", "delete " + ProductConfiguration.ServiceName, true);
                    RunProcess("sc.exe", "stop KymoteHost", true);
                    RunProcess("sc.exe", "delete KymoteHost", true);
                    using (RegistryKey run = Registry.LocalMachine.OpenSubKey(
                        RunRegistryPath,
                        true))
                    {
                        if (run != null)
                        {
                            run.DeleteValue(ProductConfiguration.ProductName, false);
                        }
                    }
                }

                StopProductProcesses();
                DeleteShortcutFiles();
                Registry.LocalMachine.DeleteSubKeyTree(
                    UninstallRegistryPath + ProductConfiguration.ProductId,
                    false);
                ScheduleSelfRemoval(ProductConfiguration.InstallDirectory);

                MessageBox.Show(
                    ProductConfiguration.ProductName + "을 제거했습니다.",
                    ProductConfiguration.ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "제거하지 못했습니다.\r\n\r\n" + exception.Message,
                    "제거 오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        private static void DeleteShortcutFiles()
        {
            string[] paths =
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    ProductConfiguration.ProductName + ".lnk"),
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonDesktopDirectory),
                    ProductConfiguration.ProductName + ".lnk")
            };
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static void ScheduleSelfRemoval(string installPath)
        {
            string escapedPath = installPath.Replace("\"", "\"\"");
            string installerPath =
                Process.GetCurrentProcess().MainModule.FileName.Replace("\"", "\"\"");
            string installerDirectory =
                Path.GetDirectoryName(installerPath).Replace("\"", "\"\"");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c timeout /t 2 /nobreak >nul & rmdir /s /q \"" +
                    escapedPath + "\" & del /f /q \"" + installerPath +
                    "\" & rmdir \"" + installerDirectory + "\" 2>nul",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        internal static void LaunchInstalledApplication()
        {
            string executable = Path.Combine(
                ProductConfiguration.InstallDirectory,
                ProductConfiguration.ExecutableName);
            if (!File.Exists(executable))
            {
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + executable + "\"",
                UseShellExecute = false
            });
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
