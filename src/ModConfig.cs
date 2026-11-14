using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Self-contained JSON configuration for the official Workshop build.
    /// Replaces the BepInEx Config file. Stored at
    /// %USERPROFILE%\AppData\LocalLow\TEAMHORAY\Sephiria\rainylove\config.json (Application.persistentDataPath),
    /// which is where every other Sephiria mod keeps its settings.
    ///
    /// Deliberately NOT inside the package folder: a subscriber's Workshop folder is re-downloaded on every update and
    /// is not a stable place for user data, so settings must live next to the game's own save data instead.
    /// PackageRoot is still used to locate the shipped audio in Asset/.
    /// </summary>
    internal static class ModConfig
    {
        internal const string DefaultRainyBase = "rainylove";
        internal const string DefaultAizoBase = "AIZO";

        /// <summary>Extensions tried in order when resolving a default audio file. .wav wins so a user-supplied
        /// lossless/PCM override still takes precedence over the packaged .mp3.</summary>
        internal static readonly string[] AudioExtensions = { ".wav", ".mp3" };

        /// <summary>Absolute path to the mod folder. Set by Entry from Context.PackageRoot before Load().
        /// Used for the shipped Asset/ audio, and as the fallback config location only.</summary>
        internal static string PackageRoot { get; set; }

        private const string ConfigFolderName = "rainylove";
        private const string ConfigFileName = "config.json";

        private static string _configPath;

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

        /// <summary>Stop policy shared by both sources.
        /// true  = when the trigger condition ends, defer the stop to the next timeline node (p2/p3/...);
        /// false = stop the instant the condition ends (a revived teammate silences RainyLove at once, and health
        ///         recovering above the AIZO threshold silences AIZO at once).
        /// Default false: the node deferral can legitimately keep playing for up to a whole track, which players
        /// report as 'the music never stops', so the predictable behaviour is the default.
        /// The in-game label is 顺延播放（到时间轴节点再停）-- named after what the option adds (delayed playback tail)
        /// rather than 顺延停止, which read as 'delay the stop' and confused players.
        /// The field name stays DeferStop because it is also the JSON key in config.json; renaming it would silently
        /// reset every existing user's setting.</summary>
        public static bool DeferStop = false;

        private static Settings _settings = new Settings();

        /// <summary>Absolute path of the live settings file, resolved once on first use.
        /// Primary: %USERPROFILE%\AppData\LocalLow\TEAMHORAY\Sephiria\rainylove\config.json.
        /// Fallback (only if persistentDataPath is unavailable): PackageRoot\config.json.</summary>
        internal static string ConfigPath
        {
            get { return _configPath ?? (_configPath = ResolveConfigPath()); }
        }

        private static string ResolveConfigPath()
        {
            try
            {
                string root = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(root))
                    return Path.Combine(Path.Combine(root, ConfigFolderName), ConfigFileName);
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 取不到持久化目录，配置退回 mod 目录：" + e.Message);
            }
            return string.IsNullOrEmpty(PackageRoot) ? null : Path.Combine(PackageRoot, ConfigFileName);
        }

        public static void Load()
        {
            string path = ConfigPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path));
                    if (loaded != null) _settings = loaded;
                }
                else
                {
                    MigrateLegacyConfig(path);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 读取配置失败，使用默认设置（" + path + "）：" + e.Message);
            }
            SyncFromSettings();
        }

        /// <summary>1.4.6 and earlier kept config.json inside the mod folder. If one is still sitting there while the
        /// new per-user file does not exist yet, adopt its values, write them to the new path, then rename the old
        /// file to config.json.bak so it is obvious which file is live.</summary>
        private static void MigrateLegacyConfig(string newPath)
        {
            if (string.IsNullOrEmpty(PackageRoot)) return;
            string legacy = Path.Combine(PackageRoot, ConfigFileName);
            if (!File.Exists(legacy)) return;
            var loaded = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(legacy));
            if (loaded == null) return;
            _settings = loaded;
            SyncFromSettings();   // publish to the static fields FIRST, otherwise Save() would write the defaults back
            Save();
            try
            {
                File.Move(legacy, legacy + ".bak");
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 旧配置文件改名失败，可以手动删除：" + e.Message);
            }
            Log.Info("[rainylove] 旧版配置已迁移到 " + newPath);
        }

        public static void Save()
        {
            string path = ConfigPath;
            if (string.IsNullOrEmpty(path)) return;
            SyncToSettings();
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(_settings, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 写入配置失败（" + path + "）：" + e.Message);
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
            DeferStop = _settings.DeferStop;
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
            _settings.DeferStop = DeferStop;
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
            public bool DeferStop = false;
        }
    }
}
