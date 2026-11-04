using System.Net.Sockets;
using System.Net;
using System.Text;

namespace V380Decoder.src
{
    public class RtspSession
    {
        private readonly int id;
        private readonly TcpClient tcp;
        private readonly NetworkStream ns;
        private readonly RtspServer server;
        private readonly bool secure;
        private bool authenticated;
        private Thread readThread;
        private volatile bool playing;
        private volatile bool alive = true;
        private volatile bool videoSetup, audioSetup;
        private int closed;
        private readonly DigestAuthentication digest = new();
        private UdpTransport videoUdp, audioUdp;
        internal bool CompatibilityVideo { get; private set; }

        // interleaved channels negotiated in SETUP
        private byte videoCh = 0, audioCh = 2;

        // RTP state
        private ushort videoSeq, audioSeq;
        private uint videoSsrc = (uint)new Random().Next();
        private uint audioSsrc = (uint)new Random().Next();

        // Camera timestamps can repeat or jump backwards (firmware v32).
        private uint videoRtpClock, audioRtpClock;

        public event Action OnClose;

        public RtspSession(int id, TcpClient tcp, RtspServer server, bool secure = false)
        {
            this.id = id; this.tcp = tcp; this.server = server;
            this.secure = secure;
            this.authenticated = !secure; // if not secure, auto-authenticate
            ns = tcp.GetStream();
        }

        public void Start()
        {
            readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"rtsp-{id}" };
            readThread.Start();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            alive = false;
            playing = false;
            try { tcp.Close(); } catch { }
            videoUdp?.Dispose();
            audioUdp?.Dispose();
            OnClose?.Invoke();
        }

        // Consume complete RTSP messages and client RTCP interleaved frames.
        void ReadLoop()
        {
            var pending = new List<byte>();
            var buffer = new byte[4096];
            try
            {
                while (alive)
                {
                    int count = ns.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    pending.AddRange(buffer.AsSpan(0, count));
                    while (pending.Count > 0)
                    {
                        if (pending[0] == 0x24)
                        {
                            if (pending.Count < 4) break;
                            int size = 4 + (pending[2] << 8) + pending[3];
                            if (pending.Count < size) break;
                            pending.RemoveRange(0, size);
                            continue;
                        }
                        int end = -1;
                        for (int i = 0; i + 3 < pending.Count; i++)
                            if (pending[i] == 13 && pending[i + 1] == 10 && pending[i + 2] == 13 && pending[i + 3] == 10) { end = i + 4; break; }
                        if (end < 0) { if (pending.Count > 65536) throw new IOException("RTSP header too large"); break; }
                        string header = Encoding.ASCII.GetString(pending.GetRange(0, end).ToArray());
                        string contentLength = header.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        int bodyLength = contentLength == null ? 0 : int.Parse(contentLength.Split(':', 2)[1].Trim());
                        if (bodyLength < 0 || bodyLength > 65536) throw new IOException("Invalid RTSP body length");
                        if (pending.Count < end + bodyLength) break;
                        pending.RemoveRange(0, end + bodyLength);
                        HandleRequest(header);
                    }
                }
            }
            catch { }
            finally { Close(); }
        }

        private bool CheckAuth(string header, string method, string target)
        {
            string value = header.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "";
            return DigestAuthentication.VerifyBasic(value, server.Username, server.Password) ||
                digest.Verify(value, method, target, server.Username, server.Password);
        }

