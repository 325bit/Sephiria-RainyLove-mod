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
    /// 停止策略（节点顺延，不使用淡出）：
    /// AIZO 的时间轴被切成几个“节点”，血量回升需要停播时不立即停，而是向后顺延到最近的下一个节点：
    ///     p1 = 00:00（开始） / p2 = 00:43.5 / p3 = 01:38 / p4 = 02:20 / p5 = 曲末
    /// 例如播放到 01:00 时回满血 → 放到 01:38（p3）再停。
    /// 若在目标节点前 NodeWindowSec(10s) 内血量又跌破任一阈值 → 再顺延到下一个节点；
    /// 已经顺延到 p4(02:20) 还在窗口内触发 → 放完整首（p5）。
    ///
    /// 血量字段名在不同构建里不确定，这里用反射按候选名探测 PlayerAvatar 的 current / max / 直接百分比字段，
    /// 缓存 MemberInfo 并打日志，便于确认是否取对。
    /// </summary>
    public class AizoWatcher : MonoBehaviour
    {
        // ===== 时间轴节点（秒）：p2 00:43.5 / p3 01:38 / p4 02:20 / p5 曲末；窗口 10 秒 =====
        private readonly PlaybackNodeScheduler _sched = new PlaybackNodeScheduler(
            new[] { 43.5f, 98f, 140f },
            new[] { "p2 00:43.5", "p3 01:38", "p4 02:20", "p5 曲末" },
            10f);

        private float _scanTimer;
        private string _wavPath;
        private bool _audioReady;
        private bool _reloading;
        private bool _playAfterLoad;
        private bool _manualAfterLoad;
        private bool _triggered;       // 本次“低血量”区间已触发过，避免重复触发

        public bool IsPlaying => AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo);
        public bool IsAudioReady => _audioReady;

        /// <summary>渲染出来的临时 WAV 是否仍然可用（临时目录可能被清理、或被别处误删）。
        /// 用这个判断而不是只看 _audioReady，否则会出现“显示就绪、点了却没声音”。</summary>
        private bool IsUsable => _audioReady
                                 && !string.IsNullOrEmpty(_wavPath)
                                 && System.IO.File.Exists(_wavPath);

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

            // AIZO 只做音量缩放：淡入淡出已取消，停止改由“时间轴节点”控制。
            string wavPath;
            if (AudioLoader.TryPrepareWav(path, Plugin.AizoVolume.Value, 0f, 0f, out wavPath))
            {
                _wavPath = wavPath;
                _sched.Duration = AudioLoader.GetWavDuration(wavPath);
                _audioReady = true;
                Plugin.Log.LogInfo("[rainylove][AIZO] 音频就绪（系统播放）：" + wavPath
                                   + "，时长 " + PlaybackNodeScheduler.Fmt(AudioLoader.GetWavDuration(wavPath)));
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
                    _sched.Reset();
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _wavPath);
                    else if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _wavPath)) _triggered = true;
                }
            }
        }

        private void Update()
        {
            if (!Plugin.AizoEnabled.Value)
            {
                _sched.Reset();
                AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);   // 关闭只停自动，手动测试不受影响
                return;
            }

            // “到节点就停”每帧检查，避免 0.25s 扫描间隔带来的过冲。
            TickScheduledStop();

            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer >= Plugin.ScanInterval.Value)
            {
                _scanTimer = 0f;
                Evaluate();
            }
        }

        private void OnDestroy()
        {
            _sched.Reset();
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

            AudioChannel ch = AudioChannel.Instance;
            bool autoPlaying = ch != null && ch.IsPlaying(AudioSourceKey.Aizo)
                               && !ch.IsManualPlaying(AudioSourceKey.Aizo);

            if (!autoPlaying)
            {
                // 没在自动播放：走“要不要起播”的常规判定，并清空节点计划。
                _sched.Reset();
                if (low)
                {
                    if (!_triggered && AutoPlay()) _triggered = true;
                }
                else
                {
                    _triggered = false;
                }
                return;
            }

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.Aizo);
            if (elapsed < 0f) { _sched.Reset(); return; }

            string msg = _sched.OnScan(low, elapsed);
            if (msg != null) Plugin.Log.LogMessage("[rainylove][AIZO] " + msg);
        }

        /// <summary>每帧检查：已播到计划的节点就停（无淡出，直接停）。</summary>
        private void TickScheduledStop()
        {
            if (!_sched.HasPlan) return;

            AudioChannel ch = AudioChannel.Instance;
            if (ch == null) return;
            if (!ch.IsPlaying(AudioSourceKey.Aizo) || ch.IsManualPlaying(AudioSourceKey.Aizo)) return;

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.Aizo);
            if (elapsed < 0f) { _sched.Reset(); return; }

            int verdict = _sched.CheckStop(elapsed, out string msg);
            if (verdict == PlaybackNodeScheduler.NoStop) return;

            if (verdict == PlaybackNodeScheduler.StopNow)
            {
                ch.StopAuto(AudioSourceKey.Aizo);
                _sched.Reset();
                _triggered = false;
            }
            Plugin.Log.LogMessage("[rainylove][AIZO] " + msg + "。");
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
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = true;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            _sched.Reset();
            AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _wavPath);
        }

        /// <summary>配置界面的“音频播放停止”。</summary>
        public void StopFromUi()
        {
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo))
            {
                _sched.Reset();
                AudioChannel.Instance.StopManual(AudioSourceKey.Aizo);
                Plugin.Log.LogMessage("[rainylove][AIZO] 配置界面：已停止 AIZO 播放。");
            }
            else
            {
                Plugin.Log.LogMessage("[rainylove][AIZO] 配置界面：AIZO 当前没有音乐在播放。");
            }
        }

        /// <summary>音量被改动：作废已渲染的 WAV，下次播放按新参数重新渲染。只打断自动播放。</summary>
        public void OnParamChanged()
        {
            _sched.Reset();
            AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            _audioReady = false;
            _wavPath = null;
            Plugin.Log.LogMessage("[rainylove][AIZO] 音频参数已变更，下次播放按新参数重新渲染。");
        }

        // ===================== 内部实现 =====================

        private bool AutoPlay()
        {
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return false;
            }
            bool ok = AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _wavPath);
            if (ok) _sched.Reset();     // 新的一次播放，节点计划从头开始
            return ok;
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
