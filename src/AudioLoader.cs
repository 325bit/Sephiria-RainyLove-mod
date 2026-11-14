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
    /// Two source formats are supported:
    ///   - .mp3 : handed to MCI as-is; volume is applied by the MCI audio device (setaudio volume).
    ///   - .wav : MCI's waveaudio device does NOT support setaudio (verified: MCIERR_UNSUPPORTED_FUNCTION),
    ///           so the volume is baked into a temporary PCM WAV in pure C# instead.
    ///
    /// Every playable file must be 16-bit PCM when it is a .wav (the renderer only handles that); the packaged
    /// audio is MP3, so the temporary-file path is only used for user-supplied WAV overrides.
    /// </summary>
    public static class AudioLoader
    {
        // Render cache for the WAV path: key (source file | volume) -> temporary WAV path.
        private static readonly Dictionary<string, string> _renderCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, KeyValuePair<string, string>> _bySource
            = new Dictionary<string, KeyValuePair<string, string>>();

        private static bool IsMp3(string path)
        {
            return path != null && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resolve a source file into something the player can actually play.
        /// For .mp3 the source path is returned untouched and playVolume is the volume to apply at playback time.
        /// For .wav the volume is baked into a temporary PCM WAV and playVolume is 1 (the waveaudio device has no
        /// volume control). Returns false only when the source file is missing or unreadable.</summary>
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

            if (IsMp3(srcPath))
            {
                // No rendering: MCI decodes the MP3 itself and the device applies the volume.
                playPath = srcPath;
                playVolume = vol;
                Log.Info("[rainylove] 音频就绪（MCI 直接播放 MP3）：" + srcPath
                                   + "，音量 " + vol.ToString("F2", CultureInfo.InvariantCulture));
                return true;
            }

            // .wav: bake the volume into a temporary PCM WAV, because waveaudio ignores setaudio.
            if (vol >= 0.999f)
            {
                playPath = srcPath;
                playVolume = 1f;
                Log.Info("[rainylove] 音频就绪（MCI 直接播放 WAV，无音量处理）：" + srcPath);
                return true;
            }

            if (RenderProcessedWav(srcPath, vol, out string rendered))
            {
                playPath = rendered;
                playVolume = 1f;
                Log.Info("[rainylove] 已渲染临时 WAV：音量=" + vol.ToString("F2", CultureInfo.InvariantCulture)
                                   + " → " + rendered);
                return true;
            }

            Log.Warning("[rainylove] 音量渲染失败，将直接播放原始 WAV（音量按 100%）：" + srcPath);
            playPath = srcPath;
            playVolume = 1f;
            return true;
        }

        /// <summary>Scale srcWav's 16-bit PCM samples by vol (0~1) and write the result to a temporary WAV.
        /// Pure C#, no external decoder. Standard 16-bit PCM only; other formats return false so the caller can
        /// fall back to the original file. Cached by (source file + volume).</summary>
        private static bool RenderProcessedWav(string srcWav, float vol, out string outWav)
        {
            outWav = null;
            try
            {
                string key = srcWav + "|" + vol.ToString("F3", CultureInfo.InvariantCulture);
                if (_renderCache.TryGetValue(key, out string cached)
                    && !string.IsNullOrEmpty(cached) && File.Exists(cached))
                {
                    outWav = cached;
                    return true;
                }

                byte[] data = File.ReadAllBytes(srcWav);
                // Validate RIFF/WAVE
                if (data.Length < 12
                    || BitConverter.ToUInt32(data, 0) != 0x46464952u   // "RIFF"
                    || BitConverter.ToUInt32(data, 8) != 0x45564157u)  // "WAVE"
                    return false;

                int pos = 12;
                int fmtBody = -1, dataBody = -1, dataLen = 0;
                while (pos + 8 <= data.Length)
                {
                    uint id = BitConverter.ToUInt32(data, pos);
                    int size = BitConverter.ToInt32(data, pos + 4);
                    if (size < 0) return false;
                    int body = pos + 8;
                    if (id == 0x20746D66u) fmtBody = body;        // "fmt "
                    else if (id == 0x61746164u) { dataBody = body; dataLen = size; } // "data"
                    pos = body + size + (size & 1);              // Chunks are word-aligned
                }
                if (fmtBody < 0 || dataBody < 0) return false;

                short formatTag = BitConverter.ToInt16(data, fmtBody);
                short channels = BitConverter.ToInt16(data, fmtBody + 2);
                int sampleRate = BitConverter.ToInt32(data, fmtBody + 4);
                short bits = BitConverter.ToInt16(data, fmtBody + 14);
                if (formatTag != 1 || bits != 16 || channels <= 0)
                {
                    Log.Warning("[rainylove] 音量处理：仅支持 16-bit PCM WAV，当前格式不支持，回退原文件。");
                    return false;
                }

                int frameBytes = channels * 2;
                int end = dataBody + dataLen;
                if (end > data.Length) end = data.Length;
                long totalFrames = (end - dataBody) / frameBytes;
                if (totalFrames <= 0) return false;

                // Copy the whole file and rewrite only the 16-bit samples in the data chunk
                byte[] outBuf = (byte[])data.Clone();
                for (long frame = 0; frame < totalFrames; frame++)
                {
                    int baseIndex = dataBody + (int)(frame * frameBytes);
                    for (int c = 0; c < channels; c++)
                    {
                        int i = baseIndex + c * 2;
                        if (i + 1 >= end) break;
                        int s = (short)(outBuf[i] | (outBuf[i + 1] << 8));
                        int v = (int)(s * vol);
                        if (v > 32767) v = 32767;
                        else if (v < -32768) v = -32768;
                        outBuf[i] = (byte)(v & 0xFF);
                        outBuf[i + 1] = (byte)((v >> 8) & 0xFF);
                    }
                }

                // Only evict the old file for 'the same key' (a re-render is needed only when parameters change);
                // never delete a temp file another source is using.
                if (_renderCache.TryGetValue(key, out string stale)) DeleteCacheFile(stale);

                // Evict the previous copy of the same source (reached only after a parameter change)
                if (_bySource.TryGetValue(srcWav, out KeyValuePair<string, string> prev) && prev.Value != null)
                {
                    if (prev.Value != stale) DeleteCacheFile(prev.Value);
                    _renderCache.Remove(prev.Key);
                }

                string tmp = Path.Combine(Path.GetTempPath(), "rainylove_mix_" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(tmp, outBuf);
                _renderCache[key] = tmp;
                _bySource[srcWav] = new KeyValuePair<string, string>(key, tmp);
                outWav = tmp;
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] 音量渲染异常：" + e.GetType().Name + " - " + e.Message);
                return false;
            }
        }

        /// <summary>Delete the previous temporary WAV. Deletion fails while a player still holds the file; Windows
        /// reclaims it after the handle is released, so ignore the error. Called only just before writing a new file.</summary>
        private static void DeleteCacheFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* in use, skip */ }
        }

        /// <summary>Duration of a prepared audio file in seconds; 0 when unknown.
        /// Used by the node scheduler (end-of-track node) and by the channel's natural-end timer: MCI has no
        /// blocking 'finished' call, and its own length query is unreliable for MP3 (measured 674907 ms for a
        /// 219370 ms file), so the duration is read from the file itself.</summary>
        public static float GetAudioDuration(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0f;
            if (IsMp3(path)) return Mp3Duration.GetSeconds(path);
            return GetWavDuration(path);
        }

        /// <summary>Read WAV duration (seconds). Standard PCM only (format=1, 16-bit). Returns 0 on failure.</summary>
        public static float GetWavDuration(string wavPath)
        {
            try
            {
                using (var fs = File.OpenRead(wavPath))
                {
                    byte[] buf = new byte[8192];
                    int read = fs.Read(buf, 0, buf.Length);
                    if (read < 12 || BitConverter.ToUInt32(buf, 0) != 0x46464952u) return 0f; // RIFF

                    int pos = 12;
                    int sampleRate = 0, channels = 0, bits = 0, dataSize = -1;
                    while (pos + 8 <= read)
                    {
                        uint id = BitConverter.ToUInt32(buf, pos);
                        int size = BitConverter.ToInt32(buf, pos + 4);
                        int body = pos + 8;
                        if (id == 0x20746D66u) // "fmt "
                        {
                            if (body + 16 <= read)
                            {
                                channels = BitConverter.ToInt16(buf, body + 2);
                                sampleRate = BitConverter.ToInt32(buf, body + 4);
                                bits = BitConverter.ToInt16(buf, body + 14);
                            }
                        }
                        else if (id == 0x61746164u) // "data"
                        {
                            dataSize = size;
                            break;
                        }
                        if (size < 0) break;
                        pos = body + size + (size & 1);
                    }

                    if (dataSize < 0 || sampleRate <= 0 || channels <= 0 || bits <= 0) return 0f;
                    int bytesPerSec = sampleRate * channels * (bits / 8);
                    if (bytesPerSec <= 0) return 0f;
                    return (float)dataSize / bytesPerSec;
                }
            }
            catch
            {
                return 0f;
            }
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

        /// <summary>Whether the most recent Play() achieved device-level looping. The mpegvideo (MP3) driver honours
        /// the 'repeat' flag; the waveaudio driver rejects it with MCIERR_UNSUPPORTED_FUNCTION (259), so a WAV falls
        /// back to a single-shot playback. Written on the audio thread, read from the main thread only after the
        /// enqueueing call has returned.</summary>
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

            string alias = "rlm" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string device = audioPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "mpegvideo" : "waveaudio";

            int rc = Send("open \"" + audioPath + "\" type " + device + " alias " + alias);
            if (rc != 0)
            {
                Log.Warning("[rainylove] MCI 打开失败（" + device + "）：" + Describe(rc) + " → " + audioPath);
                return false;
            }

            _alias = alias;

            // Position queries (used by the loop + timeline-node stop policy) need a stable unit.
            int trc = Send("set " + alias + " time format milliseconds");
            if (trc != 0)
                Log.Warning("[rainylove] MCI 设置时间格式失败：" + Describe(trc));

            // The waveaudio device rejects setaudio (MCIERR_UNSUPPORTED_FUNCTION); WAV volume is baked instead.
            if (device == "mpegvideo")
            {
                float v = volume < 0f ? 0f : (volume > 1f ? 1f : volume);
                int level = (int)Math.Round(v * 1000f);
                int vrc = Send("setaudio " + alias + " volume to " + level.ToString(CultureInfo.InvariantCulture));
                if (vrc != 0)
                    Log.Warning("[rainylove] MCI 设置音量失败（" + level + "）：" + Describe(vrc));
            }

            rc = Send("play " + alias + (loop ? " repeat" : ""));
            if (rc != 0 && loop)
            {
                // Only the mpegvideo driver supports 'repeat'. A WAV (or a bitten driver) still has to make a
                // sound, so retry once without the flag and let the channel fall back to single-shot playback.
                Log.Warning("[rainylove] 系统音频不支持循环播放（仅 MP3 支持）：" + Describe(rc) + "，本次按单次播放。");
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
