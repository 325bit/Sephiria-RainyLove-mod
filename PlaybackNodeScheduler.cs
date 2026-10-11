using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// “节点顺延停止”调度器（AIZO 与 RainyLove 共用）。
    ///
    /// 背景：触发条件（血量过低 / 只剩最后一人）变假时不该立刻掐掉音乐，
    /// 而是向后顺延到音频时间轴上最近的下一个“节点”再停；淡入淡出已取消。
    ///
    /// 规则：
    ///   1. 条件第一次变假 → 目标 = 严格晚于当前播放位置的第一个节点。
    ///   2. 在目标节点前 windowSec 秒内条件又变真 → 目标顺延到下一个节点，
    ///      并标记为“锁定”——锁定的目标不允许再被取消（否则下一帧就会因为
    ///      “不在新目标的窗口内”而被取消，退化成永远不停）。
    ///   3. 已经顺延到最后一个节点（曲末）还被触发 → 放完整首，不再安排停止。
    ///   4. 条件在窗口之外变真 → 取消本次停止计划，继续正常播放，等下次变假再重排。
    /// </summary>
    public class PlaybackNodeScheduler
    {
        public const int NoStop = 0;
        public const int StopNow = 1;
        public const int StopCancelled = 2;

        private readonly float[] _checkpoints;   // 中间节点（秒），不含曲末
        private readonly string[] _labels;       // 节点名，比 _checkpoints 多一项（最后一项是“曲末”）
        private readonly float _windowSec;

        private int _stopNode = -1;     // 计划停止的节点索引；-1 = 暂无停止计划
        private bool _locked;           // 目标由“窗口内顺延”产生，不允许被取消
        private bool _playToEnd;        // 已决定放完整首
        private bool _lastCondition;    // 最近一次扫描到的条件结果
        private float _duration;        // 音频总时长（秒）

        /// <param name="checkpoints">中间节点（秒，递增），不含曲末。</param>
        /// <param name="labels">节点名，长度 = checkpoints.Length + 1，最后一项为曲末。</param>
        /// <param name="windowSec">距目标节点前多少秒内条件再次满足就顺延。</param>
        public PlaybackNodeScheduler(float[] checkpoints, string[] labels, float windowSec)
        {
            _checkpoints = checkpoints;
            _labels = labels;
            _windowSec = windowSec;
        }

        /// <summary>是否已排好“到某个节点就停”。</summary>
        public bool HasPlan => _stopNode >= 0;

        /// <summary>设置音频总时长（曲末节点用它）。</summary>
        public float Duration { set { _duration = value; } }

        /// <summary>清空一次播放的节点计划。</summary>
        public void Reset()
        {
            _stopNode = -1;
            _locked = false;
            _playToEnd = false;
            _lastCondition = false;
        }

        private int EndNode => _checkpoints.Length;      // 虚拟节点索引：曲末

        public float NodeTime(int index)
        {
            if (index < 0) return -1f;
            if (index < _checkpoints.Length) return _checkpoints[index];
            return _duration > 0f ? _duration : _checkpoints[_checkpoints.Length - 1];
        }

        public string NodeLabel(int index)
        {
            if (index < 0) return string.Empty;
            if (index < _labels.Length) return _labels[index];
            return _labels[_labels.Length - 1];
        }

        /// <summary>严格晚于 elapsed 的最近节点；没有就返回 EndNode（曲末）。</summary>
        private int NextNodeIndex(float elapsed)
        {
            for (int i = 0; i < _checkpoints.Length; i++)
            {
                float c = _checkpoints[i];
                if (c > elapsed + 0.01f && (_duration <= 0f || c < _duration - 1f)) return i;
            }
            return EndNode;
        }

        /// <summary>每 0.25s 的条件扫描。返回要打的日志文本；无变化返回 null。</summary>
        public string OnScan(bool conditionMet, float elapsed)
        {
            _lastCondition = conditionMet;

            if (conditionMet)
            {
                if (_stopNode < 0) return null;                  // 本来就打算放到底

                float target = NodeTime(_stopNode);
                if (elapsed >= target - _windowSec)
                {
                    int from = _stopNode;
                    int next = _stopNode + 1;
                    if (next >= EndNode) { _playToEnd = true; _stopNode = -1; }
                    else _stopNode = next;
                    _locked = true;
                    return Fmt(elapsed) + " 条件再次满足（" + NodeLabel(from) + " 前 "
                        + _windowSec.ToString("0") + " 秒内）→ 顺延到 "
                        + (_playToEnd ? NodeLabel(EndNode) + "，放完整首" : NodeLabel(_stopNode) + " 结束播放");
                }

                if (!_locked)
                {
                    string cancelled = NodeLabel(_stopNode);
                    _stopNode = -1;
                    return Fmt(elapsed) + " 条件再次满足（离 " + cancelled + " 还远）→ 取消停止计划，继续播放";
                }
                return null;
            }

            if (_playToEnd || _stopNode >= 0) return null;       // 已在放到底 / 目标已排好

            int node = NextNodeIndex(elapsed);
            if (node >= EndNode)
            {
                _playToEnd = true;
                return Fmt(elapsed) + " 条件解除，已过最后一个节点 → 放完整首";
            }
            _stopNode = node;
            _locked = false;
            return Fmt(elapsed) + " 条件解除 → 顺延至 " + NodeLabel(node) + " 结束播放";
        }

        /// <summary>每帧调用（避免 0.25s 扫描过冲）。
        /// 返回 NoStop / StopNow / StopCancelled；后两者附带一条日志文本。</summary>
        public int CheckStop(float elapsed, out string message)
        {
            message = null;
            if (_stopNode < 0) return NoStop;
            if (elapsed < NodeTime(_stopNode)) return NoStop;

            if (_lastCondition)
            {
                // 刚好到节点时条件又满足了：不硬停，否则复位后会立刻从头重播。
                message = "到达 " + NodeLabel(_stopNode) + " 时条件再次满足，取消本次停止，继续播放";
                _stopNode = -1;
                _locked = false;
                return StopCancelled;
            }

            message = "已在节点 " + NodeLabel(_stopNode) + "（" + Fmt(NodeTime(_stopNode)) + "）结束播放";
            return StopNow;
        }

        public static string Fmt(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = Mathf.FloorToInt(seconds);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }
    }
}
