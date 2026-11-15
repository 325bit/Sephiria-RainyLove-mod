using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RainyLove
{
    /// <summary>
    /// Audio preparation for the official Workshop build.
    ///
    /// Playback goes through the Windows MCI interface (winmm mciSendString) because this game's Unity audio
    /// pipeline is unreliable, and MCI can decode MP3 through the system codec without bundling a decoder.
    /// The old ffmpeg / local-HTTP-server paths were removed earlier: external process execution and a TCP
    /// server are prohibited by the Workshop review system.
    ///
    /// MP3 is the only accepted source format. The packaged audio is MP3 and the mpegvideo driver is the only one
    /// that supports the two features this mod needs: setaudio volume and the 'repeat' flag. The earlier WAV path
    /// (local re-render with baked-in volume, then waveaudio device) is gone - it could not loop and its
    /// temporary-file renderer was dead weight.
    /// </summary>
    public static class AudioLoader
    {
        /// <summary>Whether path is an MP3 file. The only format the player accepts.</summary>
        internal static bool IsMp3(string path)
        {
            return path != null && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Validate a source file and return the path to hand to the player plus the volume to apply at
        /// playback time. The path is returned untouched and the volume is clamped to 0~1; the device applies it
        /// (setaudio volume). Returns false when the file is missing or is not an MP3.</summary>
        public static bool TryPrepareAudio(string srcPath, float volume, out string playPath, out float playVolume)
        {
            playPath = null;
            playVolume = 1f;
            if (string.IsNullOrEmpty(srcPath)) return false;

            float vol = volume < 0f ? 0f : (volume > 1f ? 1f : volume);

            if (!File.Exists(srcPath))
            {
                Log.Warning("[rainylove] 找不到音频文件：" + srcPath);
                return false;
            }

            if (!IsMp3(srcPath))
            {
                Log.Warning("[rainylove] 不支持的音频格式（只支持 MP3）：" + srcPath);
                return false;
            }

            playPath = srcPath;
            playVolume = vol;
            Log.Info("[rainylove] 音频就绪（MCI 直接播放 MP3）：" + srcPath
                               + "，音量 " + vol.ToString("F2", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>Duration of a track in seconds; 0 when unknown. Used by the node scheduler (end-of-track node)
        /// and by the channel's natural-end timer: MCI has no blocking 'finished' call, and its own length query is
        /// unreliable for MP3 (measured 674907 ms for a 219370 ms file), so the duration is read from the file itself.</summary>
        public static float GetAudioDuration(string path)
        {
            if (string.IsNullOrEmpty(path) || !IsMp3(path)) return 0f;
            return Mp3Duration.GetSeconds(path);
        }
    }

    /// <summary>
    /// MP3 duration by walking the frame headers. Needed because MCI's own length query is wrong for these files
    /// and because the node scheduler needs a real end-of-track time. Handles ID3v2 at the start, ID3v1 at the end,
    /// MPEG-1 / MPEG-2 / MPEG-2.5 Layer III, CBR and VBR. Returns 0 when the file cannot be parsed.
    /// </summary>
    internal static class Mp3Duration
    {
        private static readonly int[] BitratesV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        private static readonly int[] BitratesV2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
        private static readonly int[] RatesV1 = { 44100, 48000, 32000 };
        private static readonly int[] RatesV2 = { 22050, 24000, 16000 };
        private static readonly int[] RatesV25 = { 11025, 12000, 8000 };

        public static float GetSeconds(string path)
        {
            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 读取 MP3 失败：" + e.GetType().Name + " - " + e.Message);
                return 0f;
            }
            if (d.Length < 4) return 0f;

            int pos = 0;
            if (d.Length > 10 && d[0] == 'I' && d[1] == 'D' && d[2] == '3')
            {
                int tagSize = ((d[6] & 0x7F) << 21) | ((d[7] & 0x7F) << 14) | ((d[8] & 0x7F) << 7) | (d[9] & 0x7F);
                pos = Math.Min(d.Length, 10 + tagSize);
            }

            long samples = 0;
            int rate = 0;
            while (pos + 4 <= d.Length)
            {
                // Resync on the next 0xFF Ex byte pair after any garbage (ID3v1 trailing tag, padding, junk).
                if (d[pos] != 0xFF || (d[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }

                int version = (d[pos + 1] >> 3) & 3;   // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5, 1 = reserved
                int layer = (d[pos + 1] >> 1) & 3;     // 1 = Layer III
                int bitrateIndex = (d[pos + 2] >> 4) & 0x0F;
                int rateIndex = (d[pos + 2] >> 2) & 3;
                int padding = (d[pos + 2] >> 1) & 1;

                if (version == 1 || layer != 1 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3)
                {
                    pos++;
                    continue;
                }

                bool v1 = version == 3;
                int bitrate = (v1 ? BitratesV1L3 : BitratesV2L3)[bitrateIndex] * 1000;
                int sampleRate = v1 ? RatesV1[rateIndex] : (version == 2 ? RatesV2[rateIndex] : RatesV25[rateIndex]);
                int frameLength = (v1 ? 144 : 72) * bitrate / sampleRate + padding;
                if (frameLength < 4) { pos++; continue; }

                samples += v1 ? 1152 : 576;
                rate = sampleRate;
                pos += frameLength;

                if (pos + 3 <= d.Length && d[pos] == 'T' && d[pos + 1] == 'A' && d[pos + 2] == 'G') break;
            }

            return rate > 0 ? (float)((double)samples / rate) : 0f;
        }
    }

    /// <summary>
    /// Windows system audio playback through MCI (winmm mciSendString), fully bypassing the broken Unity audio
    /// pipeline. MCI decodes MP3 with the system codec, so no decoder is bundled and nothing is written to disk.
    ///
    /// Only the mpegvideo (MP3) device is used: it is the one that accepts setaudio volume and the 'repeat' flag.
    /// The waveaudio device was dropped together with the WAV path - it rejects both.
    ///
    /// Threading: every MCI command runs on one dedicated worker thread that initialises COM as **STA**.
    /// This is not optional - the MPEG video driver (mciqtz32.dll) builds a DirectShow graph internally and
    /// refuses to open on an MTA thread, failing with MCIERR_CANNOT_LOAD_DRIVER (266). Measured: the identical
    /// "open ... type mpegvideo" succeeds on an STA thread and fails with 266 on an MTA thread. Running the
    /// commands on our own thread also keeps the game main thread out of the device-open path.
    ///
    /// Single-device invariant: MCI happily runs several aliases at once (verified: two MP3s reported "playing"
    /// simultaneously), and this mod must NEVER sound two tracks together. Play() therefore always closes the
    /// previous device before opening the next one, and only one alias is ever tracked, so a second source can
    /// only replace the first - never mix with it.
    /// </summary>
    internal static class SystemAudio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr hwndCallback);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        private const uint COINIT_APARTMENTTHREADED = 0x2;
        private const int CommandTimeoutMs = 8000;

        /// <summary>The MCI device for MP3 (DirectShow graph behind it, hence the STA requirement).</summary>
        private const string Device = "mpegvideo";

        private sealed class Job
        {
            public Action Run;
            public ManualResetEventSlim Done;
        }

        private static readonly object _gate = new object();
        private static readonly Queue<Job> _queue = new Queue<Job>();
        private static Thread _worker;
        private static bool _shutdown;

        private static string _alias;   // Alias of the open device; owned by the worker thread

        /// <summary>Open and start playback. Any previous sound is stopped first, so two tracks can never overlap.
        /// When loop is true the device is asked to repeat forever ('play alias repeat'), which restarts the file
        /// without a close/open gap; LastPlayLooped reports whether the driver accepted it.
        /// Returns false when the device could not be opened or playback did not start.</summary>
        public static bool Play(string audioPath, float volume, bool loop = false)
        {
            bool ok = false;
            RunOnWorker(() => { ok = PlayOnWorker(audioPath, volume, loop); });
            return ok;
        }

        /// <summary>Whether the most recent Play() achieved device-level looping. Reads are only valid after the
        /// enqueueing Play() call has returned, because the flag is written on the audio thread.</summary>
        public static bool LastPlayLooped { get; private set; }

        /// <summary>Position inside the file in seconds, or -1 when no device is open. While looping this is the
        /// position within the current repetition, which is what the timeline-node stop policy needs.</summary>
        public static float GetPositionSeconds()
        {
            float pos = -1f;
            RunOnWorker(() => { pos = PositionOnWorker(); });
            return pos;
        }

        /// <summary>Stop and release the current device. Closing (not just stopping) is what guarantees the next
        /// Play() cannot overlap with this one.</summary>
        public static void Stop(string audioPath)
        {
            RunOnWorker(() => StopOnWorker(audioPath));
        }

        /// <summary>Release the worker thread on mod unload. Safe to call when nothing ever played.</summary>
        public static void Shutdown()
        {
            lock (_gate)
            {
                if (_worker == null) return;
                _shutdown = true;
                Monitor.Pulse(_gate);
            }
            try { _worker?.Join(2000); } catch { /* shutting down anyway */ }
            lock (_gate)
            {
                _worker = null;
                _shutdown = false;
                _queue.Clear();
            }
        }

        private static void RunOnWorker(Action action)
        {
            var job = new Job { Run = action, Done = new ManualResetEventSlim(false) };
            lock (_gate)
            {
                EnsureWorkerLocked();
                _queue.Enqueue(job);
                Monitor.Pulse(_gate);
            }
            if (!job.Done.Wait(CommandTimeoutMs))
                Log.Warning("[rainylove] MCI 命令超时（" + CommandTimeoutMs + "ms），播放线程可能被阻塞。");
        }

        private static void EnsureWorkerLocked()
        {
            if (_worker != null) return;
            _shutdown = false;
            _worker = new Thread(WorkerLoop, 1024 * 1024)
            {
                IsBackground = true,   // never keep the game process alive
                Name = "RainyLove.MciAudio"
            };
            // The apartment must be chosen BEFORE the thread starts: once it runs, the apartment is fixed and
            // CoInitializeEx can no longer switch it (measured: calling CoInitializeEx(STA) inside the thread
            // returns RPC_E_CHANGED_MODE because managed threads start as MTA).
            try { _worker.SetApartmentState(ApartmentState.STA); }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 无法把播放线程设为 STA（" + e.GetType().Name + "），MP3 可能打不开。");
            }
            _worker.Start();
        }

        /// <summary>Worker thread body: STA apartment + FIFO command queue. Single-threaded by construction, so
        /// MCI device state needs no locking and every command reaches the device in call order.</summary>
        private static void WorkerLoop()
        {
            // STA is mandatory for the mpegvideo driver (see the class comment). SetApartmentState already did
            // the work; this call is the fallback for hosts where that was unavailable, and its result is only
            // reported so a wrong apartment shows up in the log instead of as silent 266 errors.
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            if (hr == unchecked((int)0x80010106))
                Log.Warning("[rainylove] 播放线程当前是 MTA（RPC_E_CHANGED_MODE），系统 MP3 解码器将无法打开。");
            else if (hr < 0)
                Log.Warning("[rainylove] MCI 线程 STA 初始化失败：0x" + hr.ToString("X8"));

            try
            {
                while (true)
                {
                    Job job;
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_shutdown) Monitor.Wait(_gate);
                        if (_queue.Count == 0 && _shutdown) break;
                        job = _queue.Dequeue();
                    }
                    try { job.Run(); }
                    catch (Exception e) { Log.Warning("[rainylove] MCI 命令异常：" + e.GetType().Name + " - " + e.Message); }
                    finally { job.Done.Set(); }
                }
            }
            finally
            {
                if (_alias != null) CloseDevice();
                if (hr >= 0) CoUninitialize();
            }
        }

        private static bool PlayOnWorker(string audioPath, float volume, bool loop)
        {
            // Enforce the single-device invariant before touching the new file.
            CloseDevice();
            LastPlayLooped = false;

            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
            {
                Log.Warning("[rainylove] 系统音频播放失败：音频文件不存在 → "
                            + (string.IsNullOrEmpty(audioPath) ? "(路径为空)" : audioPath));
                return false;
            }

            if (!AudioLoader.IsMp3(audioPath))
            {
                Log.Warning("[rainylove] 系统音频播放失败：只支持 MP3 → " + audioPath);
                return false;
            }

            string alias = "rlm" + Guid.NewGuid().ToString("N").Substring(0, 8);

            int rc = Send("open \"" + audioPath + "\" type " + Device + " alias " + alias);
            if (rc != 0)
            {
                Log.Warning("[rainylove] MCI 打开失败（" + Device + "）：" + Describe(rc) + " → " + audioPath);
                return false;
            }

            _alias = alias;

            // Position queries (used by the loop + timeline-node stop policy) need a stable unit.
            int trc = Send("set " + alias + " time format milliseconds");
            if (trc != 0)
                Log.Warning("[rainylove] MCI 设置时间格式失败：" + Describe(trc));

            float v = volume < 0f ? 0f : (volume > 1f ? 1f : volume);
            int level = (int)Math.Round(v * 1000f);
            int vrc = Send("setaudio " + alias + " volume to " + level.ToString(CultureInfo.InvariantCulture));
            if (vrc != 0)
                Log.Warning("[rainylove] MCI 设置音量失败（" + level + "）：" + Describe(vrc));

            rc = Send("play " + alias + (loop ? " repeat" : ""));
            if (rc != 0 && loop)
            {
                // The mpegvideo driver does support 'repeat', so this means the driver or the file is odd. Play
                // the track once instead of going silent, and let the channel fall back to single-shot playback.
                Log.Warning("[rainylove] 系统音频驱动未接受循环播放：" + Describe(rc) + "，本次按单次播放。");
                rc = Send("play " + alias);
            }
            else if (loop)
            {
                LastPlayLooped = true;
            }

            if (rc != 0)
            {
                Log.Warning("[rainylove] MCI 播放失败：" + Describe(rc) + " → " + audioPath);
                CloseDevice();
                return false;
            }

            Log.Info("[rainylove] 系统音频播放已发起" + (LastPlayLooped ? "（循环）" : "") + "：" + audioPath);
            return true;
        }

        /// <summary>Read the current position from the device. Runs on the audio thread, so it needs its own reply
        /// buffer (Send() discards the text).</summary>
        private static float PositionOnWorker()
        {
            if (_alias == null) return -1f;
            try
            {
                var reply = new StringBuilder(256);
                int rc = mciSendString("status " + _alias + " position", reply, reply.Capacity, IntPtr.Zero);
                if (rc != 0) return -1f;
                if (!int.TryParse(reply.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                    return -1f;
                return ms / 1000f;
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] MCI 读取播放位置异常：" + e.GetType().Name + " - " + e.Message);
                return -1f;
            }
        }

        private static void StopOnWorker(string audioPath)
        {
            if (_alias == null)
            {
                Log.Info("[rainylove] 系统音频已中断：" + (audioPath ?? "") + "（当前没有播放）");
                return;
            }
            Send("stop " + _alias);
            CloseDevice();
            Log.Info("[rainylove] 系统音频已中断：" + (audioPath ?? ""));
        }

        /// <summary>Close the device if open. Errors are logged but never thrown: an already-closed alias is normal.</summary>
        private static void CloseDevice()
        {
            string alias = _alias;
            _alias = null;
            if (alias == null) return;
            int rc = Send("close " + alias);
            if (rc != 0) Log.Info("[rainylove] MCI 关闭提示：" + Describe(rc));
        }

        private static int Send(string command)
        {
            try
            {
                // Always pass a real buffer: "open" returns the device id as text, so the reply buffer is used
                // even for commands whose return value we ignore.
                var reply = new StringBuilder(256);
                return mciSendString(command, reply, reply.Capacity, IntPtr.Zero);
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] MCI 调用异常：" + e.GetType().Name + " - " + e.Message);
                return -1;
            }
        }

        /// <summary>Translate an MCI error code into readable text for the log.</summary>
        private static string Describe(int code)
        {
            if (code == 0) return "成功";
            string text = null;
            try
            {
                var sb = new StringBuilder(256);
                if (mciGetErrorString(code, sb, sb.Capacity)) text = sb.ToString();
            }
            catch { /* fall through to the numeric code */ }
            string codeText = code.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(text) ? "错误码 " + codeText : code + " - " + text;
        }
    }
}
