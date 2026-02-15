
using System;
using System.IO;
using Newtonsoft.Json;

namespace Host
{
    public class AppSettings
    {
        public PusherConfig Pusher { get; set; } = new PusherConfig();
        public string? DefaultHostName { get; set; }
        public string? DefaultPassword { get; set; }
        public string? HostId { get; set; }

        public class PusherConfig
        {
            public string AppId { get; set; } = "2114862";
            public string AppKey { get; set; } = "50ef3c55ccd8c468f604";
            public string Secret { get; set; } = "18c2f6cbcfe14071733b";
            public string Cluster { get; set; } = "ap3";
        }

        // Supabase Settings
        public string SupabaseUrl { get; set; } = "https://nlodelehewbbniayzjuv.supabase.co";
        public string SupabaseAnonKey { get; set; } = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im5sb2RlbGVoZXdiYm5pYXl6anV2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzExMzc1MjksImV4cCI6MjA4NjcxMzUyOX0.-r_qNDErJPWLma1i3wjXZmvzXAZUGtHHK-L3YMcZYb4";
        public string WebAuthUrl { get; set; } = "https://comote.vercel.app/api/pusher/auth";

        public static AppSettings Load()
        {
            try
            {
                // 1. 임베디드 리소스 우선 로드 (단일 파일 배포용)
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("Host.appsettings.json"))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            string json = reader.ReadToEnd();
                            return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                        }
                    }
                }
            }
            catch
            {
                // 리소스 로드 실패 시 무시하고 파일 시도
            }

            // 2. 파일 시스템 로드 (개발 환경용)
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                // 기본값 또는 빈 설정 반환
                return new AppSettings();
            }

            try
            {
                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
