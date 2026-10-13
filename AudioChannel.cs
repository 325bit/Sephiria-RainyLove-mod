using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// 音频播放仲裁器（单例 MonoBehaviour）。所有音频最终都通过它播放，
    /// 因为 winmm 的 PlaySound 是进程级单通道——同一时刻只能有一声音频在响，
    /// 所以“谁该播”必须由一个统一的中枢来裁决。
    ///
    /// 两类请求：
    ///   · 自动触发（StartAuto）：来自 RainyLove 的“最后存活者”判定、或 AIZO 的“血量过低”判定。
    ///     是否真的播放由 AudioPriority 决定（见 ShouldTakeOver）。
    ///   · 手动测试（StartManual）：来自配置界面的“测试播放/音频播放测试”按钮。
    ///     属于用户显式操作，总是立即接管当前声音（优先级最高）。
    ///
    /// 由于 winmm PlaySound 异步播放、放完不回调，这里用计时器（_endTime）在声音自然放完后复位状态，
    /// 否则被“占用”的源会永久阻塞另一源的自动触发。
    /// </summary>
    public enum AudioSourceKey { RainyLove, Aizo }

    public enum AudioPriority { RainyLoveFirst, AizoFirst, FirstCome }

    public class AudioChannel : MonoBehaviour
    {
        public static AudioChannel Instance;

        private AudioSourceKey? _current;   // 当前真正在响的来源
        private bool _currentManual;        // 当前是手动测试播放（手动总优先，自动不打断）
        private float _endTime;             // 自然结束的 unscaled 时间；<=0 表示未启用计时
        private string _currentWav;
        private float _startTime;           // 本次播放开始的 unscaled 时间（用于算已播秒数）
        private float _duration;            // 本次播放的音频总时长（秒）

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            // 自然结束复位：winmm 不回调，用计时兜底。
            if (_current != null && _endTime > 0f && Time.unscaledTime >= _endTime)
            {
                SystemAudio.Stop(null);
                Plugin.Log.LogMessage("[rainylove] 音频自然结束，已复位状态（" + _current + "）。");
                _current = null;
                _currentManual = false;
                _endTime = 0f;
                _currentWav = null;
                _startTime = 0f;
                _duration = 0f;
            }
        }

        private void OnDestroy()
        {
            SystemAudio.Stop(null);
            _current = null;
            _currentManual = false;
            _endTime = 0f;
            _currentWav = null;
            _startTime = 0f;
            _duration = 0f;
        }

        /// <summary>本来源当前已播放的秒数；未在播放该来源时返回 -1。</summary>
        public float GetElapsedSeconds(AudioSourceKey src)
        {
            if (_current != src || _startTime <= 0f) return -1f;
            return Time.unscaledTime - _startTime;
        }

        /// <summary>本来源当前音频的总时长（秒）；未知时返回 0。</summary>
        public float GetDuration(AudioSourceKey src) => _current == src ? _duration : 0f;

        /// <summary>自动触发请求。返回 true 表示已经真正开始播放（调用方应置“本局已触发”标记，避免重复触发）。</summary>
        public bool StartAuto(AudioSourceKey src, string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath)) return false;

            if (_current == null)
            {
                Play(src, wavPath, false);
                return true;
            }

            // 手动播放进行中：手动永远优先，自动一律不打断。
            if (_currentManual) return false;

            // 自动 vs 自动：按优先级裁决。
            if (!ShouldTakeOver(src)) return false;

            Play(src, wavPath, false);
            return true;
        }

        /// <summary>调用方条件结束，停止自己正在播放的“自动”声音（手动播放不受此影响）。</summary>
        public void StopAuto(AudioSourceKey src)
        {
            if (_current == src && !_currentManual)
            {
                SystemAudio.Stop(null);
                _current = null;
                _endTime = 0f;
                _currentWav = null;
                _startTime = 0f;
                _duration = 0f;
            }
        }

        /// <summary>配置界面“测试播放 / 音频播放测试”：用户显式操作，总是立即接管。</summary>
        public void StartManual(AudioSourceKey src, string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath)) return;
            Play(src, wavPath, true);
        }

        /// <summary>配置界面“停止播放 / 音频播放停止”：只停止“本来源”的声音；不影响另一来源。</summary>
        public void StopManual(AudioSourceKey src)
        {
            if (_current == src)
            {
                SystemAudio.Stop(null);
                _current = null;
                _currentManual = false;
                _endTime = 0f;
                _currentWav = null;
                _startTime = 0f;
                _duration = 0f;
            }
            // 注意：不在这里 PURGE 其它来源——否则按 A 的停止键会误杀 B 正在放的声音。
        }

        public bool IsPlaying(AudioSourceKey src) => _current == src;

        public bool IsManualPlaying(AudioSourceKey src) => _current == src && _currentManual;

        // 是否允许 src 的自动请求抢占当前正在播放的自动声音。
        private bool ShouldTakeOver(AudioSourceKey src)
        {
            AudioPriority pri = Plugin.AudioPriority.Value;
            if (pri == AudioPriority.FirstCome) return false;          // 先来后到：已占用的不交让
            if (pri == AudioPriority.RainyLoveFirst) return src == AudioSourceKey.RainyLove;
            return src == AudioSourceKey.Aizo;                          // AizoFirst
        }

        private void Play(AudioSourceKey src, string wavPath, bool manual)
        {
            SystemAudio.Play(wavPath);
            _current = src;
            _currentManual = manual;
            _currentWav = wavPath;
            float dur = AudioLoader.GetWavDuration(wavPath);
            _duration = dur;
            _startTime = Time.unscaledTime;
            _endTime = dur > 0f ? Time.unscaledTime + dur : 0f;
            Plugin.Log.LogMessage("[rainylove] 开始播放（" + src + (manual ? " · 手动测试" : " · 自动触发") + "）。");
        }
    }
}
