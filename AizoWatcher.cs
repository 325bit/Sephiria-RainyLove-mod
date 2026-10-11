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
        // ===== 时间轴节点（秒）=====
        // 索引 0 = p2 00:43.5，1 = p3 01:38，2 = p4 02:20；索引 EndNode(3) = p5 曲末
        private static readonly float[] Checkpoints = { 43.5f, 98f, 140f };
        private static readonly string[] NodeLabels = { "p2 00:43.5", "p3 01:38", "p4 02:20", "p5 曲末" };
        private const int EndNode = 3;              // 虚拟节点：曲末（放完整首）
        private const float NodeWindowSec = 10f;    // 距目标节点前多少秒内再次满足阈值→顺延

        private float _scanTimer;
        private string _wavPath;
        private bool _audioReady;
        private bool _reloading;
        private bool _playAfterLoad;
        private bool _manualAfterLoad;
        private bool _triggered;       // 本次“低血量”区间已触发过，避免重复触发
        private float _duration;       // 当前渲染出来的 WAV 时长（秒）

        // ===== 节点顺延状态 =====
        private int _stopNode = -1;    // 计划停止的节点索引；-1 = 暂无停止计划
        private bool _targetLocked;    // 目标由“窗口内顺延”产生，不允许再被取消
        private bool _playToEnd;       // 已决定放完整首，不再安排任何停止
        private bool _lastLow;         // 最近一次扫描到的“血量低于阈值”结果

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

            // AIZO 只做音量缩放：淡入淡出已取消，停止改由“时间轴节点”控制。
            string wavPath;
            if (AudioLoader.TryPrepareWav(path, Plugin.AizoVolume.Value, 0f, 0f, out wavPath))
            {
                _wavPath = wavPath;
                _duration = AudioLoader.GetWavDuration(wavPath);
                _audioReady = true;
                Plugin.Log.LogInfo("[rainylove][AIZO] 音频就绪（系统播放）：" + wavPath
                                   + "，时长 " + Fmt(_duration));
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
                    ResetSchedule();
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _wavPath);
                    else if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _wavPath)) _triggered = true;
                }
            }
        }

        private void Update()
        {
            if (!Plugin.AizoEnabled.Value)
            {
                ResetSchedule();
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
            ResetSchedule();
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
            _lastLow = low;

            AudioChannel ch = AudioChannel.Instance;
            bool autoPlaying = ch != null && ch.IsPlaying(AudioSourceKey.Aizo)
                               && !ch.IsManualPlaying(AudioSourceKey.Aizo);

            if (!autoPlaying)
            {
                // 没在自动播放：走“要不要起播”的常规判定，并清空节点计划。
                ResetSchedule();
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
            if (elapsed < 0f) { ResetSchedule(); return; }
            if (_duration <= 0f) _duration = ch.GetDuration(AudioSourceKey.Aizo);

            if (low)
            {
                // 播放途中血量又跌破阈值
                if (_stopNode < 0) return;                     // 本来就打算放到底，不用管

                float target = NodeTime(_stopNode);
                if (elapsed >= target - NodeWindowSec)
                {
                    // 落在目标节点前 10 秒窗口内 → 顺延到下一个节点
                    int from = _stopNode;
                    AdvanceNode();
                    _targetLocked = true;
                    Plugin.Log.LogMessage("[rainylove][AIZO] " + Fmt(elapsed) + " 血量再次低于阈值，"
                        + "（" + NodeLabels[from] + " 前 10 秒内）顺延到 "
                        + (_playToEnd ? "曲末（放完整首）" : NodeLabels[_stopNode]) + "。");
                }
                else if (!_targetLocked)
                {
                    // 离目标节点还远就跌破阈值 → 取消这次停止计划，继续正常播放
                    _stopNode = -1;
                    Plugin.Log.LogMessage("[rainylove][AIZO] " + Fmt(elapsed) + " 血量再次低于阈值，"
                        + "取消原定于 " + NodeLabels[IndexOfElapsed(target)] + " 的停止，继续播放。");
                }
            }
            else
            {
                // 血量回升：本来要停，但改为顺延到最近的下一个节点再停
                if (_playToEnd || _stopNode >= 0) return;

                int node = NextNodeIndex(elapsed);
                if (node >= EndNode)
                {
                    _playToEnd = true;
                    Plugin.Log.LogMessage("[rainylove][AIZO] " + Fmt(elapsed) + " 血量回升，"
                        + "已过最后一个节点（02:20），放完整首音频。");
                }
                else
                {
                    _stopNode = node;
                    _targetLocked = false;
                    Plugin.Log.LogMessage("[rainylove][AIZO] " + Fmt(elapsed) + " 血量回升，"
                        + "顺延至 " + NodeLabels[node] + " 结束播放。");
                }
            }
        }

        /// <summary>每帧检查：已播到计划的节点就停（无淡出，直接停）。</summary>
        private void TickScheduledStop()
        {
            if (_stopNode < 0) return;

            AudioChannel ch = AudioChannel.Instance;
            if (ch == null) return;
            if (!ch.IsPlaying(AudioSourceKey.Aizo) || ch.IsManualPlaying(AudioSourceKey.Aizo)) return;

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.Aizo);
            if (elapsed < 0f) { ResetSchedule(); return; }
            if (elapsed < NodeTime(_stopNode)) return;

            if (_lastLow)
            {
                // 刚好到节点时血量又低了：不硬停，继续放着（否则会立刻从头重播）
                Plugin.Log.LogMessage("[rainylove][AIZO] 到达 " + NodeLabels[_stopNode]
                    + " 时血量再次低于阈值，取消本次停止，继续播放。");
                _stopNode = -1;
                _targetLocked = false;
                return;
            }

            ch.StopAuto(AudioSourceKey.Aizo);
            Plugin.Log.LogMessage("[rainylove][AIZO] 已在节点 " + NodeLabels[_stopNode]
                + "（" + Fmt(NodeTime(_stopNode)) + "）结束播放。");
            ResetSchedule();
            _triggered = false;
        }

        // ===================== 节点时间轴 =====================

        /// <summary>节点索引对应的播放位置（秒）；EndNode 为曲末。</summary>
        private float NodeTime(int index)
        {
            if (index < 0) return -1f;
            if (index < Checkpoints.Length) return Checkpoints[index];
            return _duration > 0f ? _duration : Checkpoints[Checkpoints.Length - 1];
        }

        /// <summary>严格晚于 elapsed 的最近节点；没有就返回 EndNode（放完整首）。</summary>
        private int NextNodeIndex(float elapsed)
        {
            for (int i = 0; i < Checkpoints.Length; i++)
            {
                float c = Checkpoints[i];
                if (c > elapsed + 0.01f && (_duration <= 0f || c < _duration - 1f)) return i;
            }
            return EndNode;
        }

        private void AdvanceNode()
        {
            int next = _stopNode + 1;
            if (next >= EndNode) { _playToEnd = true; _stopNode = -1; }
            else _stopNode = next;
        }

        private int IndexOfElapsed(float t)
        {
            for (int i = 0; i < Checkpoints.Length; i++)
                if (Mathf.Approximately(Checkpoints[i], t)) return i;
            return Checkpoints.Length - 1;
        }

        /// <summary>清空一次播放的节点顺延计划。</summary>
        private void ResetSchedule()
        {
            _stopNode = -1;
            _targetLocked = false;
            _playToEnd = false;
            _lastLow = false;
        }

        private static string Fmt(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = Mathf.FloorToInt(seconds);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
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
            ResetSchedule();
            AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _wavPath);
        }

        /// <summary>配置界面的“音频播放停止”。</summary>
        public void StopFromUi()
        {
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo))
            {
                ResetSchedule();
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
            ResetSchedule();
            AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            _audioReady = false;
            _wavPath = null;
            _duration = 0f;
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
            bool ok = AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _wavPath);
            if (ok) ResetSchedule();     // 新的一次播放，节点计划从头开始
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