        private sealed class UdpTransport : IDisposable
        {
            public readonly UdpClient Rtp, Rtcp;
            public readonly IPEndPoint Destination;
            public int RtpPort => ((IPEndPoint)Rtp.Client.LocalEndPoint).Port;
            public int RtcpPort => ((IPEndPoint)Rtcp.Client.LocalEndPoint).Port;
            public UdpTransport(IPAddress remote, int port)
            {
                Destination = new IPEndPoint(remote, port);
                Rtp = new UdpClient(new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                Rtcp = new UdpClient(new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            }
            public void Dispose() { Rtp.Dispose(); Rtcp.Dispose(); }
        }

        void HandleRequest(string req)
        {
            string[] lines = req.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0) return;

            string method = lines[0].Split(' ')[0];
            string url = lines[0].Split(' ').ElementAtOrDefault(1) ?? "";
            string cseq = lines.FirstOrDefault(l => l.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                                  ?.Split(':', 2)[1].Trim() ?? "0";
            string transport = lines.FirstOrDefault(l => l.StartsWith("Transport:", StringComparison.OrdinalIgnoreCase)) ?? "";
            string authHeader = lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (method is "DESCRIBE" or "SETUP")
            {
                bool compatible = Uri.TryCreate(url, UriKind.Absolute, out var target) &&
                    target.AbsolutePath.Split('/').Contains("h264");
                if ((videoSetup || audioSetup) && CompatibilityVideo != compatible)
                { Send($"RTSP/1.0 459 Aggregate Operation Not Allowed\r\nCSeq: {cseq}\r\n\r\n"); return; }
                CompatibilityVideo = compatible;
            }

            // Check authentication for methods other than OPTIONS (if secure)
            if (secure && method != "OPTIONS" && !authenticated)
            {
                if (!CheckAuth(authHeader, method, url))
                {
                    Send($"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\n" +
                         $"WWW-Authenticate: {digest.Challenge()}\r\n" +
                         $"WWW-Authenticate: Basic realm=\"V380 Authentication\"\r\n\r\n");
                    return;
                }
                authenticated = true;
            }

            switch (method)
            {
                case "OPTIONS":
                    Reply(cseq, "Public: OPTIONS,DESCRIBE,SETUP,PLAY,GET_PARAMETER,TEARDOWN");
                    break;

                case "DESCRIBE":
                    {
                        string sdp;
                        try { sdp = server.Describe(CompatibilityVideo); }
                        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception)
                        {
                            Console.Error.WriteLine($"[RTSP#{id}] H.264 compatibility stream unavailable: {ex.Message}");
                            Send($"RTSP/1.0 503 Service Unavailable\r\nCSeq: {cseq}\r\nRetry-After: 3\r\n\r\n");
                            break;
                        }
                        byte[] body = Encoding.ASCII.GetBytes(sdp);
                        Send($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n" +
                             $"Content-Type: application/sdp\r\nContent-Base: {url.TrimEnd('/')}/\r\nContent-Length: {body.Length}\r\n\r\n{sdp}");
                        break;
                    }

                case "SETUP":
                    {
                        bool isAudio = url.Contains("trackID=1");
                        bool tcpTransport = transport.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase);
                        if (tcpTransport)
                        {
                            byte ch = (byte)(isAudio ? 2 : 0);
                            var match = System.Text.RegularExpressions.Regex.Match(transport, @"interleaved=(\d+)-(\d+)");
                            if (match.Success && (!byte.TryParse(match.Groups[1].Value, out ch) || ch > 254))
                            { Send($"RTSP/1.0 461 Unsupported Transport\r\nCSeq: {cseq}\r\n\r\n"); break; }
                            if (isAudio) { audioUdp?.Dispose(); audioUdp = null; audioCh = ch; audioSetup = true; }
                            else { videoUdp?.Dispose(); videoUdp = null; videoCh = ch; videoSetup = true; }
                            Reply(cseq, $"Transport: RTP/AVP/TCP;unicast;interleaved={ch}-{ch + 1}", "Session: 1");
                        }
                        else
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(transport, @"client_port=(\d+)-(\d+)");
                            if (!transport.Contains("RTP/AVP", StringComparison.OrdinalIgnoreCase) || transport.Contains("multicast", StringComparison.OrdinalIgnoreCase) ||
                                !match.Success || !int.TryParse(match.Groups[1].Value, out int port) || port is < 1 or > 65535 ||
                                !int.TryParse(match.Groups[2].Value, out int rtcpPort) || rtcpPort is < 1 or > 65535)
                            { Send($"RTSP/1.0 461 Unsupported Transport\r\nCSeq: {cseq}\r\n\r\n"); break; }
                            var udp = new UdpTransport(((IPEndPoint)tcp.Client.RemoteEndPoint).Address, port);
                            if (isAudio) { audioUdp?.Dispose(); audioUdp = udp; audioSetup = true; }
                            else { videoUdp?.Dispose(); videoUdp = udp; videoSetup = true; }
                            Reply(cseq, $"Transport: RTP/AVP;unicast;client_port={port}-{rtcpPort};server_port={udp.RtpPort}-{udp.RtcpPort}", "Session: 1");
                        }
                        break;
                    }

