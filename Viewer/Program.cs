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
            Console.WriteLine("[DEBUG] Main() started");
            
            // WPF Application 객체를 가장 먼저 생성하여 시스템 DLL 로딩을 보장
            Console.WriteLine("[DEBUG] Creating Application...");
            var app = new Application();

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

                Console.WriteLine("[DEBUG] Creating MainWindow...");
                var window = new MainWindow();

                Console.WriteLine("[DEBUG] Calling app.Run()...");
                app.Run(window);

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
