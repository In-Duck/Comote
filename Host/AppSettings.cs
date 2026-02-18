
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
            AppSettings? settings = null;

            // 1. 임베디드 리소스 우선 로드 (단일 파일 배포용 - API Key 등 기본값)
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("Host.appsettings.json"))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            settings = JsonConvert.DeserializeObject<AppSettings>(reader.ReadToEnd());
                        }
                    }
                }
            }
            catch { }

            // 2. 파일 시스템에서 런타임 값(HostId 등) 병합
            // 임베디드 리소스에 HostId가 없으므로 파일에서 반드시 읽어야 함
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                try
                {
                    var fileSettings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
                    if (fileSettings != null)
                    {
                        if (settings == null)
                            return fileSettings;
                        // 파일의 HostId를 임베디드 설정에 병합
                        if (!string.IsNullOrEmpty(fileSettings.HostId))
                            settings.HostId = fileSettings.HostId;
                    }
                }
                catch { }
            }

            return settings ?? new AppSettings();
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
