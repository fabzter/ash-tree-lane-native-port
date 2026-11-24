using System;
using System.IO;
using System.Text.Json;
using UnityShim.Backend;

namespace AshTreeLane
{
    public sealed class UserSettings
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public bool Fullscreen { get; set; } = false;
        public bool ControllerEnabled { get; set; } = true;

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static string ResolveDefaultPath(string explicitConfigPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitConfigPath))
            {
                return Path.GetFullPath(explicitConfigPath);
            }
            string baseDir = AppContext.BaseDirectory.Replace('\\', '/');
            if (baseDir.Contains(".app/Contents/MacOS", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "settings.json"));
            }
            return Path.GetFullPath(Path.Combine(EngineHost.AssetRoot, "..", "settings.json"));
        }

        public static UserSettings Load(string path)
        {
            var cfg = new UserSettings();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return cfg;
            try
            {
                var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
                if (loaded != null)
                {
                    cfg.Width = Math.Clamp(loaded.Width, 640, 7680);
                    cfg.Height = Math.Clamp(loaded.Height, 360, 4320);
                    cfg.Fullscreen = loaded.Fullscreen;
                    cfg.ControllerEnabled = loaded.ControllerEnabled;
                    Console.WriteLine($"[Settings] Loaded {path}: {cfg.Width}x{cfg.Height} fullscreen={cfg.Fullscreen} controller={cfg.ControllerEnabled}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Settings] Failed to read {path}: {ex.Message}");
            }
            return cfg;
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
                Console.WriteLine($"[Settings] Saved {path}: {Width}x{Height} fullscreen={Fullscreen} controller={ControllerEnabled}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Settings] Failed to save {path}: {ex.Message}");
            }
        }
    }
}
