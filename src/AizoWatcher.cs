using System.Collections;
using System.Reflection;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// AIZO trigger: watches the local player's health and plays AIZO.mp3 when either condition below is met:
    ///   - Current health percentage &lt; configured percentage threshold (AizoHealthPercent, 0~99)
    ///   - Current health value &lt; configured fixed threshold (AizoHealthValue, 0~9999)
    /// A threshold of 0 disables that condition (it can never be met).
    /// Playback still goes through AudioChannel arbitration; the config UI 'Audio Playback Test / Stop' uses the manual channel.
    ///
    /// Stop policy is switchable (ModConfig.DeferStop):
    ///   false (default) - stop the moment health is back above both thresholds;
    ///   true            - defer to the next node: AIZO timeline p1 = 00:00 / p2 = 00:43.5 / p3 = 01:38 / p4 = 02:20 /
    ///                     p5 = end of track. Example: full health at 01:00 -> keeps playing until 01:38 (p3).
    ///                     Re-triggering within NodeWindowSec (10s) before the target node defers to the following node;
    ///                     re-triggering in the p4 (02:20) window plays the whole track.
    ///
    /// Health field names vary between builds, so reflection probes PlayerAvatar for current / max / direct-percentage fields by candidate name.
    /// The resolved MemberInfo is cached and logged so the correct field can be verified.
    ///
    /// Boss hold (ModConfig.AizoBossHold, off by default): while BossFightWatcher reports a living Miniboss/Boss,
    /// nothing stops the track - not the health condition, not DeferStop, not the node schedule - and playback is
    /// started with device-level MCI 'repeat' so a finished track keeps sounding. When the fight ends the normal
    /// DeferStop policy takes over again. Starting still requires the health trigger: the hold only extends, it
    /// never forces a start.
    /// </summary>
    public class AizoWatcher : MonoBehaviour
    {
        // ===== Timeline nodes (seconds): p2 00:43.5 / p3 01:38 / p4 02:20 / p5 end of track; 10s window =====
        private readonly PlaybackNodeScheduler _sched = new PlaybackNodeScheduler(
            new[] { 43.5f, 98f, 140f },
            new[] { "p2 00:43.5", "p3 01:38", "p4 02:20", "p5 曲末" },
            10f);

        // When loading a save or changing scenes, the game first spawns a fresh local PlayerAvatar;
        // health fields are not populated until save data has been applied (see 'Reset For New Game' in the log).
        // During that window hp=0 / MaxHp=0 reads as false low health and would trigger AIZO by mistake.
        // Therefore: a new avatar waits AvatarSettleSec, then requires valid health data and MinLowSec of sustained low health before playback starts.
        private const float AvatarSettleSec = 2.5f;
        private const float MinLowSec = 0.75f;

        private float _scanTimer;
        private string _playPath;
        private float _playVolume = 1f;
        private bool _audioReady;
        private bool _reloading;
        private bool _playAfterLoad;
        private bool _manualAfterLoad;
        private bool _triggered;       // Already triggered during this low-health interval; prevents repeated triggering
        private bool _holdLatch;       // A boss fight is the reason the music is on; keep it alive until the fight ends
        private bool _holdLogged;      // Transition log guard for the boss hold

        private PlayerAvatar _lastSelf;     // Local player avatar seen on the previous scan
        private float _selfReadyAt;         // Time after which the current avatar may participate in health evaluation
        private float _lowSince = -1f;      // Start time of sustained low health; <0 means not currently low
        private bool _invalidLogged;        // Log an invalid reading only once per avatar to avoid log spam

        public bool IsPlaying => AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo);
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

        private IEnumerator LoadClip()
        {
            if (_reloading) yield break;
            _reloading = true;
            _audioReady = false;
            _playPath = null;

            string path = ModConfig.ResolveAudioPath(ModConfig.DefaultAizoBase, out bool exists);
            if (!exists)
            {
                Log.Warning("[rainylove][AIZO] 找不到音频文件（需要 AIZO.mp3）：" + path +
                                      "。请把文件放到 WorkshopLocal/rainylove/Asset/ 下");
                _reloading = false;
                yield break;
            }

            // AIZO only applies volume; fade-in/fade-out were removed and stopping is controlled by timeline nodes.
            if (AudioLoader.TryPrepareAudio(path, ModConfig.AizoVolume, out string playPath, out float playVolume))
            {
                _playPath = playPath;
                _playVolume = playVolume;
                float duration = AudioLoader.GetAudioDuration(playPath);
                _sched.Duration = duration;
                _audioReady = true;
                Log.Info("[rainylove][AIZO] 音频就绪（系统播放）：" + playPath
                                   + "，时长 " + PlaybackNodeScheduler.Fmt(duration));
            }
            else
            {
                Log.Warning("[rainylove][AIZO] 音频准备失败：需要 AIZO.mp3：" + path);
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
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _playPath, _playVolume);
                    else if (AudioChannel.Instance != null
                             && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _playPath, _playVolume,
                                                                ModConfig.AizoBossHold && BossFightWatcher.Active))
                        _triggered = true;
                }
            }
        }

        private void Update()
        {
            if (!ModConfig.AizoEnabled)
            {
                _sched.Reset();
                _holdLatch = false;
                AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);   // Disabling stops auto playback only; manual test playback is unaffected
                return;
            }

            // Keep the boss-fight detector warm for the whole session (it throttles itself to one scan per 0.5s).
            BossFightWatcher.Tick();

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
            AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            AudioChannel.Instance?.StopManual(AudioSourceKey.Aizo);
        }

        /// <summary>Clear the per-run state when the run ends (left the dungeon, back to the title screen, world swap).
        /// The next world then re-runs the settle window instead of inheriting this avatar's low-health latch.</summary>
        public void ResetRunState()
        {
            _sched.Reset();
            _triggered = false;
            _holdLatch = false;
            _holdLogged = false;
            _lowSince = -1f;
            _lastSelf = null;
            _selfReadyAt = 0f;
            BossFightWatcher.Reset();
        }

        private void Evaluate()
        {
            PlayerAvatar self = FindSelf();
            if (self == null)
            {
                _lastSelf = null;
                _lowSince = -1f;
                // No local avatar means no boss fight can be keeping the track alive. Clearing the latch here also
                // covers the case where the avatar disappears (death, world teardown) while the hold was engaged -
                // otherwise the latch would survive into the next life and skip every stop path again.
                _holdLatch = false;
                _holdLogged = false;
                return;
            }

            // A new avatar just appeared: health has not been loaded from the save yet, so skip evaluation entirely.
            if (!ReferenceEquals(self, _lastSelf))
            {
                _lastSelf = self;
                _selfReadyAt = Time.unscaledTime + AvatarSettleSec;
                _lowSince = -1f;
                _invalidLogged = false;
                _triggered = false;
                Log.Message("[rainylove][AIZO] avatar=new settle="
                    + AvatarSettleSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "s reason=等待存档血量写入");
            }

            if (Time.unscaledTime < _selfReadyAt) return;

            PlayerHealth.Ensure(self);
            if (!PlayerHealth.Read(self, out float cur, out float max, out float pct)) return;

            // max<=0 / cur<=0 means health data has not been written from the save yet (or this is a dead placeholder avatar);
            // never evaluate in that state, otherwise save loading is mistaken for low health and playback starts by mistake.
            if (max <= 0f || cur <= 0f)
            {
                if (!_invalidLogged)
                {
                    _invalidLogged = true;
                    Log.Message("[rainylove][AIZO] hp=invalid cur=" + cur + " max=" + max + " action=skip");
                }
                _lowSince = -1f;
                return;
            }

            bool low = (pct * 100f) < ModConfig.AizoHealthPercent
                       || cur < ModConfig.AizoHealthValue;

            // Starting playback requires sustained low health to filter out transient false readings from network sync or single-frame jitter.
            if (low)
            {
                if (_lowSince < 0f) _lowSince = Time.unscaledTime;
            }
            else
            {
                _lowSince = -1f;
                _triggered = false;
            }
            bool lowConfirmed = low && (Time.unscaledTime - _lowSince) >= MinLowSec;

            AudioChannel ch = AudioChannel.Instance;
            bool autoPlaying = ch != null && ch.IsPlaying(AudioSourceKey.Aizo)
                               && !ch.IsManualPlaying(AudioSourceKey.Aizo);

            // ---- BOSS / mini-boss hold (ModConfig.AizoBossHold) ----
            // While a boss fight lasts AIZO must not stop: the health condition, the defer switch and the node
            // schedule are all irrelevant until it ends. Playback is started with device-level 'repeat', so a track
            // that reaches its end keeps sounding instead of leaving a gap.
            // The latch records that this fight is what keeps the music on, so a track that was started *before*
            // the fight (and therefore is not looping yet) is restarted when it ends rather than left silent.
            // Starting still requires the normal low-health trigger - we only extend, never force a start.
            bool hold = ModConfig.AizoBossHold && BossFightWatcher.Active;
            if (hold)
            {
                if (autoPlaying) _holdLatch = true;
                if (_holdLatch)
                {
                    _sched.Reset();
                    if (!autoPlaying && AutoPlay())
                        Log.Message("[rainylove][AIZO] BOSS 战中续播（上一轮已播完）。");
                    if (!_holdLogged)
                    {
                        _holdLogged = true;
                        Log.Message("[rainylove][AIZO] BOSS 战中 → 不停止播放"
                                    + (ch != null && ch.IsLooping(AudioSourceKey.Aizo) ? "（循环中，播完自动重播）" : "（下一轮起循环）")
                                    + "。");
                    }
                    return;
                }
            }
            else if (_holdLatch)
            {
                _holdLatch = false;
                _holdLogged = false;
                Log.Message("[rainylove][AIZO] 已退出 BOSS 战保持 → 恢复常规停止逻辑。");
            }

            if (!autoPlaying)
            {
                // Not auto-playing: run the normal start decision and clear the node schedule.
                _sched.Reset();
                if (lowConfirmed && !_triggered && AutoPlay())
                {
                    _triggered = true;
                    Log.Message("[rainylove][AIZO] hp=trigger cur=" + cur + " max=" + max
                        + " pct=" + (pct * 100f).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                        + " lowFor=" + (Time.unscaledTime - _lowSince).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                        + "s action=play");
                }
                return;
            }

            // Immediate-stop mode (DeferStop off): health back above both thresholds silences AIZO on the next scan.
            // No node plan is created, so nothing can keep the track running to its end.
            if (!ModConfig.DeferStop)
            {
                _sched.Reset();
                if (!low)
                {
                    ch.StopAuto(AudioSourceKey.Aizo);
                    Log.Message("[rainylove][AIZO] 血量回到阈值以上（顺延播放已关闭）→ 立即停止播放。");
                }
                return;
            }

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.Aizo);
            if (elapsed < 0f) { _sched.Reset(); return; }

            // The stop decision during playback still uses the raw low value (recovery must schedule a node immediately), unaffected by start debouncing.
            string msg = _sched.OnScan(low, elapsed);
            if (msg != null) Log.Message("[rainylove][AIZO] " + msg);
        }

        /// <summary>Checked every frame: stop once the scheduled node is reached (no fade-out, hard stop).</summary>
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
            Log.Message("[rainylove][AIZO] " + msg + "。");
        }

        private PlayerAvatar FindSelf()
        {
            // Never use 'authority' here: on a host it is true for every avatar and on a pure client for none,
            // which made AIZO play on the host using a teammate's health and never play on clients. See PlayerLocator.
            return PlayerLocator.GetLocal();
        }

        // ===================== Playback controls called by the config UI =====================

        /// <summary>Config UI 'Audio Playback Test': manual, bypasses the health condition and plays immediately (only the stop button can end it).</summary>
        public void TestPlay()
        {
            Log.Message("[rainylove][AIZO] 配置界面：音频播放测试。");
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = true;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            _sched.Reset();
            AudioChannel.Instance?.StartManual(AudioSourceKey.Aizo, _playPath, _playVolume);
        }

        /// <summary>Config UI 'Audio Playback Stop'. An explicit stop also cancels the boss-fight hold for this
        /// encounter, otherwise the hold would immediately restart the track the user just silenced.</summary>
        public void StopFromUi()
        {
            _holdLatch = false;
            _holdLogged = false;
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.Aizo))
            {
                _sched.Reset();
                AudioChannel.Instance.StopManual(AudioSourceKey.Aizo);
                Log.Message("[rainylove][AIZO] 配置界面：已停止 AIZO 播放。");
            }
            else
            {
                Log.Message("[rainylove][AIZO] 配置界面：AIZO 当前没有音乐在播放。");
            }
        }

        /// <summary>Volume changed: the MP3 device applies the new volume on the next play, so nothing has to be
        /// re-prepared - the prepared path stays valid. Interrupts auto playback only.</summary>
        public void OnParamChanged()
        {
            _sched.Reset();
            _holdLatch = false;         // the volume change stops playback, so the hold must not resurrect it
            _holdLogged = false;
            AudioChannel.Instance?.StopAuto(AudioSourceKey.Aizo);
            Log.Message("[rainylove][AIZO] 音量已更新，下次播放直接使用新音量。");
        }

        // ===================== Internal implementation =====================

        private bool AutoPlay()
        {
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return false;
            }
            bool loop = ModConfig.AizoBossHold && BossFightWatcher.Active;
            bool ok = AudioChannel.Instance != null
                      && AudioChannel.Instance.StartAuto(AudioSourceKey.Aizo, _playPath, _playVolume, loop);
            if (ok) _sched.Reset();     // A new playback starts, so reset the node schedule
            return ok;
        }
    }

    /// <summary>
    /// Probe PlayerAvatar health fields (current / max / direct percentage) at runtime.
    /// Reflection looks up a Field or readable Property by candidate name order and caches the result to avoid per-frame reflection cost.
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
            Log.Info("[rainylove][AIZO] 血量字段探测：current="
                + (_cur != null ? _cur.Name + "(" + (_cur is FieldInfo ? "field" : "prop") + ")" : "未找到")
                + "，max=" + (_max != null ? _max.Name : "未找到")
                + "，percent=" + (_pct != null ? _pct.Name : "未找到"));
            if (_cur == null) Log.Warning("[rainylove][AIZO] 未能探测到当前血量字段，AIZO 不会触发；请把日志里的 PlayerAvatar 血量字段名反馈给我。");
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

        /// <summary>Read local health. Returns false when reading is impossible (field not resolved).</summary>
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
                percent = p > 1f ? p / 100f : p;   // Supports both 0~1 and 0~100 representations
            }
            return true;
        }
    }
}
