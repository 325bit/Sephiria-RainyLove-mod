using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// 'Deferred stop at timeline node' scheduler (shared by AIZO and RainyLove).
    ///
    /// Background: when the trigger condition (low health / last survivor left) becomes false, the music should not be cut off immediately;
    /// instead it is deferred to the next 'node' on the audio timeline; fade-in/out was removed.
    ///
    /// Rules:
    ///   1. Condition becomes false for the first time -> target = first node strictly after the current playback position.
    ///   2. Condition becomes true again within windowSec before the target node -> defer the target to the next node,
    ///      and mark it as 'locked': a locked target can no longer be cancelled (otherwise the next frame
    ///      would cancel it for being 'outside the new target's window', degenerating into never stopping).
    ///   3. If it still triggers after deferring to the last node (end of track), play the whole track and schedule no stop.
    ///   4. Condition becomes true outside the window -> cancel this stop plan, keep playing normally, and reschedule when it next becomes false.
    ///
    /// Node grid: the listeners place nodes on verse/chorus phrase boundaries, roughly every 6-15 seconds.
    /// The window must stay well below the smallest gap: with a window as wide as the gaps, every re-trigger would
    /// land inside a window, each one would defer a whole gap, and the stop would slide to the end of the track.
    /// </summary>
    public class PlaybackNodeScheduler
    {
        public const int NoStop = 0;
        public const int StopNow = 1;
        public const int StopCancelled = 2;

        private readonly float[] _checkpoints;   // Intermediate nodes (seconds), excluding the end of track
        private readonly string[] _labels;       // Node labels, one more than _checkpoints (last entry is 'end of track')
        private readonly float _windowSec;

        private int _stopNode = -1;     // Index of the scheduled stop node; -1 = no stop scheduled
        private bool _locked;           // Target produced by an in-window deferral; must not be cancelled
        private bool _playToEnd;        // Decided to play the whole track
        private bool _lastCondition;    // Condition result from the most recent scan
        private float _duration;        // Total audio duration (seconds)

        /// <param name="checkpoints">Intermediate nodes in seconds, ascending, excluding the end of track.</param>
        /// <param name="labels">Node labels, length = checkpoints.Length + 1, last entry is the end of track.</param>
        /// <param name="windowSec">How many seconds before the target node a re-trigger defers the stop.</param>
        public PlaybackNodeScheduler(float[] checkpoints, string[] labels, float windowSec)
        {
            _checkpoints = checkpoints;
            _labels = labels;
            _windowSec = windowSec;
        }

        /// <summary>Whether a 'stop at node' is scheduled.</summary>
        public bool HasPlan => _stopNode >= 0;

        /// <summary>Set the total audio duration (used by the end-of-track node).</summary>
        public float Duration { set { _duration = value; } }

        /// <summary>Clear the node schedule for one playback.</summary>
        public void Reset()
        {
            _stopNode = -1;
            _locked = false;
            _playToEnd = false;
            _lastCondition = false;
        }

        private int EndNode => _checkpoints.Length;      // Virtual node index: end of track

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

        /// <summary>Nearest node strictly after elapsed; returns EndNode (end of track) if none exists.</summary>
        private int NextNodeIndex(float elapsed)
        {
            for (int i = 0; i < _checkpoints.Length; i++)
            {
                float c = _checkpoints[i];
                if (c > elapsed + 0.01f && (_duration <= 0f || c < _duration - 1f)) return i;
            }
            return EndNode;
        }

        /// <summary>Condition scan every 0.25s. Returns the log text to emit, or null when nothing changed.</summary>
        public string OnScan(bool conditionMet, float elapsed)
        {
            _lastCondition = conditionMet;

            if (conditionMet)
            {
                if (_stopNode < 0) return null;                  // Already planning to play to the end

                float target = NodeTime(_stopNode);
                if (elapsed >= target - _windowSec)
                {
                    int from = _stopNode;
                    int next = _stopNode + 1;
                    if (next >= EndNode) { _playToEnd = true; _stopNode = -1; }
                    else _stopNode = next;
                    _locked = true;
                    return Fmt(elapsed) + " 条件又成立（换段点 " + NodeLabel(from) + " 就在眼前）→ 这段不算唱完，改在 "
                        + (_playToEnd ? NodeLabel(EndNode) + " 放完再停" : NodeLabel(_stopNode) + " 停");
                }

                if (!_locked)
                {
                    string cancelled = NodeLabel(_stopNode);
                    _stopNode = -1;
                    return Fmt(elapsed) + " 条件又成立（离换段点 " + cancelled + " 还早）→ 不停了，继续播";
                }
                return null;
            }

            if (_playToEnd || _stopNode >= 0) return null;       // Already playing to the end / target already scheduled

            int node = NextNodeIndex(elapsed);
            if (node >= EndNode)
            {
                _playToEnd = true;
                return Fmt(elapsed) + " 条件解除，后面没有换段点了 → 把这首放完";
            }
            _stopNode = node;
            _locked = false;
            return Fmt(elapsed) + " 条件解除 → 先把当前这段唱完，在 " + NodeLabel(node) + " 停";
        }

        /// <summary>Called every frame (to avoid overshoot from the 0.25s scan).
        /// Returns NoStop / StopNow / StopCancelled; the latter two carry a log message.</summary>
        public int CheckStop(float elapsed, out string message)
        {
            message = null;
            if (_stopNode < 0) return NoStop;
            if (elapsed < NodeTime(_stopNode)) return NoStop;

            if (_lastCondition)
            {
                // Condition is met again exactly at the node: do not hard-stop, otherwise it would immediately replay from the start after reset.
                message = "刚到换段点 " + NodeLabel(_stopNode) + " 条件又成立 → 这次不停，继续播";
                _stopNode = -1;
                _locked = false;
                return StopCancelled;
            }

            message = "到换段点 " + NodeLabel(_stopNode) + "，当前这段已经唱完 → 停止播放";
            return StopNow;
        }

        public static string Fmt(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = Mathf.FloorToInt(seconds);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        /// <summary>mm:ss plus a tenth of a second only when there is one (00:43.5 / 01:38).</summary>
        public static string FmtPrecise(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int whole = Mathf.FloorToInt(seconds);
            // Plain cast instead of Mathf.RoundToInt: the seconds here are never negative, and it keeps the
            // label builder usable from the stand-alone test harnesses, which stub only part of Mathf.
            int tenth = (int)((seconds - whole) * 10f + 0.5f);
            if (tenth >= 10) { whole++; tenth = 0; }
            return Fmt(whole) + (tenth > 0 ? "." + tenth : string.Empty);
        }

        /// <summary>Builds the label array for a node time list: p2..pN for the checkpoints and 'pN+1 曲末'
        /// for the end of track, matching the p1 = 00:00 (start) convention used in the logs.
        /// Generated instead of hand-written so a node time and its label can never drift apart.</summary>
        public static string[] BuildLabels(float[] checkpoints)
        {
            var labels = new string[checkpoints.Length + 1];
            for (int i = 0; i < checkpoints.Length; i++)
                labels[i] = "p" + (i + 2) + " " + FmtPrecise(checkpoints[i]);
            labels[checkpoints.Length] = "p" + (checkpoints.Length + 2) + " 曲末";
            return labels;
        }
    }
}
