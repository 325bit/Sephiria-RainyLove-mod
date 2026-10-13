using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// 把磁盘上的音频文件变成可播放的 AudioClip。
    ///
    /// 加载路线（按可靠性排序，首个成功即采用）：
    ///   1) UnityWebRequest 原生解码（file:// 三段式 URL）：MP3 失败则 ffmpeg 转 WAV 再 UWR(WAV)。
    ///   2) ffmpeg 解 PCM + AudioClip.Create + SetData（多组合试错）：
    ///      本构建里 AudioClip.Create 的 bool 参数虽反编译名为 stream，但实测 stream:false 时
    ///      SetData 报 "contains no data"，说明它实际是 preloadAudioData，需要 true 才会分配缓冲。
    ///      因此逐一尝试 (5参 / 6参_3D) × (stream=true / false)，首个 samples&gt;0 者采用。
    ///   3) 流式 PCMReaderCallback 兜底（stream:true，Unity 在播放中多次回调喂数据）。
    ///
    /// 注意：AudioClip / AudioSource 在 UnityEngine.AudioModule，与 net472 工程冲突，
    /// 因此一律用运行时反射获取类型，避免编译期直接引用。AudioClip 在这个 Unity 6 构建里
    /// 不一定在 AudioModule 程序集，所以用 FindType 扫描全部已加载程序集。
    /// </summary>
    public static class AudioLoader
    {
        private static Type _audioClipType;
        private static Type _audioSourceType;
        private static MethodInfo _playMethod;
        private static MethodInfo _create5;     // Create(name,len,ch,freq,bool stream)
        private static MethodInfo _create6_3d;  // Create(name,len,ch,freq,bool _3D,bool stream)
        private static MethodInfo _createCb6;   // Create(name,len,ch,freq,bool stream,PCMReaderCallback)
        private static MethodInfo _createCb7;   // Create(name,len,ch,freq,bool stream,PCMReaderCallback,PCMSetPositionCallback)

        // 流式回调喂数据用：整段 PCM 与已喂偏移
        private static float[] _pcmBuffer;
        private static int _pcmOffset;
        private static Delegate _aliveDelegate;   // 持有回调委托引用，防止被 GC 回收导致播放时静音
        private static bool _createDiagLogged;

        private static bool EnsureAudioModule()
        {
            if (_audioClipType != null && _audioSourceType != null) return true;

            _audioClipType = FindType("UnityEngine.AudioClip");
            _audioSourceType = FindType("UnityEngine.AudioSource");
            if (_audioClipType == null || _audioSourceType == null) return false;

            _playMethod = _audioSourceType.GetMethod("Play", Type.EmptyTypes);

            const BindingFlags st = BindingFlags.Static | BindingFlags.Public;
            foreach (var m in _audioClipType.GetMethods(st))
            {
                if (m.Name != "Create") continue;
                var ps = m.GetParameters();
                if (ps.Length == 5 && ps[4].ParameterType == typeof(bool))
                    _create5 = m;
                else if (ps.Length == 6 && ps[4].ParameterType == typeof(bool) && ps[5].ParameterType == typeof(bool))
                    _create6_3d = m;
                else if (ps.Length == 6 && ps[4].ParameterType == typeof(bool)
                         && typeof(MulticastDelegate).IsAssignableFrom(ps[5].ParameterType))
                    _createCb6 = m;
                else if (ps.Length == 7 && ps[4].ParameterType == typeof(bool)
                         && typeof(MulticastDelegate).IsAssignableFrom(ps[5].ParameterType))
                    _createCb7 = m;
            }
            LogCreateOverloads();
            return (_create5 != null || _create6_3d != null || _createCb6 != null || _createCb7 != null)
                   && _playMethod != null;
        }

        private static void LogCreateOverloads()
        {
            if (_createDiagLogged || _audioClipType == null) return;
            _createDiagLogged = true;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public;
            var sb = new System.Text.StringBuilder();
            foreach (var m in _audioClipType.GetMethods(st))
            {
                if (m.Name != "Create") continue;
                var ps = m.GetParameters();
                sb.Clear();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                }
                Plugin.Log.LogInfo("[rainylove][diag] AudioClip.Create(" + sb + ")");
            }
        }

        /// <summary>暴露给外部：拿 UnityEngine.AudioSource 类型（反射获取）。</summary>
        public static Type EnsureAudioSourceType()
        {
            EnsureAudioModule();
            return _audioSourceType;
        }

        /// <summary>反射调用无参的 AudioSource.Play()，避开与 Play(ulong) 的二义性。</summary>
        public static void Play(object source)
        {
            if (!EnsureAudioModule() || _playMethod == null)
                throw new InvalidOperationException("AudioSource 类型/Play 未就绪");
            _playMethod.Invoke(source, null);
        }

        public static void SetProp(object target, string name, object value)
        {
            var p = target.GetType().GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(target, value, null);
        }

        public static object GetProp(object target, string name)
        {
            var p = target.GetType().GetProperty(name);
            return p != null ? p.GetValue(target, null) : null;
        }

        // ===================== UnityWebRequest 原生解码（主路径） =====================
        // 用 Unity 自带的音频解码器读本地文件，绕开本构建里不可靠的 AudioClip.Create/SetData。
        // 关键：Windows 本地文件 URL 必须是 file:/// 三段式，用 System.Uri 规范化，否则 UWR 读不到数据却“成功”。
        private static Type _uwrType;
        private static Type _uwrMultiType;
        private static Type _dhAudioType;
        private static Type _audioTypeEnum;
        private static MethodInfo _getAudioClip;
        private static object _audioTypeMpeg;
        private static object _audioTypeWav;
        private static bool _uwrReady;

        private static bool EnsureUwr()
        {
            if (_uwrReady) return _getAudioClip != null && _audioTypeMpeg != null && _audioTypeWav != null;
            _uwrMultiType = FindType("UnityEngine.Networking.UnityWebRequestMultimedia");
            _uwrType = FindType("UnityEngine.Networking.UnityWebRequest");
            _dhAudioType = FindType("UnityEngine.Networking.DownloadHandlerAudioClip");
            _audioTypeEnum = FindType("UnityEngine.AudioType");
            if (_uwrMultiType == null || _uwrType == null || _dhAudioType == null || _audioTypeEnum == null)
            {
                Plugin.Log.LogWarning(string.Format(
                    "[rainylove] UWR 类型缺失：Multi={0}, UWR={1}, DHA={2}, AudioType={3}",
                    _uwrMultiType != null, _uwrType != null, _dhAudioType != null, _audioTypeEnum != null));
                _uwrReady = true; return false;
            }
            _getAudioClip = _uwrMultiType.GetMethod("GetAudioClip", new[] { typeof(string), _audioTypeEnum });
            _audioTypeMpeg = _audioTypeEnum.GetField("MPEG")?.GetValue(null);
            _audioTypeWav = _audioTypeEnum.GetField("WAV")?.GetValue(null);
            _uwrReady = true;
            return _getAudioClip != null && _audioTypeMpeg != null && _audioTypeWav != null;
        }

        /// <summary>创建原生解码请求（file:// 三段式 URL）。wav=true 用 AudioType.WAV（MP3 解码失败时的兜底）。</summary>
        public static object CreateAudioWebRequest(string path, bool wav)
        {
            if (!EnsureUwr() || _getAudioClip == null) return null;
            try
            {
                string url = new Uri(path).AbsoluteUri;   // F:\x.mp3 -> file:///F:/x.mp3
                object atype = wav ? _audioTypeWav : _audioTypeMpeg;
                Plugin.Log.LogInfo(string.Format("[rainylove] UWR 解码 url={0} (AudioType={1})",
                    url, wav ? "WAV" : "MPEG"));
                return _getAudioClip.Invoke(null, new object[] { url, atype });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] 创建 UWR 失败：" + e.GetType().Name + " - " + e.Message);
                return null;
            }
        }

        /// <summary>发起请求，返回可 yield 的异步操作对象。</summary>
        public static object SendRequest(object req)
        {
            try { return _uwrType.GetMethod("SendWebRequest").Invoke(req, null); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] UWR Send 失败：" + e.Message);
                return null;
            }
        }

        public static bool RequestSucceeded(object req)
        {
            var resultProp = _uwrType.GetProperty("result");
            if (resultProp != null)
            {
                var v = resultProp.GetValue(req, null);
                return v != null && Convert.ToInt32(v) == 0;
            }
            var net = _uwrType.GetProperty("isNetworkError");
            var http = _uwrType.GetProperty("isHttpError");
            bool n = net != null && (bool)net.GetValue(req, null);
            bool h = http != null && (bool)http.GetValue(req, null);
            return !n && !h;
        }

        /// <summary>失败时打印详细诊断：result 枚举值、responseCode、error，便于区分“文件访问被拒”还是“解码失败”。</summary>
        public static void LogRequestDetail(object req, string tag)
        {
            try
            {
                var resultProp = _uwrType.GetProperty("result");
                var codeProp = _uwrType.GetProperty("responseCode");
                var errProp = _uwrType.GetProperty("error");
                var result = resultProp != null ? Convert.ToInt32(resultProp.GetValue(req, null)) : -1;
                var code = codeProp != null ? Convert.ToInt32(codeProp.GetValue(req, null)) : -1;
                var err = errProp != null ? (errProp.GetValue(req, null) as string) : null;
                Plugin.Log.LogWarning(string.Format(
                    "[rainylove] {0} 失败详情：result={1}(0=Success,1=Conn,2=Protocol,3=Data), responseCode={2}, error={3}",
                    tag, result, code, err ?? "<null>"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] 读取 UWR 详情异常：" + e.Message);
            }
        }

        public static string RequestError(object req)
        {
            var p = _uwrType.GetProperty("error");
            return p != null ? p.GetValue(req, null) as string : null;
        }

        public static object ExtractClip(object req)
        {
            try
            {
                var m = _dhAudioType.GetMethod("GetContent", new[] { _uwrType });
                return m == null ? null : m.Invoke(null, new object[] { req });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] UWR 取剪辑失败：" + e.Message);
                return null;
            }
        }

        public static void DisposeRequest(object req)
        {
            try { _uwrType.GetMethod("Dispose").Invoke(req, null); } catch { /* ignore */ }
        }

        // ===================== 本地 HTTP 服务器（主加载路径） =====================
        // 本构建里两条官方本地加载都废了：
        //   - UnityWebRequest 的 file:// 被拒（result=1 ConnectionError，responseCode=200 但取不回数据）；
        //   - AudioClip.Create + SetData 全部 "contains no data"（SetData 写不进缓冲）。
        // 因此用 TcpListener 在 127.0.0.1 起一个极简 HTTP 服务，把音频字节以 http:// 提供给
        // UnityWebRequestMultimedia 原生解码。loopback 用普通 Winsock，不需要管理员权限/URL 保留。
        private static TcpListener _tcp;
        private static Thread _serverThread;
        private static int _port;
        private static string _mp3Url;
        private static string _wavUrl;
        private static byte[] _mp3Data;
        private static byte[] _wavData;
        private static readonly object _dataLock = new object();

        public static void StartLocalServer(string mp3Path)
        {
            if (_tcp != null) return;
            try
            {
                if (!File.Exists(mp3Path))
                {
                    Plugin.Log.LogWarning("[rainylove] 本地服务器：音频文件不存在，跳过：" + mp3Path);
                    return;
                }
                byte[] mp3 = File.ReadAllBytes(mp3Path);
                int port = 8731;
                TcpListener srv = null;
                for (int i = 0; i < 50; i++)
                {
                    try { srv = new TcpListener(IPAddress.Loopback, port); srv.Start(); break; }
                    catch (SocketException) { port++; srv = null; }
                    catch (Exception) { port++; srv = null; }
                }
                if (srv == null) { Plugin.Log.LogWarning("[rainylove] 本地音频服务器：无可用端口"); return; }

                _tcp = srv; _port = port; _mp3Data = mp3;
                _mp3Url = "http://127.0.0.1:" + port + "/rainylove.mp3";
                _wavUrl = "http://127.0.0.1:" + port + "/rainylove.wav";
                _serverThread = new Thread(ServeLoop) { IsBackground = true, Name = "RainyLove.AudioHttp" };
                _serverThread.Start();
                Plugin.Log.LogInfo("[rainylove] 本地音频服务器已启动：" + _mp3Url);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] 本地音频服务器启动失败：" + e.GetType().Name + " - " + e.Message);
            }
        }

        private static void ServeLoop()
        {
            try
            {
                while (_tcp != null)
                {
                    TcpClient client = _tcp.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => { try { HandleClient(client); } catch { } });
                }
            }
            catch { /* 服务器停止 */ }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    ns.ReadTimeout = 5000;
                    // 读到 \r\n\r\n 为止（HTTP 头结束）
                    var acc = new System.Collections.Generic.List<byte>(256);
                    byte[] buf = new byte[4096];
                    int read;
                    bool done = false;
                    while ((read = ns.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++) acc.Add(buf[i]);
                        for (int i = acc.Count - 1; i >= 3; i--)
                        {
                            if (acc[i - 3] == '\r' && acc[i - 2] == '\n' && acc[i - 1] == '\r' && acc[i] == '\n')
                            { done = true; break; }
                        }
                        if (done) break;
                        if (acc.Count > 65536) break;
                    }

                    string req = Encoding.ASCII.GetString(acc.ToArray());
                    bool wantWav = req.IndexOf(".wav", StringComparison.OrdinalIgnoreCase) >= 0;
                    byte[] data; string mime;
                    lock (_dataLock)
                    {
                        if (wantWav) { data = _wavData; mime = "audio/wav"; }
                        else { data = _mp3Data; mime = "audio/mpeg"; }
                    }

                    if (data == null)
                    {
                        byte[] r404 = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        ns.Write(r404, 0, r404.Length);
                        ns.Close();
                        return;
                    }

                    var sb = new StringBuilder();
                    sb.Append("HTTP/1.1 200 OK\r\n");
                    sb.Append("Content-Type: ").Append(mime).Append("\r\n");
                    sb.Append("Content-Length: ").Append(data.LongLength).Append("\r\n");
                    sb.Append("Accept-Ranges: bytes\r\n");
                    sb.Append("Connection: close\r\n");
                    sb.Append("Access-Control-Allow-Origin: *\r\n\r\n");
                    byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
                    ns.Write(header, 0, header.Length);
                    ns.Write(data, 0, data.Length);
                    ns.Close();
                }
            }
            catch { /* 客户端断开等，忽略 */ }
        }

        public static string ServedMp3Url => _mp3Url;
        public static string ServedWavUrl => _wavUrl;

        public static void SetWavData(byte[] wav)
        {
            lock (_dataLock) { _wavData = wav; }
        }

        public static void ClearWavData()
        {
            lock (_dataLock) { _wavData = null; }
        }

        /// <summary>ffmpeg 把源文件转成 WAV 字节（16bit PCM），返回字节数组；失败返回 null。用于 MP3 原生解码失败时的兜底。</summary>
        public static byte[] TranscodeToWavBytes(string path)
        {
            string ffmpeg = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg)) return null;
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Arguments = string.Format(
                "-hide_banner -loglevel error -i \"{0}\" -map 0:a:0 -f wav -acodec pcm_s16le -ar 44100 -ac 2 -",
                path);
            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        proc.StandardOutput.BaseStream.CopyTo(ms);
                        string err = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();
                        if (proc.ExitCode != 0)
                        {
                            Plugin.Log.LogWarning("[rainylove] ffmpeg 转 WAV 失败：\n" + err);
                            return null;
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] ffmpeg 转 WAV 异常：" + e.Message);
                return null;
            }
        }

        /// <summary>ffmpeg 把源文件转成临时 WAV（16bit PCM），返回路径；失败返回 null。用于 MP3 原生解码失败时的兜底。</summary>
        public static string TranscodeToWav(string path)
        {
            string ffmpeg = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg)) return null;
            string tmp = Path.Combine(Path.GetTempPath(), "rainylove_" + Guid.NewGuid().ToString("N") + ".wav");
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Arguments = string.Format(
                "-hide_banner -loglevel error -y -i \"{0}\" -map 0:a:0 -acodec pcm_s16le -ar 44100 -ac 2 \"{1}\"",
                path, tmp);
            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return null;
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0 || !File.Exists(tmp)) return null;
                    return tmp;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] ffmpeg 转 WAV 异常：" + e.Message);
                return null;
            }
        }

        // ===================== ffmpeg -> WAV（系统音频播放用） =====================
        //
        // 本构建里 Unity 的 AudioClip.Create/SetData 与 UnityWebRequest 全部失效
        // （UWR 连本地 200 响应都报 ConnectionError），故放弃在 Unity 内部造 AudioClip，
        // 改成用 ffmpeg 把源文件解码成 WAV，交给 Windows 系统音频 API（winmm PlaySound）直接播放。
        private static string _lastCacheWav;

        /// <summary>准备一个可直接被系统音频播放的 WAV 文件路径：
        ///   入参 wavPath 即打包好的 rainylove.wav（由 Plugin.ResolveAudioPath 直接解析，不再依赖 mp3）。
        ///   0) WAV 存在：默认音量(≈1.0)直接用它；否则用纯 C# 缩放其 PCM 采样成临时 WAV（无需 ffmpeg）。
        ///   1) WAV 缺失但有同目录 mp3 且本机有 ffmpeg：ffmpeg 解码兜底（仅兼容老安装，非必需）。
        /// 返回 true 时 out outWav 为有效文件。</summary>
        public static bool TryPrepareWav(string wavPath, float volume, out string outWav)
        {
            outWav = null;
            if (string.IsNullOrEmpty(wavPath)) return false;

            // 主路径：打包 WAV 直接存在，完全不碰 ffmpeg（便于 mod 分发，无需用户安装 ffmpeg）。
            if (File.Exists(wavPath))
            {
                float vol = volume < 0f ? 0f : (volume > 1f ? 1f : volume);
                if (vol >= 0.999f)
                {
                    outWav = wavPath;
                    Plugin.Log.LogInfo("[rainylove] 使用打包 WAV（跳过 ffmpeg）：" + wavPath);
                    return true;
                }
                // 音量被改过：用纯 C# 缩放打包 WAV 的 16-bit PCM 采样生成临时 WAV 播放（无需 ffmpeg）。
                if (ScaleWavVolume(wavPath, vol, out outWav))
                {
                    Plugin.Log.LogInfo("[rainylove] 已按音量 " + vol.ToString("F2", CultureInfo.InvariantCulture)
                                       + " 缩放为临时 WAV：" + outWav);
                    return true;
                }
                // 缩放失败（非 16-bit PCM 等少见情况）：退而用全音量打包 WAV 并告警
                Plugin.Log.LogWarning("[rainylove] 音量缩放失败，将直接播放打包的全音量 WAV：" + wavPath);
                outWav = wavPath;
                return true;
            }

            // 兜底（极少用）：WAV 缺失但同目录有 mp3 且本机有 ffmpeg —— 解码 mp3 兜底，保留老安装兼容。
            string dir = Path.GetDirectoryName(wavPath) ?? string.Empty;
            string mp3Sibling = Path.Combine(dir, "rainylove 1.03x.mp3");
            if (File.Exists(mp3Sibling) && HasFfmpeg())
            {
                Plugin.Log.LogInfo("[rainylove] 未找到打包 WAV，使用 ffmpeg 解码同目录 mp3 兜底。");
                return TryFfmpegToWav(mp3Sibling, volume, out outWav);
            }

            Plugin.Log.LogWarning("[rainylove] 找不到音频文件（需要 rainylove.wav）：" + wavPath);
            return false;
        }

        /// <summary>把 srcWav 的 16-bit PCM 采样整体乘以 vol(0~1) 后写入临时 WAV，返回临时文件路径。
        /// 纯 C# 实现，不依赖 ffmpeg。仅支持标准 PCM（format=1, 16bit），其它格式返回 false 交由调用方回退。
        /// 这样即便用户机器上没有 ffmpeg，改 Volume 配置也能真正生效，且打包 WAV 仍可随 mod 分发。</summary>
        private static bool ScaleWavVolume(string srcWav, float vol, out string outWav)
        {
            outWav = null;
            try
            {
                byte[] data = File.ReadAllBytes(srcWav);
                // 校验 RIFF/WAVE
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
                    pos = body + size + (size & 1);              // chunk 按字对齐
                }
                if (fmtBody < 0 || dataBody < 0) return false;

                short formatTag = BitConverter.ToInt16(data, fmtBody);
                short bits = BitConverter.ToInt16(data, fmtBody + 14);
                if (formatTag != 1 || bits != 16)
                {
                    Plugin.Log.LogWarning("[rainylove] 音量缩放：仅支持 16-bit PCM WAV，当前格式不支持，回退全音量。");
                    return false;
                }

                // 复制整个文件，仅缩放 data 段的 16-bit 样本
                byte[] outBuf = (byte[])data.Clone();
                int end = dataBody + dataLen;
                if (end > outBuf.Length) end = outBuf.Length;
                for (int i = dataBody; i + 1 < end; i += 2)
                {
                    int s = BitConverter.ToInt16(outBuf, i);
                    int v = (int)(s * vol);
                    if (v > 32767) v = 32767;
                    else if (v < -32768) v = -32768;
                    outBuf[i] = (byte)(v & 0xFF);
                    outBuf[i + 1] = (byte)((v >> 8) & 0xFF);
                }

                if (_lastCacheWav != null)
                {
                    try { if (File.Exists(_lastCacheWav)) File.Delete(_lastCacheWav); } catch { /* 占用中，跳过 */ }
                }
                string tmp = Path.Combine(Path.GetTempPath(), "rainylove_vol_" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(tmp, outBuf);
                _lastCacheWav = tmp;
                outWav = tmp;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] 音量缩放异常：" + e.GetType().Name + " - " + e.Message);
                return false;
            }
        }

        /// <summary>用 ffmpeg 把 mp3 解码（按 vol 调音量）为临时 WAV 缓存；失败返回 false。</summary>
        private static bool TryFfmpegToWav(string mp3Path, float vol, out string wavPath)
        {
            wavPath = null;
            string ffmpeg = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg)) return false;

            // 删除上一次缓存（若正被 PlaySound 占用则忽略错误，Windows 会延后删除）
            if (_lastCacheWav != null)
            {
                try { if (File.Exists(_lastCacheWav)) File.Delete(_lastCacheWav); } catch { /* 占用中，跳过 */ }
            }
            string cache = Path.Combine(Path.GetTempPath(), "rainylove_cache_" + Guid.NewGuid().ToString("N") + ".wav");
            _lastCacheWav = cache;

            string volArg = vol >= 0.999f ? "" : string.Format(CultureInfo.InvariantCulture, " -af volume={0:F2}", vol);

            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = string.Format(CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error -y -i \"{0}\" -map 0:a:0 -acodec pcm_s16le -ar 44100 -ac 2{1} \"{2}\"",
                    mp3Path, volArg, cache)
            };
            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    string err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0 || !File.Exists(cache))
                    {
                        Plugin.Log.LogWarning("[rainylove] ffmpeg 解码为 WAV 失败：\n" + err);
                        return false;
                    }
                }
                wavPath = cache;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] ffmpeg 调用异常：" + e.GetType().Name + " - " + e.Message);
                return false;
            }
        }

        // ===================== ffmpeg -> PCM -> AudioClip（旧兜底，本构建已失效，仅留作参考） =====================
        //
        // 主路径：Create(stream:false) + SetData(pcm,0) —— Unity 标准、跨版本最稳。
        //   （初版"SetData contains no data"是因为误用了 stream:true；stream:false 时 SetData 正常写入。）
        // 兜底：  Create(stream:true) + PCMReaderCallback —— 流式接口，回调内渐进喂数据。
        //   （stream:false 时 Unity 根本不回调，故回调式只能用 stream:true。）

        /// <summary>最后兜底：ffmpeg 解 PCM + 流式 PCMReaderCallback 造 AudioClip。
        /// 注意：本构建里 AudioClip.Create+SetData 全部失效（"contains no data"），file:// UWR 被拒，
        /// 故仅在本地 HTTP 服务器 + UWR 原生解码也失败时才走到这里。委托会存入静态字段防止被 GC 回收。</summary>
        public static bool TryLoadClip(string path, out object clip)
        {
            clip = null;
            float[] pcm; int channels; int sampleRate;
            if (!TryDecodeToPcm(path, out pcm, out channels, out sampleRate)) return false;
            if (pcm == null || pcm.Length == 0) return false;

            int lengthSamples = pcm.Length / channels;
            if (lengthSamples <= 0 || !EnsureAudioModule()) return false;

            // ---- 兜底：流式回调（stream:true，Unity 在播放中多次回调喂数据） ----
            MethodInfo cb = _createCb6 ?? _createCb7;
            if (cb != null)
            {
                try
                {
                    var ps = cb.GetParameters();
                    var delType = ps[5].ParameterType; // PCMReaderCallback：delegate void(float[])
                    var mi = typeof(AudioLoader).GetMethod("PcmReaderCallback",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    var del = Delegate.CreateDelegate(delType, mi);
                    _aliveDelegate = del; // 防止被 GC 回收导致播放时回调消失 -> 静音

                    _pcmBuffer = pcm;
                    _pcmOffset = 0;
                    object created = cb.Invoke(null, BuildCbArgs(cb, "rainylove", lengthSamples, channels, sampleRate, del));
                    Plugin.Log.LogInfo(string.Format(
                        "[rainylove] 使用流式回调兜底（重载={0}参）：lengthSamples={1}, pcm.Length={2}",
                        cb.GetParameters().Length, lengthSamples, pcm.Length));
                    clip = created; // 流式 clip 即便此刻 samples 读 0 也接受，播放时由回调喂入
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[rainylove] 创建流式 AudioClip 失败：" + e.GetType().Name + " - " + e.Message);
                }
            }

            Plugin.Log.LogWarning("[rainylove] 无可用 AudioClip.Create 重载，音频加载失败。");
            return false;
        }

        private static object[] BuildCbArgs(MethodInfo m, string name, int len, int ch, int hz, Delegate del)
        {
            var ps = m.GetParameters();
            if (ps.Length == 6)
                return new object[] { name, len, ch, hz, true, del };
            // 7 参：补 null 作为 PCMSetPositionCallback
            return new object[] { name, len, ch, hz, true, del, null };
        }

        private static int ReadSamples(object clip)
        {
            var p = _audioClipType.GetProperty("samples");
            return p != null ? Convert.ToInt32(p.GetValue(clip, null)) : -1;
        }

        // 流式 PCMReaderCallback：delegate void(float[])。每次回调收到一块缓冲，从中按偏移拷贝，
        // 直到整段 PCM 喂完。stream:true 时 Unity 会在播放过程中多次回调。
        private static void PcmReaderCallback(float[] data)
        {
            if (_pcmBuffer == null) return;
            int remaining = _pcmBuffer.Length - _pcmOffset;
            int n = Math.Min(data.Length, remaining);
            if (n > 0)
            {
                Array.Copy(_pcmBuffer, _pcmOffset, data, 0, n);
                _pcmOffset += n;
            }
            // 余下 data 段保持静音（默认 0），无需处理
        }

        // ===================== 内部工具 =====================

        private static bool TryDecodeToPcm(string path, out float[] pcm, out int channels, out int sampleRate)
        {
            pcm = null;
            channels = 2;
            sampleRate = 44100;

            if (!File.Exists(path)) return false;
            return TryDecodeWithFfmpeg(path, Plugin.ResampleHz.Value, out pcm, out channels, out sampleRate);
        }

        public static bool HasFfmpeg()
        {
            return !string.IsNullOrEmpty(ResolveFfmpegPath());
        }

        public static string ResolveFfmpegPath()
        {
            string[] candidates =
            {
                Path.Combine(BepInEx.Paths.PluginPath, "rainylove", "tools", "ffmpeg.exe"),
                Path.Combine(BepInEx.Paths.PluginPath, "rainylove", "ffmpeg.exe"),
                @"D:\tools\ffmpeg\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                "ffmpeg.exe",
            };

            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try { if (File.Exists(c)) return c; }
                catch { /* 忽略不可读路径 */ }
            }

            // 在 PATH 环境变量里查找
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    try { if (File.Exists(p)) return p; } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }

            return string.Empty;
        }

        private static bool TryDecodeWithFfmpeg(string path, int resampleHz, out float[] pcm, out int channels, out int sampleRate)
        {
            pcm = null;
            channels = 2;
            sampleRate = resampleHz > 0 ? resampleHz : 44100;

            string ffmpeg = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Plugin.Log.LogWarning("[rainylove] 未找到 ffmpeg，无法解码：" + path);
                return false;
            }

            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.Arguments = string.Format(
                "-hide_banner -loglevel error -i \"{0}\" -map 0:a:0 -f f32le -acodec pcm_f32le -ar {1} -ac 2 -",
                path, sampleRate);

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;

                    using (var ms = new MemoryStream())
                    {
                        // 必须在 ReadToEnd 之前把 stdout 读完，否则 stderr 缓冲写满会导致 ffmpeg 死锁
                        proc.StandardOutput.BaseStream.CopyTo(ms);
                        string err = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();

                        if (proc.ExitCode != 0)
                        {
                            Plugin.Log.LogWarning("[rainylove] ffmpeg 解码失败：\n" + err);
                            return false;
                        }

                        byte[] bytes = ms.ToArray();
                        if (bytes.Length < 4) return false;
                        if (bytes.Length % 4 != 0)
                            Array.Resize(ref bytes, bytes.Length - (bytes.Length % 4));

                        pcm = new float[bytes.Length / 4];
                        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                        return pcm.Length > 0;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] 调用 ffmpeg 异常：" + e.Message);
                return false;
            }
        }

        private static Assembly LoadAssembly(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            try { return Assembly.Load(name); }
            catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { /* 某些程序集 GetType 会抛，忽略 */ }
            }
            return null;
        }
    }

    /// <summary>Windows 系统音频播放（winmm PlaySound），彻底绕开本构建失效的 Unity 音频管线。
    /// 直接走 Windows 默认输出设备发声，不依赖 Unity 的 AudioClip / AudioSource / UnityWebRequest。</summary>
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
            if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath)) return;
            try
            {
                PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                Plugin.Log.LogInfo("[rainylove] 系统音频播放已发起：" + wavPath);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] PlaySound 调用失败：" + e.GetType().Name + " - " + e.Message);
            }
        }

        /// <summary>中断当前播放（停止本进程所有异步 PlaySound 声音）。
        /// 关键：必须用 pszSound=NULL + SND_PURGE 来“停止”，绝不能传文件名 + SND_ASYNC + SND_PURGE——
        /// 后者在 Windows 上会先把旧实例停掉、再以异步方式重新播放同一文件，表现为“重播”而不是停止。
        /// 本游戏只有本 mod 用 winmm 的 PlaySound，停止全域异步声音安全且等价。</summary>
        public static void Stop(string wavPath)
        {
            try
            {
                // pszSound=NULL + SND_PURGE：停止调用进程内所有正在异步播放的声音，且不重新播放任何东西。
                PlaySound(null, IntPtr.Zero, SND_PURGE | SND_ASYNC);
                Plugin.Log.LogInfo("[rainylove] 系统音频已中断：" + (wavPath ?? ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[rainylove] PlaySound 停止失败：" + e.GetType().Name + " - " + e.Message);
            }
        }
    }
}
