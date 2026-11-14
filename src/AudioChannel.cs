using UnityEngine;
using UnityEngine.SceneManagement;

namespace RainyLove
{
    /// <summary>
    /// Audio playback arbiter (singleton MonoBehaviour). All audio is ultimately played through it,
    /// and only one track may ever sound at a time - the mod's whole point is a single song for a single
    /// moment, so 'who gets to play' must be decided by a single central authority. SystemAudio enforces the
    /// same rule one level down by closing the previous MCI device before opening the next one.
    ///
    /// Two kinds of requests:
    ///   - Auto trigger (StartAuto): from RainyLove's 'last survivor' check or AIZO's 'low health' check.
    ///     Whether it actually plays is decided by AudioPriority (see ShouldTakeOver).
    ///   - Manual test (StartManual): from the config UI 'Test Play / Audio Playback Test' buttons.
    ///     An explicit user action: it always takes over the current sound immediately (highest priority).
    ///
    /// MCI gives no completion callback either, so a timer (_endTime) resets state after natural end;
    /// otherwise an 'occupied' source would permanently block the other source's auto triggers.
    ///
    /// This object is DontDestroyOnLoad and the MCI device is a Windows-level device, so nothing tears the
    /// sound down when Unity unloads a scene. The scene hook below is therefore the only thing that stops a
    /// track from following the player back to the title screen or into the lobby.
    /// </summary>
    public enum AudioSourceKey { RainyLove, Aizo }

    public enum AudioPriority { RainyLoveFirst, AizoFirst, FirstCome }

    public class AudioChannel : MonoBehaviour
    {
        public static AudioChannel Instance;

        private AudioSourceKey? _current;   // Source currently audible
        private bool _currentManual;        // Current playback is a manual test (manual always wins; auto never interrupts)
        private float _endTime;             // Unscaled time of natural end; <=0 means no timer active
        private string _currentWav;         // Path of the audible file (logged when stopping, so the log names the track)
        private float _startTime;           // Unscaled start time of current playback (used to compute elapsed seconds)
        private float _duration;            // Total duration of the current audio (seconds)
        private bool _loop;                 // Device-level 'repeat' is active, so the track never reaches its end
        private float _posCache;            // Cached in-track position while looping (MCI round-trips are not free)
        private float _posStamp;            // Unscaled time of the last position refresh

        private const float PositionCacheSec = 0.1f;

        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void OnDisable()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        }

