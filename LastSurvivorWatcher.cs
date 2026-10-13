using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// 多人模式下，当地牢里“阵亡人数 = 进本峰值人数 - 1”（即只剩最后一名存活者）时播放 rainy love。
    /// 关键：只统计“处于地牢内”的玩家（isInDungeon != 0）。退出大厅的人 isInDungeon == 0，不计入，
    /// 因此大厅里队友离开导致全场只剩 1 人不会误触发；只有地牢中其余人真正阵亡才触发。
    /// 纯客户端表现层：只读游戏状态，不改任何服务端数据，联机安全。
    /// </summary>
    public class LastSurvivorWatcher : MonoBehaviour
    {
        private float _scanTimer;
        private bool _playedThisRun;
        private string _wavPath;
        private bool _audioReady;
        private bool _playing;

        // 多人局判定：一旦见过 >=2 名玩家进入地牢就认定是多人局。
        private bool _multiplayerRoom;

        // 本局地牢的峰值人数（进入地牢的队伍规模）。阵亡者的 isInDungeon 不会清零，
        // 所以 inDungeonTotal 始终保留峰值；用它减去当前存活即为“阵亡人数”。
        private int _peakInDungeon;

        public void Initialize()
        {
            StartCoroutine(LoadClip());
        }

        private IEnumerator LoadClip()
        {
            // 直接解析 WAV（rainylove.wav），不再依赖 mp3。
            // 注意：这里绝不能写“找不到就重试”的循环——找不到时一次性告警并返回即可，
            // 否则（旧代码 do/while）会在首个 yield 之前死循环，直接卡死游戏启动。
            string path = Plugin.ResolveAudioPath(out bool exists);
            if (!exists)
            {
                Plugin.Log.LogWarning("[rainylove] 找不到音频文件（需要 rainylove.wav）：" + path +
                                      "。请把文件放到 BepInEx\\plugins\\rainylove\\Asset\\ 下");
                yield break;
            }

            // 准备 WAV：直接用它，或按音量缩放成临时 WAV。
            // 本构建里 Unity 的 AudioClip.Create/SetData 和 UnityWebRequest 全部失效
            // （UWR 连本地 200 响应都报 ConnectionError），故改走 Windows 系统音频（winmm PlaySound）直接播放，
            // 完全绕开 Unity 自己的音频管线。
            if (AudioLoader.TryPrepareWav(path, Plugin.Volume.Value, out string wavPath))
            {
                _wavPath = wavPath;
                _audioReady = true;
                Plugin.Log.LogInfo("[rainylove] 音频就绪（系统播放）：" + wavPath);
            }
            else
            {
                _audioReady = false;
                Plugin.Log.LogWarning("[rainylove] 音频准备失败：需要 rainylove.wav 文件（或同目录 mp3 + ffmpeg 兜底）：" + path);
            }
        }

        private void Update()
        {
            if (!Plugin.Enabled.Value) return;

            // 扫描放最前：输入系统出问题时，核心功能照常运行
            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer >= Plugin.ScanInterval.Value)
            {
                _scanTimer = 0f;
                Evaluate();
            }

            // F7 强制停止播放：无论触发原因，立即中断当前 mp3（仅作用于正在播放时）
            if (Plugin.EnableTestKey.Value)
            {
                if (InputBridge.Available && InputBridge.WasKeyPressedThisFrame("f7"))
                {
                    if (_playing)
                    {
                        StopPlayback();
                        Plugin.Log.LogMessage("[rainylove] F7 按下，已强制停止播放。");
                    }
                    else
                    {
                        Plugin.Log.LogMessage("[rainylove] F7 按下，但当前没有音乐在播放。");
                    }
                }
            }

            // F8 强制播放：绕过所有存活判定，直接验证"解码 -> AudioClip -> 播放"链路。
            // 主菜单就能用，不需要开多人局。
            if (Plugin.EnableTestKey.Value && InputBridge.Available
                && InputBridge.WasKeyPressedThisFrame(Plugin.TestPlayKey.Value))
            {
                Plugin.Log.LogMessage("[rainylove] 测试键按下，强制播放。");
                Play();
            }

            InputBridge.TickSelfCheck("f7", Plugin.Log, Time.unscaledDeltaTime);
        }

        private void Evaluate()
        {
            PlayerAvatar[] avatars = UnityEngine.Object.FindObjectsOfType<PlayerAvatar>(true);

            int inDungeonTotal = 0;   // 当前在“地牢”里的玩家数（含已阵亡但 isInDungeon 未清零的）
            int inDungeonAlive = 0;   // 地牢内仍存活的玩家数（isInDungeon != 0 且 !IsDead）
            bool selfInDungeonAlive = false;

            foreach (PlayerAvatar a in avatars)
            {
                if (a.isInDungeon == 0) continue;   // 仅统计真正在地牢内的人，大厅里的人排除
                inDungeonTotal++;
                if (!a.IsDead)
                {
                    inDungeonAlive++;
                    if (a.authority) selfInDungeonAlive = true;
                }
            }

            // 记录本局地牢峰值人数（队伍规模）。阵亡者仍计入 inDungeonTotal，故峰值只增不减。
            if (inDungeonTotal > _peakInDungeon) _peakInDungeon = inDungeonTotal;

            // 真正有 >=2 人进过地牢 -> 多人局
            if (inDungeonTotal >= 2) _multiplayerRoom = true;

            // 地牢彻底清空（全员离开/阵亡结算回大厅）：停止仍在播放的音乐，并复位整轮状态。
            // 这一步是规避“大厅里队友退出导致全场只剩 1 人”误触发的关键——回大厅即重置，
            // _multiplayerRoom / _peakInDungeon / _playedThisRun 全部归零，大厅里的来去不再影响判定。
            // 同时若音乐还在放（团灭后回大厅的情形），必须在此停止，否则进大厅仍会继续响。
            // 注意：这里只清这些标记、并停止播放，绝不能只清 _playing 的“标记”而不真正 Stop——
            // 否则主菜单/无玩家时被扫成“一轮结束”会把正在播放的音乐标记清掉，导致 F7/复活中断误以为没在播放。
            if (inDungeonTotal == 0)
            {
                if (_playing) StopPlayback();
                _multiplayerRoom = false;
                _peakInDungeon = 0;
                _playedThisRun = false;
                return;
            }

            if (!_multiplayerRoom) { _playedThisRun = false; return; }

            // 团灭：地牢内仍有死者（isInDungeon 未清零，inDungeonTotal 仍 >=2），但已无人存活——
            // 最后一名幸存者也死了，停止播放（否则音乐会一直响到回大厅）。
            if (inDungeonAlive == 0)
            {
                if (_playing) StopPlayback();
                _playedThisRun = false;   // 允许下一局重新触发
                return;
            }

            // 阵亡人数 = 峰值 - 当前地牢内存活。最后一人 = 峰值 - 1 且当前仅 1 人存活。
            // 播放中若有人复活/重新进队导致地牢内存活 > 1，立即中断 mp3（视为这一轮结束）
            if (inDungeonAlive > 1)
            {
                if (_playing) StopPlayback();
                _playedThisRun = false;   // 存活回升，允许将来再次触发
                return;
            }

            if (_playedThisRun) return;
            if (inDungeonAlive != 1) return;       // 不是“仅剩 1 人”
            if (_peakInDungeon < 2) return;        // 没进过 >=2 人的地牢，不触发

            if (Plugin.OnlyWhenSelfIsLast.Value && !selfInDungeonAlive) return;

            _playedThisRun = true;
            Play();
        }

        private void Play()
        {
            if (!_audioReady || string.IsNullOrEmpty(_wavPath))
            {
                Plugin.Log.LogWarning("[rainylove] 音频尚未就绪，本次跳过；按 F7/F8 可重试。");
                StartCoroutine(LoadClip());
                return;
            }

            SystemAudio.Play(_wavPath);
            _playing = true;
            Plugin.Log.LogMessage("[rainylove] 开始播放 rainy love（系统音频）。");
        }

        /// <summary>中断当前 mp3 播放并复位播放状态。</summary>
        private void StopPlayback()
        {
            if (!string.IsNullOrEmpty(_wavPath))
                SystemAudio.Stop(_wavPath);
            _playing = false;
        }

        /// <summary>音量配置变更时调用：停止当前播放，按新音量重新准备 WAV（下次播放即生效）。</summary>
        public void ReloadAudio()
        {
            StopPlayback();
            _audioReady = false;
            _wavPath = null;
            Plugin.Log.LogMessage("[rainylove] 音量配置已变更（=" + Plugin.Volume.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                                  + "），正在按新音量重新准备音频…");
            StartCoroutine(LoadClip());
        }
    }
}
