using System.Collections;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// In multiplayer, plays rainy love when the dungeon death count equals the run's peak party size minus one (i.e. only one survivor remains).
    /// Important: only players 'inside the dungeon' (isInDungeon != 0) are counted. Players who left for the lobby are excluded,
    /// so teammates leaving for the lobby and dropping the total to 1 does not false-trigger; only real deaths in the dungeon do.
    /// Pure client-side presentation: reads game state only, never modifies server data, safe for online play.
    ///
    /// This class only decides 'when to play'; playback and stopping are delegated to AudioChannel (which arbitrates RainyLove vs AIZO priority).
    /// The config UI 'Test Play / Stop Playback' also goes through AudioChannel, and manual playback is ended only by the stop button.
    ///
    /// Stop policy matches AIZO: when the condition ends (leaving the dungeon / total wipe / survivors back to &gt;1), do not stop immediately;
    /// instead defer stopping to a timeline node:
    ///     p1 = 00:00 (start) / p2 = 01:27.1 / p3 = 02:10.07 / p4 = end of track
    /// If the condition returns within 10s before a node (only one survivor again), defer to the next node; if it also triggers at p3, play the whole track.
    /// </summary>
    public class LastSurvivorWatcher : MonoBehaviour
    {
        // ===== Timeline nodes (seconds): p2 01:27.1 / p3 02:10.07 / p4 end of track; 10s window =====
        private readonly PlaybackNodeScheduler _sched = new PlaybackNodeScheduler(
            new[] { 87.1f, 130.07f },
            new[] { "p2 01:27.1", "p3 02:10.07", "p4 曲末" },
            10f);

        private float _scanTimer;
        private bool _playedThisRun;
        private string _playPath;
        private float _playVolume = 1f;
        private bool _volumeBaked;    // True when the volume was baked into a temp WAV, so a volume change must reload
        private bool _audioReady;
        private bool _reloading;      // LoadClip coroutine in progress
        private bool _playAfterLoad;  // Start auto playback as soon as loading completes (audio was not ready when the condition triggered)
        private bool _manualAfterLoad;// Start manual test playback as soon as loading completes

        // Multiplayer detection: once >=2 players have entered the dungeon, the run counts as multiplayer.
        private bool _multiplayerRoom;

        // Peak party size for this run (party size on entering the dungeon). A dead player's isInDungeon is never cleared,
        // so inDungeonTotal always keeps the peak; subtracting current survivors gives the death count.
        private int _peakInDungeon;

        /// <summary>Whether audio is currently playing (shown as status in the config UI).</summary>
        public bool IsPlaying => AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.RainyLove);

        /// <summary>Whether audio has been prepared with the current parameters (shown as status in the config UI).</summary>
        public bool IsAudioReady => _audioReady;

        /// <summary>Whether the prepared audio is still usable (the temp directory may be cleaned or the file removed elsewhere).
        /// Use this instead of _audioReady alone, otherwise the UI shows 'ready' but clicking play produces no sound.</summary>
        private bool IsUsable => _audioReady
                                 && !string.IsNullOrEmpty(_playPath)
                                 && System.IO.File.Exists(_playPath);

        public void Initialize()
        {
            StartCoroutine(LoadClip());
        }

        /// <summary>Resolve the audio file and prepare it for playback. Nothing is decoded here: MCI decodes MP3 at
        /// playback time, and only user-supplied WAV files get a volume-baked temp copy.</summary>
        private IEnumerator LoadClip()
        {
            if (_reloading) yield break;
            _reloading = true;
            _audioReady = false;
            _playPath = null;

            string path = ModConfig.ResolveAudioPath(ModConfig.DefaultRainyBase, out bool exists);
            if (!exists)
            {
                Log.Warning("[rainylove] 找不到音频文件（需要 rainylove.mp3 或 rainylove.wav）：" + path +
                                      "。请把文件放到 WorkshopLocal/rainylove/Asset/ 下");
                _reloading = false;
                yield break;
            }

            // Unity's audio pipeline is broken in this build, so playback goes through the Windows system audio
            // interface (winmm MCI). Fade-in/out was removed and stopping is node-controlled.
            if (AudioLoader.TryPrepareAudio(path, ModConfig.Volume, out string playPath, out float playVolume))
            {
                _playPath = playPath;
                _playVolume = playVolume;
                _volumeBaked = !string.Equals(playPath, path, System.StringComparison.OrdinalIgnoreCase);
                float duration = AudioLoader.GetAudioDuration(playPath);
                _sched.Duration = duration;
                _audioReady = true;
                Log.Info("[rainylove] 音频就绪（系统播放）：" + playPath
                                   + "，时长 " + PlaybackNodeScheduler.Fmt(duration));
            }
            else
            {
                Log.Warning("[rainylove] 音频准备失败：需要 rainylove.mp3 或 rainylove.wav：" + path);
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
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.RainyLove, _playPath, _playVolume);
                    else if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _playPath, _playVolume)) _playedThisRun = true;
                }
            }
        }

        private void Update()
        {
            if (!ModConfig.Enabled)
            {
                _sched.Reset();
                AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);   // The master toggle stops auto playback only; manual test playback is unaffected
                return;
            }

            // 'Stop at node' is checked every frame to avoid overshoot from the 0.25s scan interval.
            TickScheduledStop();

            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer >= ModConfig.ScanInterval)
            {
                _scanTimer = 0f;
                Evaluate();
            }
        }

        private void OnDestroy()
        {
            _sched.Reset();
            AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
            AudioChannel.Instance?.StopManual(AudioSourceKey.RainyLove);
        }

        private void Evaluate()
        {
            PlayerAvatar[] avatars = UnityEngine.Object.FindObjectsOfType<PlayerAvatar>(true);

            int inDungeonTotal = 0;
            int inDungeonAlive = 0;
            bool selfInDungeonAlive = false;

            foreach (PlayerAvatar a in avatars)
            {
                if (a.isInDungeon == 0) continue;
                inDungeonTotal++;
                if (!a.IsDead)
                {
                    inDungeonAlive++;
                    if (a.authority) selfInDungeonAlive = true;
                }
            }

            if (inDungeonTotal > _peakInDungeon) _peakInDungeon = inDungeonTotal;
            if (inDungeonTotal >= 2) _multiplayerRoom = true;

            // Dungeon fully cleared: no immediate stop, defer to a node; also reset the run state.
            if (inDungeonTotal == 0)
            {
                ScanCondition(false);
                _multiplayerRoom = false;
                _peakInDungeon = 0;
                _playedThisRun = false;
                return;
            }

            if (!_multiplayerRoom) { ScanCondition(false); _playedThisRun = false; return; }

            // Trigger condition: multiplayer run + peak >=2 + exactly 1 survivor in the dungeon
            // (with OnlyWhenSelfIsLast, that last survivor must also be the local player).
            bool lastSurvivor = _multiplayerRoom
                                && _peakInDungeon >= 2
                                && inDungeonAlive == 1
                                && (!ModConfig.OnlyWhenSelfIsLast || selfInDungeonAlive);

            // A total wipe (alive==0) / survivors rising above 1 both make lastSurvivor false -> deferred node stop.
            ScanCondition(lastSurvivor);

            if (!_playedThisRun && lastSurvivor)
            {
                _playedThisRun = true;
                AutoPlay();
            }
        }

        /// <summary>Feed one 'condition met?' result to the node scheduler; when not auto-playing, clear the schedule and allow a later replay.</summary>
        private void ScanCondition(bool conditionMet)
        {
            AudioChannel ch = AudioChannel.Instance;
            bool autoPlaying = ch != null && ch.IsPlaying(AudioSourceKey.RainyLove)
                               && !ch.IsManualPlaying(AudioSourceKey.RainyLove);
            if (!autoPlaying)
            {
                _sched.Reset();
                // Re-arm only once the condition is no longer met, otherwise the last-survivor state would replay the whole track in a loop.
                if (!conditionMet) _playedThisRun = false;
                return;
            }

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.RainyLove);
            if (elapsed < 0f) { _sched.Reset(); return; }

            string msg = _sched.OnScan(conditionMet, elapsed);
            if (msg != null) Log.Message("[rainylove] " + msg);
        }

        /// <summary>Checked every frame: stop once the scheduled node is reached (no fade-out, hard stop).</summary>
        private void TickScheduledStop()
        {
            if (!_sched.HasPlan) return;

            AudioChannel ch = AudioChannel.Instance;
            if (ch == null) return;
            if (!ch.IsPlaying(AudioSourceKey.RainyLove) || ch.IsManualPlaying(AudioSourceKey.RainyLove)) return;

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.RainyLove);
            if (elapsed < 0f) { _sched.Reset(); return; }

            int verdict = _sched.CheckStop(elapsed, out string msg);
            if (verdict == PlaybackNodeScheduler.NoStop) return;

            if (verdict == PlaybackNodeScheduler.StopNow)
            {
                ch.StopAuto(AudioSourceKey.RainyLove);
                _sched.Reset();
                // Do not reset _playedThisRun here: the next scan decides based on the current condition (no replay while it still holds).
            }
            Log.Message("[rainylove] " + msg + "。");
        }

        // ===================== Playback controls called by the config UI =====================

        /// <summary>Config UI 'Test Play': bypasses the survivor check and runs the whole load + play path (manual; only the stop button can end it).</summary>
        public void TestPlay()
        {
            Log.Message("[rainylove] 配置界面：RainyLove 测试播放。");
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = true;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            _sched.Reset();
            AudioChannel.Instance?.StartManual(AudioSourceKey.RainyLove, _playPath, _playVolume);
        }

        /// <summary>Config UI 'Stop Playback'.</summary>
        public void StopFromUi()
        {
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.RainyLove))
            {
                _sched.Reset();
                AudioChannel.Instance.StopManual(AudioSourceKey.RainyLove);
                Log.Message("[rainylove] 配置界面：已停止 RainyLove 播放。");
            }
            else
            {
                Log.Message("[rainylove] 配置界面：RainyLove 当前没有音乐在播放。");
            }
        }

        /// <summary>Volume changed: for MP3 the device applies the new volume on the next play, so nothing has to be
        /// re-prepared; a volume-baked WAV copy does have to be re-rendered. Interrupts auto playback only.</summary>
        public void OnParamChanged()
        {
            _sched.Reset();
            AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
            if (!_volumeBaked)
            {
                Log.Message("[rainylove] RainyLove 音量已更新，下次播放直接使用新音量。");
                return;
            }
            _audioReady = false;
            _playPath = null;
            Log.Message("[rainylove] RainyLove 音频参数已变更，下次播放按新参数重新渲染。");
        }

        // ===================== Internal implementation =====================

        /// <summary>Playback triggered by the condition: request arbitration directly if audio is ready, otherwise queue until loading completes.</summary>
        private void AutoPlay()
        {
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _playPath, _playVolume))
            {
                _playedThisRun = true;
                _sched.Reset();     // A new playback starts, so reset the node schedule
            }
        }
    }
}
