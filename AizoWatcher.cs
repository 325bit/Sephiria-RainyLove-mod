using System.Collections;
using System.Reflection;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// AIZO 触发器：检测“自身玩家”的血量，当满足以下任一条件时播放 AIZO.wav：
    ///   · 当前血量百分比 &lt; 配置的百分比阈值（AizoHealthPercent，0~99）
    ///   · 当前血量固定值   &lt; 配置的固定值阈值（AizoHealthValue，0~9999）
    /// 两个阈值都为 0 时视为“关闭该条件”（永远不满足）。
    /// 播放同样经过 AudioChannel 仲裁；配置界面的“音频播放测试 / 音频播放停止”走手动通道。
    ///
    /// 血量字段名在不同构建里不确定，这里用反射按候选名探测 PlayerAvatar 的 current / max / 直接百分比字段，
    /// 缓存 MemberInfo 并打日志，便于确认是否取对。
    /// </summary>
    public class AizoWatcher : MonoBehaviour
    {
        private float _scanTimer;
        private string _wavPath;
        private bool _audioReady;
        private bool _reloading;
        private bool _playAfterLoad;
        private bool _manualAfterLoad;
        private bool _triggered;       // 本次“低血量”区间已触发过，避免重复触发

        public bool IsPlaying => AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo);
        public bool IsAudioReady => _audioReady;

        public void Initialize()
        {
            StartCoroutine(LoadClip());
        }

        private IEnumerator LoadClip()
        {
            if (_reloading) yield break;
            _reloading = true;
            _audioReady = false;
            _wavPath = null;

            string path = Plugin.ResolveAudioPath(Plugin.DefaultAizoWav, out bool exists);
            if (!exists)
            {
                Plugin.Log.LogWarning("[rainylove][AIZO] 找不到音频文件（需要 AIZO.wav）：" + path +
                                      "。请把文件放到 BepInEx\\plugins\\rainylove\\Asset\\ 下");
                _reloading = false;
                yield break;
            }

            // AIZO 仅音量 + 淡出（无淡入）。
            string wavPath;
            if (AudioLoader.TryPrepareWav(path, Plugin.AizoVolume.Value, 0f, Plugin.AizoFadeOutSeconds.Value, out wavPath))
            {
                _wavPath = wavPath;
                _audioReady = true;
                Plugin.Log.LogInfo("[rainylove][AIZO] 音频就绪（系统播放）：" + wavPath);
            }
            else
            {
                Plugin.Log.LogWarning("[rainylove][AIZO] 音频准备失败：需要 AIZO.wav（或同目录 mp3 + ffmpeg 兜底）：" + path);
            }
            _reloading = false;

            if (_playAfterLoad)
            {
                _playAfterLoad = false;
                bool manual = _manualAfterLoad;
                _manualAfterLoad = false;
                if (_audioReady)
                {
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _wavPath);
                    else if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _wavPath)) _triggered = true;
                }
            }
        }

        private void Update()
        {
            if (!Plugin.AizoEnabled.Value)
            {
                AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);   // 关闭只停自动，手动测试不受影响
                return;
            }

            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer >= Plugin.ScanInterval.Value)
            {
                _scanTimer = 0f;
                Evaluate();
            }
        }

        private void OnDestroy()
        {
            AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            AudioChannel.Instance?.StopManual(AudioSourceKey.Aizo);
        }

        private void Evaluate()
        {
            PlayerAvatar self = FindSelf();
            if (self == null) return;

            PlayerHealth.Ensure(self);
            if (!PlayerHealth.Read(self, out float cur, out float max, out float pct)) return;

            bool low = (pct * 100f) < Plugin.AizoHealthPercent.Value
                       || cur < Plugin.AizoHealthValue.Value;

            if (low)
            {
                if (!_triggered && AutoPlay()) _triggered = true;
            }
            else
            {
                // 血量恢复：复位触发标记，并停止正在播放的自动声音（手动测试不受影响）。
                _triggered = false;
                AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            }
        }

        private PlayerAvatar FindSelf()
        {
            PlayerAvatar[] avatars = UnityEngine.Object.FindObjectsOfType<PlayerAvatar>(true);
            foreach (PlayerAvatar a in avatars)
            {
                if (a.authority) return a;   // authority 标记本地玩家
            }
            return null;
        }

        // ===================== 供配置界面调用的播放控制 =====================

        /// <summary>配置界面的“音频播放测试”：手动，只看自身血量判定，直接播放（只受停止按钮控制）。</summary>
        public void TestPlay()
        {
            Plugin.Log.LogMessage("[rainylove][AIZO] 配置界面：音频播放测试。");
            if (!_audioReady || string.IsNullOrEmpty(_wavPath))
            {
                _playAfterLoad = true;
                _manualAfterLoad = true;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _wavPath);
        }

        /// <summary>配置界面的“音频播放停止”。</summary>
        public void StopFromUi()
        {
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo))
            {
                AudioChannel.Instance.StopManual(AudioSourceKey.Aizo);
                Plugin.Log.LogMessage("[rainylove][AIZO] 配置界面：已停止 AIZO 播放。");
            }
            else
            {
                Plugin.Log.LogMessage("[rainylove][AIZO] 配置界面：AIZO 当前没有音乐在播放。");
            }
        }

        /// <summary>音量 / 淡出被改动：作废已渲染的 WAV，下次播放按新参数重新渲染。只打断自动播放。</summary>
        public void OnParamChanged()
        {
            AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            _audioReady = false;
            _wavPath = null;
            Plugin.Log.LogMessage("[rainylove][AIZO] 音频参数已变更，下次播放按新参数重新渲染。");
        }

        // ===================== 内部实现 =====================

        private bool AutoPlay()
        {
            if (!_audioReady || string.IsNullOrEmpty(_wavPath))
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return false;
            }
            return AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _wavPath);
        }
    }

    /// <summary>
    /// 运行时探测 PlayerAvatar 的血量字段（current / max / 直接百分比）。
    /// 反射按候选名顺序查找 Field 或可读 Property，找到即缓存，避免每帧反射开销。
    /// </summary>
    internal static class PlayerHealth
    {
        private static bool _init;
        private static MemberInfo _cur, _max, _pct;

        private static readonly string[] CurrentNames =
            { "CurrentLife", "Life", "currentLife", "Hp", "HP", "hp", "currentHp", "CurrentHp", "Health", "CurrentHealth" };
        private static readonly string[] MaxNames =
            { "MaxLife", "maxLife", "MaxHp", "MaxHP", "maxHp", "MaxHealth", "maxHpValue" };
        private static readonly string[] PercentNames =
            { "HPPercent", "hpPercent", "HpPercent", "LifePercent", "hpRatio" };

        private static MemberInfo Find(System.Type t, string[] names)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var n in names)
            {
                FieldInfo f = t.GetField(n, flags);
                if (f != null) return f;
                PropertyInfo p = t.GetProperty(n, flags);
                if (p != null && p.CanRead) return p;
            }
            return null;
        }

        public static void Ensure(PlayerAvatar sample)
        {
            if (_init || sample == null) return;
            _init = true;
            System.Type t = sample.GetType();
            _cur = Find(t, CurrentNames);
            _max = Find(t, MaxNames);
            _pct = Find(t, PercentNames);
            Plugin.Log.LogInfo("[rainylove][AIZO] 血量字段探测：current="
                + (_cur != null ? _cur.Name + "(" + (_cur is FieldInfo ? "field" : "prop") + ")" : "未找到")
                + "，max=" + (_max != null ? _max.Name : "未找到")
                + "，percent=" + (_pct != null ? _pct.Name : "未找到"));
            if (_cur == null) Plugin.Log.LogWarning("[rainylove][AIZO] 未能探测到当前血量字段，AIZO 不会触发；请把日志里的 PlayerAvatar 血量字段名反馈给我。");
        }

        private static float GetVal(MemberInfo m, object obj)
        {
            if (m == null) return 0f;
            object v = (m is FieldInfo f) ? f.GetValue(obj) : ((PropertyInfo)m).GetValue(obj);
            if (v is float fl) return fl;
            if (v is int i) return (float)i;
            if (v is double d) return (float)d;
            if (v is long l) return (float)l;
            return 0f;
        }

        /// <summary>读取自身血量。返回 false 表示无法读取（字段未探测到）。</summary>
        public static bool Read(PlayerAvatar av, out float current, out float max, out float percent)
        {
            current = max = percent = 0f;
            if (!_init || av == null) return false;
            current = GetVal(_cur, av);
            max = GetVal(_max, av);
            if (max > 0f) percent = current / max;
            else if (_pct != null)
            {
                float p = GetVal(_pct, av);
                percent = p > 1f ? p / 100f : p;   // 兼容 0~1 与 0~100 两种表示
            }
            return true;
        }
    }
}