        /// <summary>Leaving the world (title screen, lobby, another world) must not leave the song sounding:
        /// the MCI device lives outside Unity's scene graph and keeps playing after the scene is gone.
        /// Entering a world fires this event too, but nothing is playing at that point, so it is a no-op then.</summary>
        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (_current == null) return;
            string name = string.IsNullOrEmpty(next.name) ? "(未命名场景)" : next.name;
            StopAll("切换场景离开当前世界 → " + name);
            Entry.Watcher?.ResetRunState();
            Entry.Aizo?.ResetRunState();
        }

        private void Update()
        {
            // Reset on natural end: winmm has no callback, so a timer is the fallback.
            if (_current != null && _endTime > 0f && Time.unscaledTime >= _endTime)
            {
                AudioSourceKey src = _current.Value;
                SystemAudio.Stop(_currentWav);
                Log.Message("[rainylove] 音频自然结束，已复位状态（" + src + "）。");
                ResetState();
            }
        }

        private void OnDestroy()
        {
            SystemAudio.Stop(_currentWav);
            ResetState();
            if (Instance == this) Instance = null;
        }

        /// <summary>Stop all playback immediately and reset the channel. Used on mod unload, on leaving a world and
        /// when the run is over (nobody left inside the dungeon). Manual test playback is stopped as well: once the
        /// run is gone there is no context left for a background song.</summary>
        public void StopAll(string reason = null)
        {
            if (_current == null) return;   // Nothing sounding: the device is already closed, stay silent in the log

            AudioSourceKey src = _current.Value;
            string path = _currentWav;
            ResetState();
            SystemAudio.Stop(path);
            Log.Message("[rainylove] 停止播放（" + src + "）：" + (reason ?? "全部停止") + "。");
        }

        /// <summary>Elapsed seconds for this source; returns -1 when this source is not playing.
        /// While the track loops, the wall clock runs past the track length, so the position inside the current
        /// repetition is read back from the device instead (this is what the timeline-node stop policy needs).</summary>
        public float GetElapsedSeconds(AudioSourceKey src)
        {
            if (_current != src || _startTime <= 0f) return -1f;
            if (!_loop) return Time.unscaledTime - _startTime;

            if (_posStamp <= 0f || Time.unscaledTime - _posStamp >= PositionCacheSec)
            {
                _posStamp = Time.unscaledTime;
                _posCache = SystemAudio.GetPositionSeconds();
            }
            if (_posCache >= 0f) return _posCache;

            // Device did not answer: fall back to the wall clock folded into one repetition.
            return _duration > 0f
                ? (Time.unscaledTime - _startTime) % _duration
                : Time.unscaledTime - _startTime;
        }

        /// <summary>Whether this source is currently repeating at the device level.</summary>
        public bool IsLooping(AudioSourceKey src) => _current == src && _loop;

        /// <summary>Total duration of this source's current audio (seconds); returns 0 when unknown.</summary>
        public float GetDuration(AudioSourceKey src) => _current == src ? _duration : 0f;

        /// <summary>Auto-trigger request. Returns true when playback actually started (the caller should set its 'triggered this run' flag to avoid repeats).
        /// loop is only honoured when the driver supports it (MP3/mpegvideo does); otherwise playback is single-shot.</summary>
        public bool StartAuto(AudioSourceKey src, string audioPath, float volume, bool loop = false)
        {
            if (string.IsNullOrEmpty(audioPath)) return false;

            // Already auto-playing this very source: report success and do NOT restart. Without this guard any
            // upper-layer re-evaluation (e.g. the 'played this run' flag being cleared while the music is still
            // sounding) would call Play() again, and SystemAudio.Play closes the device before opening it, so the
            // track would jump back to 0:00 and never reach its end.
            if (_current == src && !_currentManual) return true;

            if (_current == null) return Play(src, audioPath, volume, false, loop);

            // Manual playback in progress: manual always wins, auto never interrupts.
            if (_currentManual) return false;

            // Auto vs auto: decided by priority.
            if (!ShouldTakeOver(src)) return false;

            return Play(src, audioPath, volume, false, loop);
        }

        /// <summary>Caller's condition ended; stops this source's own auto playback (manual playback is unaffected).</summary>
        public void StopAuto(AudioSourceKey src)
        {
            if (_current == src && !_currentManual)
            {
                string path = _currentWav;
                ResetState();
                SystemAudio.Stop(path);
            }
        }

        /// <summary>Config UI 'Test Play / Audio Playback Test': explicit user action, always takes over immediately.</summary>
        public void StartManual(AudioSourceKey src, string audioPath, float volume)
        {
            if (string.IsNullOrEmpty(audioPath)) return;
            Play(src, audioPath, volume, true, false);
        }

        /// <summary>Config UI 'Stop Playback / Audio Playback Stop': stops only this source's sound; does not affect the other source.</summary>
        public void StopManual(AudioSourceKey src)
        {
            if (_current == src)
            {
                string path = _currentWav;
                ResetState();
                SystemAudio.Stop(path);
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

        /// <summary>Start playback through the system audio device. State is updated only when playback really
        /// started, so a device error (missing codec, unreadable file) cannot leave the channel 'occupied but silent'.
        /// A looping playback has no natural end, so its end timer stays disabled.</summary>
        private bool Play(AudioSourceKey src, string audioPath, float volume, bool manual, bool loop)
        {
            if (!SystemAudio.Play(audioPath, volume, loop)) return false;

            _current = src;
            _currentManual = manual;
            _currentWav = audioPath;
            float dur = AudioLoader.GetAudioDuration(audioPath);
            _duration = dur;
            _startTime = Time.unscaledTime;
            _loop = loop && SystemAudio.LastPlayLooped;
            _posCache = -1f;
            _posStamp = 0f;
            _endTime = (!_loop && dur > 0f) ? Time.unscaledTime + dur : 0f;
            Log.Message("[rainylove] 开始播放（" + src + (manual ? " · 手动测试" : " · 自动触发")
                        + (_loop ? " · 循环" : "")
                        + "，音量 " + volume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                        + "，时长 " + PlaybackNodeScheduler.Fmt(dur) + "）。");
            return true;
        }

        private void ResetState()
        {
            _current = null;
            _currentManual = false;
            _endTime = 0f;
            _currentWav = null;
            _startTime = 0f;
            _duration = 0f;
            _loop = false;
            _posCache = -1f;
            _posStamp = 0f;
        }
    }
}
