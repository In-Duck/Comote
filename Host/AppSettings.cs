using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Host
{
    public class AppSettings
    {
        private const string SettingsFileName = "hostsettings.json";

        public PusherConfig Pusher { get; set; } = new();
        public string? DefaultHostName { get; set; }
        public string? DefaultPassword { get; set; }
        public string? HostId { get; set; }
        public InputBackendMode InputBackendMode { get; set; } =
            InputBackendMode.VirtualHid;
        public string SupabaseUrl { get; set; } = "";
        public string SupabaseAnonKey { get; set; } = "";
        public string WebAuthUrl { get; set; } = "https://kymote.vercel.app/api/pusher/auth";

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Comote");

        public static string SettingsFilePath => Path.Combine(DataDirectory, SettingsFileName);

        public class PusherConfig
        {
            public string AppKey { get; set; } = "";
            public string Cluster { get; set; } = "ap3";
        }

        public static AppSettings Load()
        {
            var settings = LoadEmbeddedDefaults() ?? new AppSettings();

            if (File.Exists(SettingsFilePath))
            {
                try
                {
                    var fileSettings = JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(SettingsFilePath));
                    if (fileSettings != null)
                    {
                        Merge(settings, fileSettings);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] Failed to read {SettingsFilePath}: {ex.Message}");
                }
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Failed to save {SettingsFilePath}: {ex.Message}");
            }
        }

        public IReadOnlyList<string> GetConfigurationErrors()
        {
            var errors = new List<string>();

            if (!Uri.TryCreate(SupabaseUrl, UriKind.Absolute, out _))
                errors.Add("SupabaseUrl");
            if (string.IsNullOrWhiteSpace(SupabaseAnonKey))
                errors.Add("SupabaseAnonKey");
            if (string.IsNullOrWhiteSpace(Pusher.AppKey))
                errors.Add("Pusher.AppKey");
            if (string.IsNullOrWhiteSpace(Pusher.Cluster))
                errors.Add("Pusher.Cluster");
            if (!Uri.TryCreate(WebAuthUrl, UriKind.Absolute, out _))
                errors.Add("WebAuthUrl");

            return errors;
        }

        private static AppSettings? LoadEmbeddedDefaults()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("Host.appsettings.json");
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                return JsonConvert.DeserializeObject<AppSettings>(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Failed to read embedded defaults: {ex.Message}");
                return null;
            }
        }

        private static void Merge(AppSettings target, AppSettings source)
        {
            if (!string.IsNullOrWhiteSpace(source.Pusher.AppKey))
                target.Pusher.AppKey = source.Pusher.AppKey;
            if (!string.IsNullOrWhiteSpace(source.Pusher.Cluster))
                target.Pusher.Cluster = source.Pusher.Cluster;
            if (!string.IsNullOrWhiteSpace(source.SupabaseUrl))
                target.SupabaseUrl = source.SupabaseUrl;
            if (!string.IsNullOrWhiteSpace(source.SupabaseAnonKey))
                target.SupabaseAnonKey = source.SupabaseAnonKey;
            if (!string.IsNullOrWhiteSpace(source.WebAuthUrl))
                target.WebAuthUrl = source.WebAuthUrl;
            if (!string.IsNullOrWhiteSpace(source.DefaultHostName))
                target.DefaultHostName = source.DefaultHostName;
            if (source.DefaultPassword != null)
                target.DefaultPassword = source.DefaultPassword;
            if (!string.IsNullOrWhiteSpace(source.HostId))
                target.HostId = source.HostId;
            if (Enum.IsDefined(source.InputBackendMode))
                target.InputBackendMode = source.InputBackendMode;
        }

        private static void ApplyEnvironmentOverrides(AppSettings settings)
        {
            SetIfPresent("COMOTE_PUSHER_APP_KEY", value => settings.Pusher.AppKey = value);
            SetIfPresent("COMOTE_PUSHER_CLUSTER", value => settings.Pusher.Cluster = value);
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
