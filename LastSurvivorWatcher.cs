using System.Collections;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// 多人模式下，当地牢里“阵亡人数 = 进本峰值人数 - 1”（即只剩最后一名存活者）时播放 rainy love。
    /// 关键：只统计“处于地牢内”的玩家（isInDungeon != 0）。退出大厅的人不计入，
    /// 因此大厅里队友离开导致全场只剩 1 人不会误触发；只有地牢中其余人真正阵亡才触发。
    /// 纯客户端表现层：只读游戏状态，不改任何服务端数据，联机安全。
    ///
    /// 本类只负责“什么时候放”，播放/停止全部交给 AudioChannel（统一仲裁 RainyLove 与 AIZO 的优先级）。
    /// 配置界面的“测试播放 / 停止播放”也通过 AudioChannel，且手动播放只受停止按钮控制。
    ///
    /// 停止策略与 AIZO 一致：条件解除（出本 / 团灭 / 存活回升到 &gt;1）时不立即停，
    /// 而是顺延到时间轴节点再停：
    ///     p1 = 00:00（开始） / p2 = 01:27.1 / p3 = 02:10.07 / p4 = 曲末
    /// 节点前 10 秒内条件又满足（又只剩一人）→ 顺延到下一个节点；已到 p3 还触发 → 放完整首。
    /// </summary>
    public class LastSurvivorWatcher : MonoBehaviour
    {
        // ===== 时间轴节点（秒）：p2 01:27.1 / p3 02:10.07 / p4 曲末；窗口 10 秒 =====
        private readonly PlaybackNodeScheduler _sched = new PlaybackNodeScheduler(
            new[] { 87.1f, 130.07f },
            new[] { "p2 01:27.1", "p3 02:10.07", "p4 曲末" },
            10f);

        private float _scanTimer;
        private bool _playedThisRun;
        private string _wavPath;
        private bool _audioReady;
        private bool _reloading;      // LoadClip 协程进行中
        private bool _playAfterLoad;  // 加载完成后立即自动播放（条件触发时音频还没就绪）
        private bool _manualAfterLoad;// 加载完成后立即手动测试播放

        // 多人局判定：一旦见过 >=2 名玩家进入地牢就认定是多人局。
        private bool _multiplayerRoom;

        // 本局地牢的峰值人数（进入地牢的队伍规模）。阵亡者的 isInDungeon 不会清零，
        // 所以 inDungeonTotal 始终保留峰值；用它减去当前存活即为“阵亡人数”。
        private int _peakInDungeon;

        /// <summary>当前是否正在播放（供配置界面显示状态）。</summary>
        public bool IsPlaying => AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.RainyLove);

        /// <summary>音频是否已按当前参数准备好（供配置界面显示状态）。</summary>
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

        /// <summary>按当前音量准备 WAV（淡入淡出已取消）。加载完成后若存在待播放请求则立即播放。</summary>
        private IEnumerator LoadClip()
        {
            if (_reloading) yield break;
            _reloading = true;
            _audioReady = false;
            _wavPath = null;

            string path = Plugin.ResolveAudioPath(Plugin.DefaultRainyWav, out bool exists);
            if (!exists)
            {
                Plugin.Log.LogWarning("[rainylove] 找不到音频文件（需要 rainylove.wav）：" + path +
                                      "。请把文件放到 BepInEx\\plugins\\RainyLove_AIZO\\Asset\\ 下");
                _reloading = false;
                yield break;
            }

            // 本构建里 Unity 音频管线失效，改走 Windows 系统音频（winmm PlaySound）。
            // 只把音量缩放烘焙进临时 WAV（不依赖 ffmpeg）；淡入淡出已取消，停止改由节点控制。
            string wavPath;
            if (AudioLoader.TryPrepareWav(path, Plugin.Volume.Value, 0f, 0f, out wavPath))
            {
                _wavPath = wavPath;
                _sched.Duration = AudioLoader.GetWavDuration(wavPath);
                _audioReady = true;
                Plugin.Log.LogInfo("[rainylove] 音频就绪（系统播放）：" + wavPath
                                   + "，时长 " + PlaybackNodeScheduler.Fmt(AudioLoader.GetWavDuration(wavPath)));
            }
            else
            {
                Plugin.Log.LogWarning("[rainylove] 音频准备失败：需要 rainylove.wav（或同目录 mp3 + ffmpeg 兜底）：" + path);
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
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.RainyLove, _wavPath);
                    else if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _wavPath)) _playedThisRun = true;
                }
            }
        }

        private void Update()
        {
            if (!Plugin.Enabled.Value)
            {
                _sched.Reset();
                AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);   // 总开关只停自动播放，手动测试不受影响
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

            // 地牢彻底清空：不再立即停，顺延到节点；同时复位整轮状态。
            if (inDungeonTotal == 0)
            {
                ScanCondition(false);
                _multiplayerRoom = false;
                _peakInDungeon = 0;
                _playedThisRun = false;
                return;
            }

            if (!_multiplayerRoom) { ScanCondition(false); _playedThisRun = false; return; }

            // 触发条件：多人局 + 峰值 >=2 + 地牢内只剩 1 人存活
            // （OnlyWhenSelfIsLast 时还要求这最后一人是自己）。
            bool lastSurvivor = _multiplayerRoom
                                && _peakInDungeon >= 2
                                && inDungeonAlive == 1
                                && (!Plugin.OnlyWhenSelfIsLast.Value || selfInDungeonAlive);

            // 团灭(alive==0) / 存活回升到 >1 都会让 lastSurvivor 变假 → 走节点顺延停止。
            ScanCondition(lastSurvivor);

            if (!_playedThisRun && lastSurvivor)
            {
                _playedThisRun = true;
                AutoPlay();
            }
        }

        /// <summary>把一次“条件是否满足”喂给节点调度器；没在自动播放时清空计划并允许下次重播。</summary>
        private void ScanCondition(bool conditionMet)
        {
            AudioChannel ch = AudioChannel.Instance;
            bool autoPlaying = ch != null && ch.IsPlaying(AudioSourceKey.RainyLove)
                               && !ch.IsManualPlaying(AudioSourceKey.RainyLove);
            if (!autoPlaying)
            {
                _sched.Reset();
                // 只在“条件已经不成立”时才重新武装，否则最后存活者会一直循环重播整首。
                if (!conditionMet) _playedThisRun = false;
                return;
            }

            float elapsed = ch.GetElapsedSeconds(AudioSourceKey.RainyLove);
            if (elapsed < 0f) { _sched.Reset(); return; }

            string msg = _sched.OnScan(conditionMet, elapsed);
            if (msg != null) Plugin.Log.LogMessage("[rainylove] " + msg);
        }

        /// <summary>每帧检查：已播到计划的节点就停（无淡出，直接停）。</summary>
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
                // 不在这里复位 _playedThisRun：由下一次扫描按当前条件决定（条件仍成立就不重播）。
            }
            Plugin.Log.LogMessage("[rainylove] " + msg + "。");
        }

        // ===================== 供配置界面调用的播放控制 =====================

        /// <summary>配置界面的“测试播放”：不看存活判定，直接走一遍加载 + 播放链路（手动，只受停止按钮控制）。</summary>
        public void TestPlay()
        {
            Plugin.Log.LogMessage("[rainylove] 配置界面：RainyLove 测试播放。");
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = true;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            _sched.Reset();
            AudioChannel.Instance?.StartManual(AudioSourceKey.RainyLove, _wavPath);
        }

        /// <summary>配置界面的“停止播放”。</summary>
        public void StopFromUi()
        {
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.RainyLove))
            {
                _sched.Reset();
                AudioChannel.Instance.StopManual(AudioSourceKey.RainyLove);
                Plugin.Log.LogMessage("[rainylove] 配置界面：已停止 RainyLove 播放。");
            }
            else
            {
                Plugin.Log.LogMessage("[rainylove] 配置界面：RainyLove 当前没有音乐在播放。");
            }
        }

        /// <summary>音量被改动：作废已渲染的 WAV，下次播放按新参数重新渲染。
        /// 只打断自动播放；手动测试播放不受影响（只受停止按钮控制）。</summary>
        public void OnParamChanged()
        {
            _sched.Reset();
            AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
            _audioReady = false;
            _wavPath = null;
            Plugin.Log.LogMessage("[rainylove] RainyLove 音频参数已变更，下次播放按新参数重新渲染。");
        }

        // ===================== 内部实现 =====================

        /// <summary>条件触发播放：音频就绪直接请求仲裁；否则排队等加载完。</summary>
        private void AutoPlay()
        {
            if (!IsUsable)
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _wavPath))
            {
                _playedThisRun = true;
                _sched.Reset();     // 新的一次播放，节点计划从头开始
            }
        }
    }
}
