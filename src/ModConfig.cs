using System;
using System.IO;
using Newtonsoft.Json;

namespace RainyLove
{
    /// <summary>
    /// Self-contained JSON configuration for the official Workshop build.
    /// Replaces the BepInEx Config file. Stored at Context.PackageRoot/config.json.
    ///
    /// The install path differs per user, so NEVER hard-code it: every path derives from PackageRoot,
    /// which Entry sets from Context.PackageRoot before Load().
    /// </summary>
    internal static class ModConfig
    {
        internal const string DefaultRainyBase = "rainylove";
        internal const string DefaultAizoBase = "AIZO";

        /// <summary>Extensions tried in order when resolving a default audio file. .wav wins so a user-supplied
        /// lossless/PCM override still takes precedence over the packaged .mp3.</summary>
        internal static readonly string[] AudioExtensions = { ".wav", ".mp3" };

        /// <summary>Absolute path to the mod folder. Set by Entry from Context.PackageRoot before Load().</summary>
        internal static string PackageRoot { get; set; }

        // ===== RainyLove =====
        public static bool Enabled = true;
        public static float Volume = 0.4f;
        public static string AudioFilePath = "";
        public static float ScanInterval = 0.25f;
        public static bool OnlyWhenSelfIsLast = false;
        public static int ResampleHz = 44100;       // Retained for compatibility; playback format follows the source file.

        // ===== AIZO =====
        public static bool AizoEnabled = true;
        public static float AizoVolume = 0.4f;
        public static string AizoAudioFilePath = "";
        public static float AizoHealthPercent = 30f; // 0~99 (%)
        public static float AizoHealthValue = 30f;    // 0~9999 (fixed value)

        // ===== Main =====
        public static AudioPriority AudioPriority = AudioPriority.RainyLoveFirst;
        public static string MenuKey = "f7";

        private static Settings _settings = new Settings();

        public static void Load()
        {
            if (string.IsNullOrEmpty(PackageRoot)) return;
            string path = Path.Combine(PackageRoot, "config.json");
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path));
                    if (loaded != null) _settings = loaded;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 读取 config.json 失败，使用默认设置：" + e.Message);
            }
            SyncFromSettings();
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(PackageRoot)) return;
            SyncToSettings();
            string path = Path.Combine(PackageRoot, "config.json");
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(_settings, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 写入 config.json 失败：" + e.Message);
            }
        }

        private static void SyncFromSettings()
        {
            Enabled = _settings.Enabled;
            Volume = _settings.Volume;
            AudioFilePath = _settings.AudioFilePath ?? "";
            ScanInterval = _settings.ScanInterval;
            OnlyWhenSelfIsLast = _settings.OnlyWhenSelfIsLast;
            ResampleHz = _settings.ResampleHz;
            AizoEnabled = _settings.AizoEnabled;
            AizoVolume = _settings.AizoVolume;
            AizoAudioFilePath = _settings.AizoAudioFilePath ?? "";
            AizoHealthPercent = _settings.AizoHealthPercent;
            AizoHealthValue = _settings.AizoHealthValue;
            AudioPriority = _settings.AudioPriority;
            MenuKey = _settings.MenuKey ?? "f7";
        }

        private static void SyncToSettings()
        {
            _settings.Enabled = Enabled;
            _settings.Volume = Volume;
            _settings.AudioFilePath = AudioFilePath;
            _settings.ScanInterval = ScanInterval;
            _settings.OnlyWhenSelfIsLast = OnlyWhenSelfIsLast;
            _settings.ResampleHz = ResampleHz;
            _settings.AizoEnabled = AizoEnabled;
            _settings.AizoVolume = AizoVolume;
            _settings.AizoAudioFilePath = AizoAudioFilePath;
            _settings.AizoHealthPercent = AizoHealthPercent;
            _settings.AizoHealthValue = AizoHealthValue;
            _settings.AudioPriority = AudioPriority;
            _settings.MenuKey = MenuKey;
        }

        /// <summary>Resolve the absolute audio file path from config; baseName is "rainylove" / "AIZO" without an
        /// extension. A configured path may be a file or a directory (see AudioFilePath / AizoAudioFilePath).
        /// Falls back to PackageRoot/Asset then PackageRoot, trying .wav before .mp3.</summary>
        internal static string ResolveAudioPath(string baseName, out bool fileExists)
        {
            fileExists = false;
            if (string.IsNullOrEmpty(PackageRoot)) return baseName + AudioExtensions[0];

            string configuredKey = baseName == DefaultAizoBase ? AizoAudioFilePath : AudioFilePath;
            var configured = (configuredKey ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(configured))
            {
                if (File.Exists(configured))
                {
                    fileExists = true;
                    return configured;
                }
                if (Directory.Exists(configured))
                {
                    foreach (var ext in AudioExtensions)
                    {
                        string p = Path.Combine(configured, baseName + ext);
                        if (File.Exists(p)) { fileExists = true; return p; }
                    }
                }
            }

            foreach (var dir in new[] { Path.Combine(PackageRoot, "Asset"), PackageRoot })
            {
                foreach (var ext in AudioExtensions)
                {
                    string p = Path.Combine(dir, baseName + ext);
                    if (File.Exists(p)) { fileExists = true; return p; }
                }
            }

            // Nothing found: report the most likely location so the warning names a concrete path.
            return Path.Combine(PackageRoot, "Asset", baseName + AudioExtensions[0]);
        }

        // Settings DTO (instance) serialized to JSON. Newtonsoft maps the AudioPriority enum by name.
        private class Settings
        {
            public bool Enabled = true;
            public float Volume = 0.4f;
            public string AudioFilePath = "";
            public float ScanInterval = 0.25f;
            public bool OnlyWhenSelfIsLast = false;
            public int ResampleHz = 44100;
            public bool AizoEnabled = true;
            public float AizoVolume = 0.4f;
            public string AizoAudioFilePath = "";
            public float AizoHealthPercent = 30f;
            public float AizoHealthValue = 30f;
            public AudioPriority AudioPriority = AudioPriority.RainyLoveFirst;
            public string MenuKey = "f7";
        }
    }
}
