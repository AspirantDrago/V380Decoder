using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace V380Decoder.src
{
    public class RtspServer
    {
        private readonly int port;
        private readonly bool secure;
        private readonly string username;
        private readonly string password;
        private readonly string cameraName;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        // concurrent set of active sessions
        private readonly ConcurrentDictionary<int, RtspSession> sessions = new();
        private int nextId;

        // Codec parameter sets used in SDP.
        private byte[] cachedVps, cachedSps, cachedPps;
        private VideoCodec cachedCodec = VideoCodec.H264;
        private bool codecKnown;
        private readonly object sdpLock = new();
        private readonly object compatibilityLock = new();
        private H264CompatibilityStream compatibility;
        private RtspServer compatibilitySdp;
        private FrameData lastKeyframe;
        private VideoStreamInfo videoInfo = new(VideoCodec.H264, 1280, 720, 12, false);
        internal int BoundPort => ((IPEndPoint)listener.LocalEndpoint).Port;

        public RtspServer(int port, bool secure, string username, string password, string cameraName = null)
        {
            this.port = port;
            this.username = username;
            this.password = password;
            this.secure = secure;
            this.cameraName = CameraName.Validate(cameraName);
        }

        public bool IsSecure => secure;
        public string Username => username;
        public string Password => password;

        public void Start()
        {
            string basicAuth = secure ? $"{username}:{password}@" : string.Empty;
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(10);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
            acceptThread.Start();
            Console.Error.WriteLine($"[RTSP] rtsp://{basicAuth}{NetworkHelper.GetLocalIPAddress()}:{port}/live");
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var tcp = listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    int id = Interlocked.Increment(ref nextId);
                    var s = new RtspSession(id, tcp, this, secure);
                    sessions[id] = s;
                    s.OnClose += () =>
                    {
                        sessions.TryRemove(id, out _);
                        lock (compatibilityLock)
                            if (!sessions.Values.Any(s => s.CompatibilityVideo)) StopCompatibility();
                    };
                    s.Start();
                }
                catch { }
            }
        }

        // Called from main receive loop for every complete video frame
        public void PushVideo(FrameData f)
        {
            CacheParameterSets(f);
            foreach (var s in sessions.Values)
                if (!s.CompatibilityVideo || f.Codec == VideoCodec.H264) s.PushVideo(f);
            lock (compatibilityLock)
                if (f.Codec == VideoCodec.H265) compatibility?.Push(WithParameterSets(f));
        }

        // Called from main receive loop for every complete audio frame
        public void PushAudio(FrameData f)
        {
            foreach (var s in sessions.Values) s.PushAudio(f);
        }

        // ── codec parameter set extraction ──────────────────────
        void CacheParameterSets(FrameData frame)
        {
            lock (sdpLock)
            {
                if (!codecKnown || cachedCodec != frame.Codec)
                {
                    cachedCodec = frame.Codec;
                    codecKnown = true;
                    cachedVps = null;
                    cachedSps = null;
                    cachedPps = null;
                    lastKeyframe = null;
                    Console.Error.WriteLine($"[RTSP] video codec={cachedCodec}");
                }

                ParseNals(frame.Payload, frame.Codec, (nalType, nal) =>
                {
                    if (frame.Codec == VideoCodec.H264)
                    {
                        if (nalType == 7) cachedSps = nal;
                        if (nalType == 8) cachedPps = nal;
                        return;
                    }

                    if (nalType == 32) cachedVps = nal;
                    if (nalType == 33) cachedSps = nal;
                    if (nalType == 34) cachedPps = nal;
                });
                int width = videoInfo.Width, height = videoInfo.Height;
                if (frame.Codec == VideoCodec.H265 && HevcSpsParser.TryGetDimensions(cachedSps, out int w, out int h))
                { width = w; height = h; }
                videoInfo = new(frame.Codec, width, height, frame.FrameRate is > 0 and <= 120 ? frame.FrameRate : 12);
                if (frame.IsKeyframe) lastKeyframe = frame;
            }
        }

        private FrameData WithParameterSets(FrameData frame)
        {
            if (!frame.IsKeyframe) return frame;
            lock (sdpLock)
            {
                using var payload = new MemoryStream();
                foreach (var nal in new[] { cachedVps, cachedSps, cachedPps })
                    if (nal != null) { payload.Write(new byte[] { 0, 0, 0, 1 }); payload.Write(nal); }
                payload.Write(frame.Payload);
                return new FrameData { Codec = frame.Codec, RawType = frame.RawType, FrameRate = frame.FrameRate, Payload = payload.ToArray() };
            }
        }

        internal string Describe(bool compatible)
        {
            if (!compatible) return BuildSdp();
            H264CompatibilityStream encoder;
            RtspServer description;
            lock (compatibilityLock)
            {
                VideoStreamInfo info;
                lock (sdpLock) info = videoInfo;
                if (!info.Known) throw new IOException("Camera video is not ready");
                if (info.Codec == VideoCodec.H264) return BuildSdp();
                if (compatibility == null || compatibility.Completion.IsCompleted)
                {
                    StopCompatibility();
                    compatibilitySdp = new RtspServer(0, false, "", "", cameraName);
                    var cache = compatibilitySdp;
                    compatibility = new H264CompatibilityStream(info, frame =>
                    {
                        if (!ReferenceEquals(compatibilitySdp, cache)) return;
                        cache.CacheParameterSets(frame);
                        foreach (var session in sessions.Values)
                            if (session.CompatibilityVideo) session.PushVideo(frame);
                    });
                    FrameData key;
                    lock (sdpLock) key = lastKeyframe;
                    if (key != null) compatibility.Push(WithParameterSets(key));
                }
                encoder = compatibility;
                description = compatibilitySdp;
            }
            encoder.Ready.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            return description.BuildSdp();
        }

        private void StopCompatibility()
        {
            compatibility?.Dispose();
            compatibility = null;
            compatibilitySdp = null;
        }

        // Walk Annex-B start codes, call cb(nalType, nalBytes) for each NAL.
        internal static void ParseNals(
            byte[] data,
            VideoCodec codec,
            Action<int, byte[]> callback)
        {
            AnnexB.Parse(data, codec, callback);
        }

        public string BuildSdp()
        {
            string videoDescription;
            lock (sdpLock)
            {
                if (!codecKnown)
                {
                    videoDescription =
                        "m=video 0 RTP/AVP 96 97\r\n" +
                        "a=rtpmap:96 H264/90000\r\n" +
                        "a=rtpmap:97 H265/90000\r\n";
                }
                else if (cachedCodec == VideoCodec.H265)
                {
                    string fmtp = "";
                    if (cachedVps != null && cachedSps != null && cachedPps != null)
                    {
                        fmtp =
                            "a=fmtp:97 " +
                            $"sprop-vps={Convert.ToBase64String(cachedVps)};" +
                            $"sprop-sps={Convert.ToBase64String(cachedSps)};" +
                            $"sprop-pps={Convert.ToBase64String(cachedPps)}\r\n";
                    }

                    videoDescription =
                        "m=video 0 RTP/AVP 97\r\n" +
                        "a=rtpmap:97 H265/90000\r\n" +
                        fmtp;
                }
                else
                {
                    string fmtp = "";
                    if (cachedSps != null && cachedPps != null)
                    {
                        string profileLevelId = cachedSps.Length >= 4
                            ? $"{cachedSps[1]:X2}{cachedSps[2]:X2}{cachedSps[3]:X2}"
                            : "64001F";
                        fmtp =
                            "a=fmtp:96 packetization-mode=1;" +
                            $"sprop-parameter-sets={Convert.ToBase64String(cachedSps)}," +
                            $"{Convert.ToBase64String(cachedPps)};" +
                            $"profile-level-id={profileLevelId}\r\n";
                    }

                    videoDescription =
                        "m=video 0 RTP/AVP 96\r\n" +
                        "a=rtpmap:96 H264/90000\r\n" +
                        fmtp;
                }
            }

            return
                "v=0\r\n" +
                "o=- 1 1 IN IP4 0.0.0.0\r\n" +
                $"s={cameraName}\r\n" +
                "t=0 0\r\n" +
                "a=recvonly\r\na=control:*\r\n" +
                videoDescription +
                "a=control:trackID=0\r\n" +
                "m=audio 0 RTP/AVP 8\r\n" +
                "a=rtpmap:8 PCMA/8000/1\r\n" +
                "a=control:trackID=1\r\n";
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            foreach (var s in sessions.Values) s.Close();
            lock (compatibilityLock) StopCompatibility();
        }
    }
}
