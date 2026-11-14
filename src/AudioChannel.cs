using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Audio playback arbiter (singleton MonoBehaviour). All audio is ultimately played through it,
    /// because winmm PlaySound is a process-wide single channel: only one sound can play at a time,
    /// so 'who gets to play' must be decided by a single central authority.
    ///
    /// Two kinds of requests:
    ///   - Auto trigger (StartAuto): from RainyLove's 'last survivor' check or AIZO's 'low health' check.
    ///     Whether it actually plays is decided by AudioPriority (see ShouldTakeOver).
    ///   - Manual test (StartManual): from the config UI 'Test Play / Audio Playback Test' buttons.
    ///     An explicit user action: it always takes over the current sound immediately (highest priority).
    ///
    /// winmm PlaySound is asynchronous and gives no completion callback, so a timer (_endTime) resets state after natural end;
    /// otherwise an 'occupied' source would permanently block the other source's auto triggers.
    /// </summary>
    public enum AudioSourceKey { RainyLove, Aizo }

    public enum AudioPriority { RainyLoveFirst, AizoFirst, FirstCome }

    public class AudioChannel : MonoBehaviour
    {
        public static AudioChannel Instance;

        private AudioSourceKey? _current;   // Source currently audible
        private bool _currentManual;        // Current playback is a manual test (manual always wins; auto never interrupts)
        private float _endTime;             // Unscaled time of natural end; <=0 means no timer active
        private string _currentWav;
        private float _startTime;           // Unscaled start time of current playback (used to compute elapsed seconds)
        private float _duration;            // Total duration of the current audio (seconds)

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            // Reset on natural end: winmm has no callback, so a timer is the fallback.
            if (_current != null && _endTime > 0f && Time.unscaledTime >= _endTime)
            {
                SystemAudio.Stop(null);
                Log.Message("[rainylove] 音频自然结束，已复位状态（" + _current + "）。");
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

        /// <summary>Stop all playback immediately (used on mod unload).</summary>
        public void StopAll()
        {
            SystemAudio.Stop(null);
            _current = null;
            _currentManual = false;
            _endTime = 0f;
            _currentWav = null;
            _startTime = 0f;
            _duration = 0f;
        }

        /// <summary>Elapsed seconds for this source; returns -1 when this source is not playing.</summary>
        public float GetElapsedSeconds(AudioSourceKey src)
        {
            if (_current != src || _startTime <= 0f) return -1f;
            return Time.unscaledTime - _startTime;
        }

        /// <summary>Total duration of this source's current audio (seconds); returns 0 when unknown.</summary>
        public float GetDuration(AudioSourceKey src) => _current == src ? _duration : 0f;

        /// <summary>Auto-trigger request. Returns true when playback actually started (the caller should set its 'triggered this run' flag to avoid repeats).</summary>
        public bool StartAuto(AudioSourceKey src, string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath)) return false;

            if (_current == null)
            {
                Play(src, wavPath, false);
                return true;
            }

            // Manual playback in progress: manual always wins, auto never interrupts.
            if (_currentManual) return false;

            // Auto vs auto: decided by priority.
            if (!ShouldTakeOver(src)) return false;

            Play(src, wavPath, false);
            return true;
        }

        /// <summary>Caller's condition ended; stops this source's own auto playback (manual playback is unaffected).</summary>
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

        /// <summary>Config UI 'Test Play / Audio Playback Test': explicit user action, always takes over immediately.</summary>
        public void StartManual(AudioSourceKey src, string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath)) return;
            Play(src, wavPath, true);
        }

        /// <summary>Config UI 'Stop Playback / Audio Playback Stop': stops only this source's sound; does not affect the other source.</summary>
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
            // Note: do not PURGE other sources here, otherwise stopping A would also kill B's playing sound.
        }

        public bool IsPlaying(AudioSourceKey src) => _current == src;

        public bool IsManualPlaying(AudioSourceKey src) => _current == src && _currentManual;

        // Whether src's auto request may take over the currently playing auto sound.
        private bool ShouldTakeOver(AudioSourceKey src)
        {
            AudioPriority pri = ModConfig.AudioPriority;
            if (pri == AudioPriority.FirstCome) return false;          // First come, first served: an occupied channel is not handed over
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
            Log.Message("[rainylove] 开始播放（" + src + (manual ? " · 手动测试" : " · 自动触发") + "）。");
        }
    }
}
