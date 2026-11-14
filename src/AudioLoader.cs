using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Audio loading for the official Workshop build.
    ///
    /// Playback goes through the Windows system audio API (winmm PlaySound) because this game's Unity audio
    /// pipeline is unreliable. Only 16-bit PCM WAV files are accepted: the old ffmpeg/HTTP-server fallback paths
    /// were removed because external process execution and a local TCP server are prohibited by the Workshop review
    /// system (they would trip the "out-of-Workshop code execution" / network warnings).
    ///
    /// Volume scaling is baked into a temporary WAV in pure C# (no external dependency). The packaged WAV is used
    /// directly when volume is effectively 1 and no fade is applied.
    /// </summary>
    public static class AudioLoader
    {
        // Render cache: key (source file | volume | fade) -> temporary WAV path. Separated by source so the two
        // independent audio sources never delete each other's temp file.
        private static readonly Dictionary<string, string> _renderCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, KeyValuePair<string, string>> _bySource
            = new Dictionary<string, KeyValuePair<string, string>>();

        /// <summary>Prepare a WAV file path that system audio can play directly.
        /// 0) Volume ~1: play the original packaged WAV directly (no copy at all).
        /// 1) Otherwise: bake a temporary WAV (pure C#) with volume scaling.
        /// When true is returned, outWav points to a valid file.</summary>
        public static bool TryPrepareWav(string wavPath, float volume, float fadeInSeconds, float fadeOutSeconds, out string outWav)
        {
            outWav = null;
            if (string.IsNullOrEmpty(wavPath)) return false;

            float vol = volume < 0f ? 0f : (volume > 1f ? 1f : volume);
            float fadeIn = fadeInSeconds < 0f ? 0f : fadeInSeconds;
            float fadeOut = fadeOutSeconds < 0f ? 0f : fadeOutSeconds;

            if (File.Exists(wavPath))
            {
                if (vol >= 0.999f && fadeIn <= 0.001f && fadeOut <= 0.001f)
                {
                    outWav = wavPath;
                    Log.Info("[rainylove] 使用打包 WAV（无音量处理）：" + wavPath);
                    return true;
                }

                if (RenderProcessedWav(wavPath, vol, fadeIn, fadeOut, out outWav))
                {
                    Log.Info(string.Format(CultureInfo.InvariantCulture,
                        "[rainylove] 已渲染临时 WAV：音量={0:F2}，淡入={1:F1}s，淡出={2:F1}s → {3}",
                        vol, fadeIn, fadeOut, outWav));
                    return true;
                }

                Log.Warning("[rainylove] 音量渲染失败，将直接播放打包的全音量 WAV：" + wavPath);
                outWav = wavPath;
                return true;
            }

            Log.Warning("[rainylove] 找不到音频文件（需要 rainylove.wav / AIZO.wav）：" + wavPath);
            return false;
        }

        /// <summary>Scale srcWav's 16-bit PCM samples by vol (0~1) and apply fadeInSec / fadeOutSec linear envelopes,
        /// write the result to a temporary WAV and return its path. Pure C#, no ffmpeg. Standard 16-bit PCM only;
        /// other formats return false so the caller can fall back. Cached by (source file + parameters).</summary>
        private static bool RenderProcessedWav(string srcWav, float vol, float fadeInSec, float fadeOutSec, out string outWav)
        {
            outWav = null;
            try
            {
                string key = srcWav
                             + "|" + vol.ToString("F3", CultureInfo.InvariantCulture)
                             + "|" + fadeInSec.ToString("F2", CultureInfo.InvariantCulture)
                             + "|" + fadeOutSec.ToString("F2", CultureInfo.InvariantCulture);
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

                // Number of frames used by fade-in / fade-out (capped at total length)
                int fadeInFrames = (int)Math.Min(totalFrames, Math.Round(fadeInSec * sampleRate));
                int fadeOutFrames = (int)Math.Min(totalFrames, Math.Round(fadeOutSec * sampleRate));

                // Copy the whole file and rewrite only the 16-bit samples in the data chunk
                byte[] outBuf = (byte[])data.Clone();
                for (long frame = 0; frame < totalFrames; frame++)
                {
                    float gain = vol;
                    if (fadeInFrames > 0 && frame < fadeInFrames)
                        gain *= (frame + 1f) / fadeInFrames;          // 0 -> vol linear fade-in
                    long framesFromEnd = totalFrames - 1 - frame;
                    if (fadeOutFrames > 0 && framesFromEnd < fadeOutFrames)
                        gain *= (framesFromEnd + 1f) / fadeOutFrames; // vol -> 0 linear fade-out

                    int baseIndex = dataBody + (int)(frame * frameBytes);
                    for (int c = 0; c < channels; c++)
                    {
                        int i = baseIndex + c * 2;
                        if (i + 1 >= end) break;
                        int s = (short)(outBuf[i] | (outBuf[i + 1] << 8));
                        int v = (int)(s * gain);
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

        /// <summary>Delete the previous temporary WAV. Deletion fails while PlaySound holds the file; Windows reclaims it
        /// after the handle is released, so ignore the error. Called only just before writing a new file.</summary>
        private static void DeleteCacheFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* in use, skip */ }
        }

        /// <summary>Read WAV duration (seconds). Standard PCM only (format=1, 16-bit). Returns 0 on failure.
        /// Used to reset state after test playback ends naturally: winmm PlaySound is async and has no completion callback.</summary>
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

    /// <summary>Windows system audio playback (winmm PlaySound), fully bypassing the broken Unity audio pipeline of this build.
    /// Outputs directly to the Windows default device, with no dependency on Unity AudioClip / AudioSource / UnityWebRequest.</summary>
    internal static class SystemAudio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_FILENAME = 0x00020000;
        private const uint SND_ASYNC = 0x00000001;
        private const uint SND_NODEFAULT = 0x00000002;
        private const uint SND_PURGE = 0x00000040;

        public static void Play(string wavPath)
        {
            // Always report a missing file: this used to return silently,
            // causing 'clicked play, log says playback started, but no sound at all', which was very hard to diagnose.
            if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
            {
                Log.Warning("[rainylove] 系统音频播放失败：音频文件不存在 → "
                            + (string.IsNullOrEmpty(wavPath) ? "(路径为空)" : wavPath));
                return;
            }
            try
            {
                PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                Log.Info("[rainylove] 系统音频播放已发起：" + wavPath);
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] PlaySound 调用失败：" + e.GetType().Name + " - " + e.Message);
            }
        }

        /// <summary>Interrupt current playback (stop all async PlaySound sounds in this process).
        /// Key point: stopping requires pszSound=NULL + SND_PURGE; never pass a file name with SND_ASYNC + SND_PURGE -
        /// on Windows that first stops the old instance and then asynchronously replays the same file, showing up as 'replay' instead of stop.
        /// This mod is the only user of winmm PlaySound in this game, so stopping all async sounds is safe and equivalent.</summary>
        public static void Stop(string wavPath)
        {
            try
            {
                // pszSound=NULL + SND_PURGE: stop every async sound in the calling process without replaying anything.
                PlaySound(null, IntPtr.Zero, SND_PURGE | SND_ASYNC);
                Log.Info("[rainylove] 系统音频已中断：" + (wavPath ?? ""));
            }
            catch (Exception e)
            {
                Log.Warning("[rainylove] PlaySound 停止失败：" + e.GetType().Name + " - " + e.Message);
            }
        }
    }
}
