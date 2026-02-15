using System;
using System.IO;
using Newtonsoft.Json;

namespace Viewer
{
    /// <summary>
    /// Viewer 환경 설정 값 저장/로드.
    /// 설정 파일은 EXE 옆에 settings.json으로 저장됩니다.
    /// </summary>
    public class AppSettings
    {
        // === 원격제어 옵션 ===

        /// <summary>출력프레임수: "최상"(60) / "상"(30) / "중"(20) / "하"(10)</summary>
        public string FrameRate { get; set; } = "상";

        /// <summary>출력품질: "최상" / "상" / "중" / "하"</summary>
        public string Quality { get; set; } = "상";

        /// <summary>클립보드 자동 동기화</summary>
        public bool AutoClipboard { get; set; } = true;

        /// <summary>원격제어 창 최상단 유지</summary>
        public bool AlwaysOnTop { get; set; } = false;

        /// <summary>창 크기 및 위치 기억</summary>
        public bool RememberWindowSize { get; set; } = true;

        /// <summary>마우스 휠 민감도: "빠름" / "보통" / "느림"</summary>
        public string WheelSensitivity { get; set; } = "보통";

        /// <summary>풀스크린 단축키 활성화</summary>
        public bool EnableFullscreenShortcut { get; set; } = true;

        /// <summary>자동 절전모드 진입 방지</summary>
        public bool PreventSleep { get; set; } = true;

        /// <summary>저장된 창 너비</summary>
        public double WindowWidth { get; set; } = 1200;

        /// <summary>저장된 창 높이</summary>
        public double WindowHeight { get; set; } = 800;

        // === 시그널링 설정 (Pusher) ===
        public string PusherAppId { get; set; } = "2114862";
        public string PusherAppKey { get; set; } = "50ef3c55ccd8c468f604";
        public string PusherSecret { get; set; } = "18c2f6cbcfe14071733b";
        public string PusherCluster { get; set; } = "ap3";
        
        // Supabase Settings
        public string SupabaseUrl { get; set; } = "https://nlodelehewbbniayzjuv.supabase.co";
        public string SupabaseAnonKey { get; set; } = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im5sb2RlbGVoZXdiYm5pYXl6anV2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzExMzc1MjksImV4cCI6MjA4NjcxMzUyOX0.-r_qNDErJPWLma1i3wjXZmvzXAZUGtHHK-L3YMcZYb4";
        public string WebAuthUrl { get; set; } = "https://comote.vercel.app/api/pusher/auth";

        // === 파일 경로 ===
        private static readonly string SettingsFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        /// <summary>설정 파일에서 로드. 실패 시 기본값 반환.</summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null)
                    {
                        Console.WriteLine("[Settings] Loaded from file");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Load error: {ex.Message}");
            }
            Console.WriteLine("[Settings] Using defaults");
            return new AppSettings();
        }

        /// <summary>현재 설정을 파일에 저장.</summary>
        public void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
                Console.WriteLine("[Settings] Saved");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Save error: {ex.Message}");
            }
        }

        // === 헬퍼: 프레임레이트 값 변환 ===
        public int GetTargetFps()
        {
            return FrameRate switch
            {
                "최상" => 60,
                "상" => 30,
                "중" => 20,
                "하" => 10,
                _ => 30
            };
        }

        // === 헬퍼: 품질 → 비트레이트 배율 ===
        public double GetQualityMultiplier()
        {
            return Quality switch
            {
                "최상" => 1.5,
                "상" => 1.0,
                "중" => 0.6,
                "하" => 0.3,
                _ => 1.0
            };
        }

        // === 헬퍼: 휠 민감도 배율 ===
        public double GetWheelMultiplier()
        {
            return WheelSensitivity switch
            {
                "빠름" => 2.0,
                "보통" => 1.0,
                "느림" => 0.5,
                _ => 1.0
            };
        }
    }
}