                case "PLAY":
                    if (CompatibilityVideo)
                    {
                        try { server.Describe(true); }
                        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception)
                        { Send($"RTSP/1.0 503 Service Unavailable\r\nCSeq: {cseq}\r\nRetry-After: 3\r\n\r\n"); break; }
                    }
                    Reply(cseq,
                        "Session: 1",
                        $"RTP-Info: url={url}/trackID=0;seq={videoSeq},url={url}/trackID=1;seq={audioSeq}");
                    playing = true;
                    Console.Error.WriteLine($"[RTSP#{id}] playing");
                    break;

                case "GET_PARAMETER":
                    Reply(cseq, "Session: 1");
                    break;

                case "TEARDOWN":
                    Reply(cseq, "Session: 1");
                    Close();
                    break;

                default:
                    Send($"RTSP/1.0 501 Not Implemented\r\nCSeq: {cseq}\r\n\r\n");
                    break;
            }
        }

        void Reply(string cseq, params string[] headers)
        {
            var sb = new StringBuilder();
            sb.Append($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n");
            foreach (var h in headers) sb.Append(h + "\r\n");
            sb.Append("\r\n");
            Send(sb.ToString());
        }

        void Send(string s)
        {
            try
            {
                byte[] b = Encoding.ASCII.GetBytes(s);
                lock (ns) { ns.Write(b, 0, b.Length); ns.Flush(); }
            }
            catch { alive = false; }
        }

        // ── RTP video push (Annex-B H.264/H.265 → RTP) ──────────
        public void PushVideo(FrameData f)
        {
            if (!playing || !videoSetup) return;

            // Advance once per access unit; use the advertised rate, or 12 fps.
            uint tick = f.FrameRate is > 0 and <= 120 ? 90000u / f.FrameRate : 7500u;
            videoRtpClock += tick;
            uint rts = videoRtpClock;
            var nals = new List<byte[]>();
            RtspServer.ParseNals(f.Payload, f.Codec, (_, nal) => nals.Add(nal));

            for (int index = 0; index < nals.Count; index++)
            {
                bool lastNal = index == nals.Count - 1;
                if (f.Codec == VideoCodec.H265)
                    PushH265Nal(nals[index], rts, lastNal);
                else
                    PushH264Nal(nals[index], rts, lastNal);
            }
        }

        private void PushH264Nal(byte[] nal, uint timestamp, bool lastNal)
        {
            const int MaxPayload = 1400;
            if (nal.Length <= MaxPayload)
            {
                SendRtp(
                    videoCh, 96, videoSeq++, timestamp, videoSsrc,
                    nal, 0, nal.Length, marker: lastNal);
                return;
            }

            byte nalHeader = nal[0];
            byte fuIndicator = (byte)((nalHeader & 0xE0) | 28);
            int offset = 1;
            bool first = true;

            while (offset < nal.Length)
            {
                int chunk = Math.Min(MaxPayload - 2, nal.Length - offset);
                bool lastFragment = offset + chunk >= nal.Length;
                byte fuHeader = (byte)(nalHeader & 0x1F);
                if (first) fuHeader |= 0x80;
                if (lastFragment) fuHeader |= 0x40;

                var fragment = new byte[2 + chunk];
                fragment[0] = fuIndicator;
                fragment[1] = fuHeader;
                Array.Copy(nal, offset, fragment, 2, chunk);

                SendRtp(
                    videoCh, 96, videoSeq++, timestamp, videoSsrc,
                    fragment, 0, fragment.Length,
                    marker: lastNal && lastFragment);

                offset += chunk;
                first = false;
            }
        }

        private void PushH265Nal(byte[] nal, uint timestamp, bool lastNal)
        {
            const int MaxPayload = 1400;
            if (nal.Length < 2) return;

            if (nal.Length <= MaxPayload)
            {
                SendRtp(
                    videoCh, 97, videoSeq++, timestamp, videoSsrc,
                    nal, 0, nal.Length, marker: lastNal);
                return;
            }

            int nalType = (nal[0] >> 1) & 0x3F;

            // RFC 7798 section 4.4.3:
            // two-byte PayloadHdr (type 49) + one-byte FU header.
            byte payloadHeader0 = (byte)((nal[0] & 0x81) | (49 << 1));
            byte payloadHeader1 = nal[1];
            int offset = 2;
            bool first = true;

            while (offset < nal.Length)
            {
                int chunk = Math.Min(MaxPayload - 3, nal.Length - offset);
                bool lastFragment = offset + chunk >= nal.Length;
                byte fuHeader = (byte)nalType;
                if (first) fuHeader |= 0x80;
                if (lastFragment) fuHeader |= 0x40;

                var fragment = new byte[3 + chunk];
                fragment[0] = payloadHeader0;
                fragment[1] = payloadHeader1;
                fragment[2] = fuHeader;
                Array.Copy(nal, offset, fragment, 3, chunk);

                SendRtp(
                    videoCh, 97, videoSeq++, timestamp, videoSsrc,
                    fragment, 0, fragment.Length,
                    marker: lastNal && lastFragment);

                offset += chunk;
                first = false;
            }
        }

        // ── RTP audio push  (PCMA raw samples) ──────────────────
        public void PushAudio(FrameData f)
        {
            if (!playing || !audioSetup) return;

            // Use synthetic RTP timestamps to prevent DTS discontinuities
            // 160 samples per chunk at 8 kHz = 20 ms of audio per RTP packet
            const int CHUNK = 160;
            for (int off = 0; off < f.Payload.Length; off += CHUNK)
            {
                int len = Math.Min(CHUNK, f.Payload.Length - off);
                SendRtp(audioCh, 8, audioSeq++, audioRtpClock, audioSsrc,
                        f.Payload, off, len, marker: false);
                audioRtpClock += (uint)len;
            }
        }

        void SendRtp(byte channel, byte pt, ushort seq, uint ts, uint ssrc,
                     byte[] payload, int offset, int length, bool marker)
        {
            int rtpLen = 12 + length;
            var frame = new byte[4 + rtpLen];

            frame[0] = 0x24; // '$'
            frame[1] = channel;
            frame[2] = (byte)(rtpLen >> 8);
            frame[3] = (byte)rtpLen;

            frame[4] = 0x80;
            frame[5] = (byte)((marker ? 0x80 : 0) | (pt & 0x7F));
            frame[6] = (byte)(seq >> 8);
            frame[7] = (byte)seq;
            frame[8] = (byte)(ts >> 24);
            frame[9] = (byte)(ts >> 16);
            frame[10] = (byte)(ts >> 8);
            frame[11] = (byte)ts;
            frame[12] = (byte)(ssrc >> 24);
            frame[13] = (byte)(ssrc >> 16);
            frame[14] = (byte)(ssrc >> 8);
            frame[15] = (byte)ssrc;

            Array.Copy(payload, offset, frame, 16, length);

            try
            {
                var udp = pt == 8 ? audioUdp : videoUdp;
                if (udp != null) udp.Rtp.Send(frame.AsSpan(4), udp.Destination);
                else lock (ns) { ns.Write(frame, 0, frame.Length); }
            }
            catch { Close(); }
        }
    }
}
