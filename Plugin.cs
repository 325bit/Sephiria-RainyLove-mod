using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace RainyLove
{
    [BepInPlugin("com.rainylove.lastsurvivor", "rainylove - Last Survivor", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal const string DefaultRainyWav = "rainylove.wav";
        internal const string DefaultAizoWav = "AIZO.wav";

        // ===== RainyLove（沿用旧 cfg 的 General / Audio 小节，保留用户已有设置）=====
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> Volume;
        public static ConfigEntry<string> AudioFilePath;
        public static ConfigEntry<float> ScanInterval;
        public static ConfigEntry<bool> OnlyWhenSelfIsLast;
        public static ConfigEntry<float> FadeOutSeconds;   // RainyLove 只保留淡出（无淡入）
        public static ConfigEntry<int> ResampleHz;

        // ===== AIZO =====
        public static ConfigEntry<bool> AizoEnabled;
        public static ConfigEntry<float> AizoVolume;
        public static ConfigEntry<string> AizoAudioFilePath;
        public static ConfigEntry<float> AizoFadeOutSeconds;
        public static ConfigEntry<float> AizoHealthPercent;  // 0~99（%）
        public static ConfigEntry<float> AizoHealthValue;    // 0~9999（固定值）

        // ===== 主设置 =====
        public static ConfigEntry<AudioPriority> AudioPriority;
        public static ConfigEntry<string> MenuKey;

        internal static LastSurvivorWatcher Watcher;
        internal static AizoWatcher Aizo;
        internal static Plugin Instance;

        private void Awake()
        {
            Log = base.Logger;
            Instance = this;

            // ---- RainyLove ----
            Enabled = Config.Bind("General", "Enabled", true, "RainyLove 总开关（也可在主设置里切换）");
            Volume = Config.Bind("General", "Volume", 0.4f,
                new ConfigDescription("播放音量 0~1（在 F7 配置界面里拖动滑块即时生效）", new AcceptableValueRange<float>(0f, 1f)));
            AudioFilePath = Config.Bind("General", "AudioFilePath", "",
                "音频文件（rainylove.wav）所在目录或文件路径。留空时按 BepInEx/plugins/rainylove/Asset、mod 源码目录的顺序查找");
            ScanInterval = Config.Bind("General", "ScanIntervalSec", 0.25f, "存活人数扫描间隔（秒）");
            OnlyWhenSelfIsLast = Config.Bind("General", "OnlyWhenSelfIsLast", false,
                "仅当自己就是最后存活的玩家时才播放（关闭时任何客户端检测到剩 1 人都会播放）");
            FadeOutSeconds = Config.Bind("Audio", "FadeOutSeconds", 2f,
                new ConfigDescription("RainyLove 结尾淡出时长（秒），0 表示不淡出", new AcceptableValueRange<float>(0f, 10f)));
            ResampleHz = Config.Bind("Audio", "ResampleHz", 44100,
                "解码采样率（仅 ffmpeg 兜底用）。44100 音质最好但内存占用大；22050 可减半");

            // ---- AIZO ----
            AizoEnabled = Config.Bind("AIZO", "Enabled", true, "AIZO 总开关（也可在主设置里切换）");
            AizoVolume = Config.Bind("AIZO", "Volume", 0.4f,
                new ConfigDescription("AIZO 播放音量 0~1", new AcceptableValueRange<float>(0f, 1f)));
            AizoAudioFilePath = Config.Bind("AIZO", "AudioFilePath", "",
                "音频文件（AIZO.wav）所在目录或文件路径。留空时按默认位置查找");
            AizoFadeOutSeconds = Config.Bind("AIZO", "FadeOutSeconds", 2f,
                new ConfigDescription("AIZO 结尾淡出时长（秒），0 表示不淡出", new AcceptableValueRange<float>(0f, 10f)));
            AizoHealthPercent = Config.Bind("AIZO", "HealthPercent", 30f,
                new ConfigDescription("当自身血量百分比低于该值(0~99%)时播放", new AcceptableValueRange<float>(0f, 99f)));
            AizoHealthValue = Config.Bind("AIZO", "HealthValue", 30f,
                new ConfigDescription("当自身血量低于该固定值(0~9999)时播放", new AcceptableValueRange<float>(0f, 9999f)));

            // ---- 主设置 ----
            AudioPriority = Config.Bind("主设置", "AudioPriority", RainyLove.AudioPriority.RainyLoveFirst,
                "两个音频同时想播放时的优先级：RainyLove优先 / AIZO优先 / 先来后到");
            MenuKey = Config.Bind("Interface", "MenuKey", "f7",
                "呼出 / 关闭配置界面的按键（键名对应 InputSystem.Key 枚举，不区分大小写）");

            var go = new UnityEngine.GameObject("RainyLove.Root");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Watcher = go.AddComponent<LastSurvivorWatcher>();
            Aizo = go.AddComponent<AizoWatcher>();
            go.AddComponent<AudioChannel>();
            go.AddComponent<ConfigUi>();

            Watcher.Initialize();
            Aizo.Initialize();

            // 音量 / 淡出被改动：作废已渲染的 WAV，下次播放按新参数重新渲染（高开销延迟化）。
            Volume.SettingChanged += (s, e) => Watcher.OnParamChanged();
            FadeOutSeconds.SettingChanged += (s, e) => Watcher.OnParamChanged();
            AizoVolume.SettingChanged += (s, e) => Aizo.OnParamChanged();
            AizoFadeOutSeconds.SettingChanged += (s, e) => Aizo.OnParamChanged();

            Log.LogInfo("[rainylove] v1.2.0 已加载，RainyLove=" + Enabled.Value + " AIZO=" + AizoEnabled.Value
                        + " 优先级=" + AudioPriority.Value);
            Log.LogInfo("[rainylove] " + MenuKey.Value.ToUpperInvariant()
                        + " 呼出配置界面；输入系统可用=" + InputBridge.Available
                        + "，按键映射=" + InputBridge.IsKeyMapped(MenuKey.Value));
        }

        /// <summary>按配置解析音频文件绝对路径；fileName 指定 rainylove.wav / AIZO.wav。
        /// 找不到时依次回退到默认位置。</summary>
        internal static string ResolveAudioPath(string fileName, out bool fileExists)
        {
            fileExists = false;
            var configuredKey = fileName == DefaultAizoWav ? AizoAudioFilePath.Value : AudioFilePath.Value;
            var configured = (configuredKey ?? string.Empty).Trim();

            if (!string.IsNullOrEmpty(configured))
            {
                var direct = System.IO.File.Exists(configured)
                    ? configured
                    : (System.IO.Directory.Exists(configured)
                        ? System.IO.Path.Combine(configured, fileName)
                        : null);
                if (!string.IsNullOrEmpty(direct) && System.IO.File.Exists(direct))
                {
                    fileExists = true;
                    return direct;
                }
            }

            var candidates = new[]
            {
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "rainylove", "Asset", fileName),
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "rainylove", fileName),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
            };

            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) { fileExists = true; return c; }
            }

            return candidates[0];
        }
    }
}
