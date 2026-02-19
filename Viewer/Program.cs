using System;
using System.Windows;
using SIPSorceryMedia.FFmpeg;

namespace Viewer
{
    public class Program
    {
        [STAThread]
        public static void Main()
        {
            // [Style] Mono Vintage Console Styling
            try
            {
                Console.Title = "KYMOTE Viewer";
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Clear();
                Console.WriteLine("=================================================");
                Console.WriteLine("    KYMOTE - Premium Remote Control (v1.2.1)     ");
                Console.WriteLine("=================================================");
                Console.WriteLine("");
            }
            catch { }

            Console.WriteLine("[DEBUG] Main() started");
            
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
