using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Viewer
{
    public class AppSettings
    {
        public string FrameRate { get; set; } = "상";
        public string Quality { get; set; } = "상";
        public bool AutoClipboard { get; set; } = true;
        public bool AlwaysOnTop { get; set; }
        public bool RememberWindowSize { get; set; } = true;
        public string WheelSensitivity { get; set; } = "보통";
        public bool EnableFullscreenShortcut { get; set; } = true;
        public bool PreventSleep { get; set; } = true;
        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 800;
        public List<string> HostOrder { get; set; } = new();

        public string PusherAppKey { get; set; } = "50ef3c55ccd8c468f604";
        public string PusherCluster { get; set; } = "ap3";
        public string SupabaseUrl { get; set; } = "https://xhdpmxarnkntbkwqobzm.supabase.co";
        public string SupabaseAnonKey { get; set; } = "sb_publishable_0DND4OEVX8lTX5qGIgs1Xg_Bnm7ORhO";
        public string WebAuthUrl { get; set; } = "https://comote-remote.dopum54.chatgpt.site/api/pusher/auth";

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Comote", "Viewer");

        public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

        public static AppSettings Load()
        {
            var settings = new AppSettings();

            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var loaded = JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(SettingsFilePath));
                    if (loaded != null)
                    {
                        var defaults = settings;
                        if (string.IsNullOrWhiteSpace(loaded.PusherAppKey)) loaded.PusherAppKey = defaults.PusherAppKey;
                        if (string.IsNullOrWhiteSpace(loaded.PusherCluster)) loaded.PusherCluster = defaults.PusherCluster;
                        if (string.IsNullOrWhiteSpace(loaded.SupabaseUrl)) loaded.SupabaseUrl = defaults.SupabaseUrl;
                        if (string.IsNullOrWhiteSpace(loaded.SupabaseAnonKey)) loaded.SupabaseAnonKey = defaults.SupabaseAnonKey;
                        if (string.IsNullOrWhiteSpace(loaded.WebAuthUrl)) loaded.WebAuthUrl = defaults.WebAuthUrl;
                        settings = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Load error: {ex.Message}");
            }

            ApplyEnvironmentOverrides(settings);
            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
                Console.WriteLine($"[Settings] Saved to {SettingsFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Save error: {ex.Message}");
            }
        }

        public IReadOnlyList<string> GetConfigurationErrors()
        {
            var errors = new List<string>();

            if (!Uri.TryCreate(SupabaseUrl, UriKind.Absolute, out _))
                errors.Add("SupabaseUrl");
            if (string.IsNullOrWhiteSpace(SupabaseAnonKey))
                errors.Add("SupabaseAnonKey");
            if (string.IsNullOrWhiteSpace(PusherAppKey))
                errors.Add("PusherAppKey");
            if (string.IsNullOrWhiteSpace(PusherCluster))
                errors.Add("PusherCluster");
            if (!Uri.TryCreate(WebAuthUrl, UriKind.Absolute, out _))
                errors.Add("WebAuthUrl");

            return errors;
        }

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

        private static void ApplyEnvironmentOverrides(AppSettings settings)
        {
            SetIfPresent("COMOTE_PUSHER_APP_KEY", value => settings.PusherAppKey = value);
            SetIfPresent("COMOTE_PUSHER_CLUSTER", value => settings.PusherCluster = value);
            SetIfPresent("COMOTE_SUPABASE_URL", value => settings.SupabaseUrl = value.TrimEnd('/'));
            SetIfPresent("COMOTE_SUPABASE_ANON_KEY", value => settings.SupabaseAnonKey = value);
            SetIfPresent("COMOTE_WEB_AUTH_URL", value => settings.WebAuthUrl = value);
        }

        private static void SetIfPresent(string name, Action<string> setter)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                setter(value.Trim());
        }
    }
}
