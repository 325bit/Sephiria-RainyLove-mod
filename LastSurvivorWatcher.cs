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
    /// </summary>
    public class LastSurvivorWatcher : MonoBehaviour
    {
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

        public void Initialize()
        {
            StartCoroutine(LoadClip());
        }

        /// <summary>按当前音量 / 淡出准备 WAV（淡入已移除）。加载完成后若存在待播放请求则立即播放。</summary>
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
                                      "。请把文件放到 BepInEx\\plugins\\rainylove\\Asset\\ 下");
                _reloading = false;
                yield break;
            }

            // 本构建里 Unity 音频管线失效，改走 Windows 系统音频（winmm PlaySound）。
            // 音量缩放 + 淡出包络烘焙进临时 WAV（不依赖 ffmpeg）。RainyLove 无淡入（fadeIn=0）。
            string wavPath;
            if (AudioLoader.TryPrepareWav(path, Plugin.Volume.Value, 0f, Plugin.FadeOutSeconds.Value, out wavPath))
            {
                _wavPath = wavPath;
                _audioReady = true;
                Plugin.Log.LogInfo("[rainylove] 音频就绪（系统播放）：" + wavPath);
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
                    if (manual) AudioChannel.Instance?.StartManual(AudioSourceKey.RainyLove, _wavPath);
                    else if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _wavPath)) _playedThisRun = true;
                }
            }
        }

        private void Update()
        {
            if (!Plugin.Enabled.Value)
            {
                AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);   // 总开关只停自动播放，手动测试不受影响
                return;
            }

            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer >= Plugin.ScanInterval.Value)
            {
                _scanTimer = 0f;
                Evaluate();
            }
        }

        private void OnDestroy()
        {
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

            // 地牢彻底清空：停止仍在播放的音乐，并复位整轮状态。
            if (inDungeonTotal == 0)
            {
                AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
                _multiplayerRoom = false;
                _peakInDungeon = 0;
                _playedThisRun = false;
                return;
            }

            if (!_multiplayerRoom) { _playedThisRun = false; return; }

            // 团灭：已无人存活，停止播放，允许下一局重新触发。
            if (inDungeonAlive == 0)
            {
                AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
                _playedThisRun = false;
                return;
            }

            // 存活回升到 >1：视为这一轮结束，停止并允许将来再次触发。
            if (inDungeonAlive > 1)
            {
                AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
                _playedThisRun = false;
                return;
            }

            if (_playedThisRun) return;
            if (inDungeonAlive != 1) return;
            if (_peakInDungeon < 2) return;

            if (Plugin.OnlyWhenSelfIsLast.Value && !selfInDungeonAlive) return;

            _playedThisRun = true;
            AutoPlay();
        }

        // ===================== 供配置界面调用的播放控制 =====================

        /// <summary>配置界面的“测试播放”：不看存活判定，直接走一遍加载 + 播放链路（手动，只受停止按钮控制）。</summary>
        public void TestPlay()
        {
            Plugin.Log.LogMessage("[rainylove] 配置界面：RainyLove 测试播放。");
            if (!_audioReady || string.IsNullOrEmpty(_wavPath))
            {
                _playAfterLoad = true;
                _manualAfterLoad = true;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            AudioChannel.Instance?.StartManual(AudioSourceKey.RainyLove, _wavPath);
        }

        /// <summary>配置界面的“停止播放”。</summary>
        public void StopFromUi()
        {
            if (AudioChannel.Instance != null && AudioChannel.Instance.IsPlaying(AudioSourceKey.RainyLove))
            {
                AudioChannel.Instance.StopManual(AudioSourceKey.RainyLove);
                Plugin.Log.LogMessage("[rainylove] 配置界面：已停止 RainyLove 播放。");
            }
            else
            {
                Plugin.Log.LogMessage("[rainylove] 配置界面：RainyLove 当前没有音乐在播放。");
            }
        }

        /// <summary>音量 / 淡出被改动：作废已渲染的 WAV，下次播放按新参数重新渲染。
        /// 只打断自动播放；手动测试播放不受影响（只受停止按钮控制）。</summary>
        public void OnParamChanged()
        {
            AudioChannel.Instance?.StopAuto(AudioSourceKey.RainyLove);
            _audioReady = false;
            _wavPath = null;
            Plugin.Log.LogMessage("[rainylove] RainyLove 音频参数已变更，下次播放按新参数重新渲染。");
        }

        // ===================== 内部实现 =====================

        /// <summary>条件触发播放：音频就绪直接请求仲裁；否则排队等加载完。</summary>
        private void AutoPlay()
        {
            if (!_audioReady || string.IsNullOrEmpty(_wavPath))
            {
                _playAfterLoad = true;
                _manualAfterLoad = false;
                if (!_reloading) StartCoroutine(LoadClip());
                return;
            }
            if (AudioChannel.Instance != null && AudioChannel.Instance.StartAuto(AudioSourceKey.RainyLove, _wavPath)) _playedThisRun = true;
        }
    }
}
