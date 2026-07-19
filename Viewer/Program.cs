using System.Linq;
using System;
using System.Windows;
using SIPSorceryMedia.FFmpeg;

namespace Viewer
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var demoMode =
                args.Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase)) ||
                Environment.GetEnvironmentVariable("COMOTE_DEMO_MODE") == "1";
            var directArgumentPresent =
                LanArguments.TryParse(
                    args,
                    out var directArguments,
                    out var directArgumentError);
            var hubArgumentPresent =
                HubArguments.TryParse(
                    args,
                    out var hubArguments,
                    out var hubArgumentError);
            var managerHubMode = args.Any(arg =>
                arg.Equals("--manager-hub", StringComparison.OrdinalIgnoreCase));

            // [Style] Mono Vintage Console Styling
            try
            {
                Console.Title = "KYMOTE Viewer";
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Clear();
                Console.WriteLine("=================================================");
                Console.WriteLine("    KYMOTE - Premium Remote Control (v1.3.0)     ");
                Console.WriteLine("=================================================");
                Console.WriteLine("");
            }
            catch { }

            Console.WriteLine("KYMOTE Viewer v1.3.0 starting...");
            
            // WPF Application 객체를 가장 먼저 생성하여 시스템 DLL 로딩을 보장
            Console.WriteLine("[DEBUG] Creating Application...");
            var app = new Application();
            
            try
            {
                var uri = new Uri("pack://application:,,,/Viewer;component/Styles.xaml");
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
                Console.WriteLine("[UI] Styles.xaml loaded");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Failed to load styles: {ex.Message}");
            }
            


            try
            {
                // FFmpeg 네이티브 DLL 초기화 (H.264 디코딩에 필수)
                string ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
                Console.WriteLine($"[FFmpeg] Path: {ffmpegPath}");
                FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);
                Console.WriteLine("[FFmpeg] Initialized");

                app.DispatcherUnhandledException += (s, e) =>
                {
                    Console.WriteLine($"[UNHANDLED] {e.Exception}");
                    e.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Console.WriteLine($"[FATAL] {e.ExceptionObject}");
                };

                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                
                Console.WriteLine("[DEBUG] Showing LoginWindow...");
                if (demoMode)
                {
                    var demoWindow = new MainWindow("demo-token", "demo-user", true);
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(demoWindow);
                    return;
                }

                if (hubArgumentPresent)
                {
                    if (hubArguments == null)
                    {
                        MessageBox.Show(
                            hubArgumentError ?? "Hub 인수가 올바르지 않습니다.",
                            "Comote Manager Hub");
                        return;
                    }

                    var hubWindow = new MainWindow(
                        hubArguments.Port,
                        hubArguments.Password,
                        hubArguments.AutoConnectId);
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(hubWindow);
                    return;
                }

                if (directArgumentPresent)
                {
                    if (directArguments == null)
                    {
                        MessageBox.Show(
                            directArgumentError ?? "직접 연결 인수가 올바르지 않습니다.",
                            "Comote Direct Manager");
                        return;
                    }

                    var directWindow = new MainWindow(
                        "direct",
                        "direct-manager",
                        directArguments.Host,
                        directArguments.Port,
                        directArguments.Password);
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(directWindow);
                    return;
                }

                if (managerHubMode)
                {
                    var setup = new ManagerHubStartWindow();
                    if (setup.ShowDialog() != true) return;
                    var hubWindow = new MainWindow(
                        setup.Port,
                        setup.AccessPassword);
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(hubWindow);
                    return;
                }

                var loginWindow = new LoginWindow();
                bool? result = loginWindow.ShowDialog();

                if (result == true)
                {
                    Console.WriteLine("[DEBUG] Login successful. Creating MainWindow...");
                    var window = new MainWindow(loginWindow.AccessToken, loginWindow.UserId);
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(window);
                }
                else
                {
                    Console.WriteLine("[DEBUG] Login cancelled.");
                }

                Console.WriteLine("[DEBUG] app.Run() returned normally");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STARTUP CRASH] {ex}");
            }

            Console.WriteLine("[DEBUG] Press Enter to exit...");
            Console.ReadLine();
        }
    }
}
