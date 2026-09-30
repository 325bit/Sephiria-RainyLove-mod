using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace RainyLove
{
    [BepInPlugin("com.rainylove.lastsurvivor", "rainylove - Last Survivor", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal const string DefaultWavName = "rainylove.wav";

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> Volume;
        public static ConfigEntry<string> AudioFilePath;
        public static ConfigEntry<float> ScanInterval;
        public static ConfigEntry<bool> OnlyWhenSelfIsLast;
        public static ConfigEntry<bool> EnableTestKey;
        public static ConfigEntry<string> TestPlayKey;
        public static ConfigEntry<int> ResampleHz;

        internal static LastSurvivorWatcher Watcher;

        private void Awake()
        {
            Log = base.Logger;

            Enabled = Config.Bind("General", "Enabled", true, "总开关，关闭后不再检测与播放");
            Volume = Config.Bind("General", "Volume", 0.4f,
                new ConfigDescription("播放音量 0~1（运行时修改即时生效）", new AcceptableValueRange<float>(0f, 1f)));
            AudioFilePath = Config.Bind("General", "AudioFilePath", "",
                "音频文件（rainylove.wav）所在目录或文件路径。留空时按 BepInEx/plugins/rainylove/Asset、mod 源码目录的顺序查找");
            ScanInterval = Config.Bind("General", "ScanIntervalSec", 0.25f, "存活人数扫描间隔（秒）");
            OnlyWhenSelfIsLast = Config.Bind("General", "OnlyWhenSelfIsLast", false,
                "仅当自己就是最后存活的玩家时才播放（关闭时任何客户端检测到剩 1 人都会播放）");
            EnableTestKey = Config.Bind("General", "EnableTestKey", true, "启用 F7 强制停止播放 / F6 强制播放，方便自测");
            TestPlayKey = Config.Bind("General", "TestPlayKey", "f6",
                "强制播放测试键（键名对应 InputSystem.Key 枚举，不区分大小写）。主菜单按下即完整走一遍解码与播放链路，不需要开联机");
            ResampleHz = Config.Bind("Audio", "ResampleHz", 44100,
                "解码采样率。44100 音质最好但内存占用大（约 70MB）；22050 可减半，16000 更小");

            var go = new UnityEngine.GameObject("RainyLove.Watcher");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Watcher = go.AddComponent<LastSurvivorWatcher>();

            // 音频改用 Windows 系统音频（winmm PlaySound）直接播放，绕开本构建失效的
            // Unity AudioClip.Create/SetData 与 UnityWebRequest。
            Watcher.Initialize();

            // 运行时修改音量配置即时重准备音频（下次播放按新音量）
            Volume.SettingChanged += (s, e) => { Watcher?.ReloadAudio(); };

            Log.LogInfo("[rainylove] 已加载，开关=" + Enabled.Value + " 音量=" + Volume.Value);

            // 这里只做 API 层检查。Awake 阶段 Keyboard.current 通常还是 null，
            // 真正的功能性自检由 LastSurvivorWatcher 在运行时触发（见 InputBridge.TickSelfCheck）。
            Log.LogInfo("[rainylove] 输入系统可用=" + InputBridge.Available
                        + "，F7 键名映射=" + InputBridge.IsKeyMapped("f7"));
        }

        /// <summary>按配置解析音频文件（rainylove.wav）绝对路径，找不到时依次回退到默认位置。
        /// 直接认 WAV，不再依赖 mp3（mp3 仅为老安装的 ffmpeg 兜底源，非必需）。</summary>
        internal static string ResolveAudioPath(out bool fileExists)
        {
            fileExists = false;
            var configured = (AudioFilePath.Value ?? string.Empty).Trim();

            if (!string.IsNullOrEmpty(configured))
            {
                var direct = System.IO.File.Exists(configured)
                    ? configured
                    : (System.IO.Directory.Exists(configured)
                        ? System.IO.Path.Combine(configured, DefaultWavName)
                        : null);
                if (!string.IsNullOrEmpty(direct) && System.IO.File.Exists(direct))
                {
                    fileExists = true;
                    return direct;
                }
            }

            var candidates = new[]
            {
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "rainylove", "Asset", DefaultWavName),
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "rainylove", DefaultWavName),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultWavName),
            };

            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) { fileExists = true; return c; }
            }

            // 没找到：返回首选候选路径（用于报错提示），fileExists 保持 false
            return candidates[0];
        }
    }
}
