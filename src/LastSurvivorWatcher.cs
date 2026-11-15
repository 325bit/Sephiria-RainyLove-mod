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
    /// Stop policy is switchable (ModConfig.DeferStop):
    ///   false (default) - stop the moment the condition ends (a revived teammate silences the track at once);
    ///   true            - defer to a timeline node: p1 = 00:00 (start) / p2 = 01:27.1 / p3 = 02:10.07 / p4 = end of
    ///                     track. Re-triggering within 10s before a node defers to the next node; re-triggering at p3
    ///                     plays the whole track. This mode can play up to the full 219s, which is why it is off by default.
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
        private bool _audioReady;
        private bool _reloading;      // LoadClip coroutine in progress
        private bool _playAfterLoad;  // Start auto playback as soon as loading completes (audio was not ready when the condition triggered)
        private bool _manualAfterLoad;// Start manual test playback as soon as loading completes

        // Multiplayer detection: once >=2 players have entered the dungeon, the run counts as multiplayer.
        private bool _multiplayerRoom;

        // Peak party size for this run (party size on entering the dungeon). A dead player's isInDungeon is never cleared,
        // so inDungeonTotal always keeps the peak; subtracting current survivors gives the death count.
        private int _peakInDungeon;

        // A scene or level transition reports 'nobody inside the dungeon' for a frame or two; acting on the first such
        // reading would silence the track during normal play. Only an empty dungeon that persists this long counts.
        private const float EmptyGraceSec = 0.5f;

        private float _emptySince = -1f;     // Unscaled time the dungeon was first seen empty; <0 means not empty
        private bool _emptyHandled;          // The current empty period was already acted on
        private float _noAvatarSince = -1f;  // Same, for 'the scene has no player avatar at all'
        private bool _noAvatarHandled;
        private bool _leftRunLogged;         // 'Left the run' was already logged for the current period

        /// <summary>Reset everything that belongs to one run (multiplayer detection, peak party size, 'played' latch).
        /// Called when the run ends or the world is left, so the next run starts from a clean state instead of
        /// inheriting the previous one's flags.</summary>
        public void ResetRunState()
        {
            _sched.Reset();
            _playedThisRun = false;
            _multiplayerRoom = false;
            _peakInDungeon = 0;
        }

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

        /// <summary>Resolve the audio file and prepare it for playback. Nothing is decoded here: MCI decodes the MP3
        /// at playback time, so the source path is handed over untouched.</summary>
        private IEnumerator LoadClip()
        {
            if (_reloading) yield break;
            _reloading = true;
            _audioReady = false;
            _playPath = null;

            string path = ModConfig.ResolveAudioPath(ModConfig.DefaultRainyBase, out bool exists);
            if (!exists)
            {
                Log.Warning("[rainylove] 找不到音频文件（需要 rainylove.mp3）：" + path +
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
                float duration = AudioLoader.GetAudioDuration(playPath);
                _sched.Duration = duration;
                _audioReady = true;
                Log.Info("[rainylove] 音频就绪（系统播放）：" + playPath
                                   + "，时长 " + PlaybackNodeScheduler.Fmt(duration));
            }
            else
            {
                Log.Warning("[rainylove] 音频准备失败：需要 rainylove.mp3：" + path);
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

            // The run is gone entirely (back to the title screen / the world is being torn down). AudioChannel's scene
            // hook normally fires first; this is the fallback for a teardown that never swaps the scene.
            if (avatars.Length == 0)
            {
                if (Confirm(ref _noAvatarSince, ref _noAvatarHandled)) LeaveRun("场上已没有任何玩家角色");
                return;
            }
            _noAvatarSince = -1f;
            _noAvatarHandled = false;

            int inDungeonTotal = 0;
            int inDungeonAlive = 0;
            bool selfInDungeonAlive = false;
            // The local avatar must be resolved through Mirror, NOT through NetworkBehaviour.authority (see PlayerLocator).
            PlayerAvatar local = PlayerLocator.GetLocal(avatars);

            foreach (PlayerAvatar a in avatars)
            {
                if (a.isInDungeon == 0) continue;
                inDungeonTotal++;
                if (!a.IsDead)
                {
                    inDungeonAlive++;
                    if (ReferenceEquals(a, local)) selfInDungeonAlive = true;
                }
            }

            if (inDungeonTotal > _peakInDungeon) _peakInDungeon = inDungeonTotal;
            if (inDungeonTotal >= 2) _multiplayerRoom = true;

            // Nobody is inside the dungeon any more (everyone went back to the lobby / to town). This ends the run:
            // stop immediately instead of deferring to a node, because a node plan would keep the song sounding while
            // the player is no longer in the dungeon at all - which is exactly what players report as 'it never stops'.
            if (inDungeonTotal == 0)
            {
                if (Confirm(ref _emptySince, ref _emptyHandled)) LeaveRun("已离开地下城（无人在场）");
                return;
            }
            _emptySince = -1f;
            _emptyHandled = false;
            _leftRunLogged = false;

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
                // Only latch 'played this run' when playback really started; otherwise a request rejected by the
                // channel arbitration would silence this run's trigger forever.
                _playedThisRun = AutoPlay();
            }
        }

        /// <summary>Debounce for 'nobody is here': returns true exactly once per period, after it has lasted
        /// EmptyGraceSec. Level/scene transitions briefly report zero players and must not end the run.</summary>
        private static bool Confirm(ref float since, ref bool handled)
        {
            if (since < 0f) since = Time.unscaledTime;
            if (handled) return false;
            if (Time.unscaledTime - since < EmptyGraceSec) return false;
            handled = true;
            return true;
        }

        /// <summary>End of run: drop any pending node stop, silence the channel (both sources - a song must not
        /// outlive the run) and clear the per-run state.</summary>
        private void LeaveRun(string why)
        {
            ResetRunState();
            // Also clear AIZO's per-run state. This path used to reset only this component, so AizoWatcher's boss
            // hold latch and BossFightWatcher survived a run boundary: the hold stayed engaged after the boss died
            // and AIZO ignored the health threshold for the rest of the session. AudioChannel's scene hook does the
            // same on a world swap; this covers the paths that never swap the scene.
            Entry.Aizo?.ResetRunState();
            AudioChannel.Instance?.StopAll(why);
            if (_leftRunLogged) return;
            _leftRunLogged = true;
            Log.Message("[rainylove] " + why + " → 本局状态已清理，顺延计划同时失效。");
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

            // Immediate-stop mode (DeferStop off): a revived teammate silences the track on the next scan.
            // No node plan is ever created, so nothing can keep the track running to its end.
            if (!ModConfig.DeferStop)
            {
                _sched.Reset();
                if (!conditionMet)
                {
                    ch.StopAuto(AudioSourceKey.RainyLove);
                    Log.Message("[rainylove] 条件解除（顺延播放已关闭）→ 立即停止播放。");
                }
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

        /// <summary>Volume changed: the MP3 device applies the new volume on the next play, so nothing has to be
        /// re-prepared - the prepared path stays valid. Interrupts auto playback only.</summary>
        public void OnParamChanged()
        {
            _sched.Reset();
            AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
            Log.Message("[rainylove] RainyLove 音量已更新，下次播放直接使用新音量。");
        }

        // ===================== Internal implementation =====================

        /// <summary>Playback triggered by the condition: request arbitration directly if audio is ready, otherwise queue until loading completes.
        /// Returns true only when the channel really started playing this source.</summary>
        private bool AutoPlay()
        {
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return false;
            }
            if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _playPath, _playVolume))
            {
                _sched.Reset();     // A new playback starts, so reset the node schedule
                return true;
            }
            return false;
        }
    }
}
