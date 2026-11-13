using System.Diagnostics;

namespace V380Decoder.src;

// All counters are process-local. No camera credentials, tickets or AES keys are exposed.
internal sealed class CameraMonitoring
{
    private sealed class TrafficBucket
    {
        public long Second = -1;
        public int VideoFrames;
        public long VideoBytes;
        public long UpstreamBytes;
        public int UpstreamPackets;
        public int AudioFrames;
        public long AudioBytes;
    }

    private readonly object trafficLock = new();
    private readonly TrafficBucket[] traffic = Enumerable.Range(0, 10).Select(_ => new TrafficBucket()).ToArray();
    private readonly long startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private string connectionState = "starting";
    private string lastError = "";
    private string audioCodec = "unknown";
    private string videoDecryptMode = "unknown";
    private int videoHeaderBytes;
    private int videoDecryptScore;
    private int connected;
    private int deviceVersion;
    private int communicationVersion;
    private int deviceType;
    private int cameraType;
    private int vendorId;
    private long authDurationMs;
    private long streamLoginDurationMs;
    private long lastErrorAt;
    private long lastAuthSuccessAt;
    private long lastStreamStartAt;
    private long lastVideoAtMs;
    private long lastAudioAtMs;
    private long lastPacketAtMs;
    private long connectionAttempts;
    private long authAttempts;
    private long authFailures;
    private long streamLoginFailures;
    private long streamStarts;
    private long reconnectAttempts;
    private long receiveErrors;
    private long invalidHeaders;
    private long unknownPackets;
    private long packetsReceived;
    private long networkBytesReceived;
    private long videoFrames;
    private long videoBytes;
    private long videoKeyframes;
    private long videoDecodeErrors;
    private long audioFrames;
    private long audioBytes;
    private long audioSkippedFrames;

    public void BeginConnection()
    {
        Interlocked.Increment(ref connectionAttempts);
        Interlocked.Increment(ref authAttempts);
        Volatile.Write(ref connectionState, "authenticating");
    }

    public void Authenticated(int version)
    {
        Interlocked.Exchange(ref deviceVersion, version);
        Interlocked.Exchange(ref lastAuthSuccessAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Volatile.Write(ref connectionState, "connecting");
    }

    public void AuthenticationFailed(bool fatal)
    {
        Interlocked.Increment(ref authFailures);
        RecordError(fatal ? "authentication_rejected" : "authentication_failed");
        Volatile.Write(ref connectionState, fatal ? "authentication_rejected" : "reconnecting");
    }

    public void StreamLoginFailed()
    {
        Interlocked.Increment(ref streamLoginFailures);
        RecordError("stream_login_failed");
    }

    public void SetDeviceIdentity(int deviceTypeValue, int cameraTypeValue, int vendorIdValue)
    {
        Interlocked.Exchange(ref deviceType, deviceTypeValue);
        Interlocked.Exchange(ref cameraType, cameraTypeValue);
        Interlocked.Exchange(ref vendorId, vendorIdValue);
    }

    public void SetAuthDuration(long milliseconds) =>
        Interlocked.Exchange(ref authDurationMs, milliseconds);

    public void SetStreamLoginDuration(long milliseconds) =>
        Interlocked.Exchange(ref streamLoginDurationMs, milliseconds);

    public void SetCommunicationVersion(int version) =>
        Interlocked.Exchange(ref communicationVersion, version);

    public void Streaming()
    {
        Interlocked.Increment(ref streamStarts);
        Interlocked.Exchange(ref lastStreamStartAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Volatile.Write(ref connected, 1);
        Volatile.Write(ref connectionState, "streaming");
    }

    public void Disconnected()
    {
        Volatile.Write(ref connected, 0);
        if (Volatile.Read(ref connectionState) != "authentication_rejected")
            Volatile.Write(ref connectionState, "reconnecting");
    }

    public void Reconnecting()
    {
        Interlocked.Increment(ref reconnectAttempts);
        Volatile.Write(ref connectionState, "reconnecting");
    }

    public void Stopped()
    {
        Volatile.Write(ref connected, 0);
        if (Volatile.Read(ref connectionState) != "authentication_rejected")
            Volatile.Write(ref connectionState, "stopped");
    }

    public void ReceiveError()
    {
        Interlocked.Increment(ref receiveErrors);
        RecordError("receive_error");
    }

    public void InvalidHeader() => Interlocked.Increment(ref invalidHeaders);
    public void UnknownPacket() => Interlocked.Increment(ref unknownPackets);

    public void PacketReceived(int payloadSize)
    {
        Interlocked.Increment(ref packetsReceived);
        Interlocked.Add(ref networkBytesReceived, 12 + payloadSize);
        Interlocked.Exchange(ref lastPacketAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        RecordTraffic(12 + payloadSize, 2);
    }

    public void VideoDecodeError()
    {
        Interlocked.Increment(ref videoDecodeErrors);
        RecordError("video_payload_invalid");
    }

    public void AudioSkipped() => Interlocked.Increment(ref audioSkippedFrames);

    public void VideoDecoded(int bytes, bool keyframe, string decryptMode, int headerBytes, int decryptScore)
    {
        Interlocked.Increment(ref videoFrames);
        Interlocked.Add(ref videoBytes, bytes);
        if (keyframe) Interlocked.Increment(ref videoKeyframes);
        Interlocked.Exchange(ref lastVideoAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Volatile.Write(ref videoDecryptMode, decryptMode);
        Interlocked.Exchange(ref videoHeaderBytes, headerBytes);
        Interlocked.Exchange(ref videoDecryptScore, decryptScore);
        RecordTraffic(bytes, 0);
    }

    public void AudioDecoded(int bytes, string codec)
    {
        Interlocked.Increment(ref audioFrames);
        Interlocked.Add(ref audioBytes, bytes);
        Interlocked.Exchange(ref lastAudioAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Volatile.Write(ref audioCodec, codec);
        RecordTraffic(bytes, 1);
    }

    private void RecordError(string category)
    {
        // Fixed categories avoid leaking exception details or camera credentials via HTTP.
        Volatile.Write(ref lastError, category);
        Interlocked.Exchange(ref lastErrorAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void RecordTraffic(int bytes, int kind)
    {
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (trafficLock)
        {
            var bucket = traffic[(int)(second % traffic.Length)];
            if (bucket.Second != second)
            {
                bucket.Second = second;
                bucket.VideoFrames = 0;
                bucket.VideoBytes = 0;
                bucket.UpstreamBytes = 0;
                bucket.UpstreamPackets = 0;
                bucket.AudioFrames = 0;
                bucket.AudioBytes = 0;
            }

            if (kind == 0)
            {
                bucket.VideoFrames++;
                bucket.VideoBytes += bytes;
            }
            else if (kind == 1)
            {
                bucket.AudioFrames++;
                bucket.AudioBytes += bytes;
            }
            else
            {
                bucket.UpstreamPackets++;
                bucket.UpstreamBytes += bytes;
            }
        }
    }

    public CameraMetricsSnapshot Snapshot(string cameraId, string cameraName, string upstreamAddress,
        int upstreamPort, string source, VideoStreamInfo videoInfo, DeviceInfo device,
        RtspServer rtsp, int httpPort, int rtspPort)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long nowSecond = nowMs / 1000;
        long vf = 0, vb = 0, af = 0, ab = 0, nb = 0, np = 0;

        lock (trafficLock)
            foreach (var bucket in traffic)
                if (bucket.Second >= nowSecond - 9 && bucket.Second <= nowSecond)
                {
                    vf += bucket.VideoFrames;
                    vb += bucket.VideoBytes;
                    af += bucket.AudioFrames;
                    ab += bucket.AudioBytes;
                    nb += bucket.UpstreamBytes;
                    np += bucket.UpstreamPackets;
                }

        long videoAge = AgeSeconds(nowMs, Interlocked.Read(ref lastVideoAtMs));
        long audioAge = AgeSeconds(nowMs, Interlocked.Read(ref lastAudioAtMs));
        long packetAge = AgeSeconds(nowMs, Interlocked.Read(ref lastPacketAtMs));
        int isConnected = Volatile.Read(ref connected);
        long sessionSince = Interlocked.Read(ref lastStreamStartAt);
        long sessionUptime = isConnected != 0 && sessionSince > 0 ? Math.Max(0, nowSecond - sessionSince) : 0;

        return new CameraMetricsSnapshot
        {
            schema_version = 1,
            timestamp_unixtime = nowSecond,
            camera_id = cameraId,
            camera_name = cameraName,
            source = source,
            upstream_address = upstreamAddress,
            upstream_port = upstreamPort,
            device_mac = device?.Mac ?? "",
            discovered_device_ip = device?.Ip ?? "",
            discovered_subnet = device?.Subnet ?? "",
            discovered_gateway = device?.Gateway ?? "",
            device_version = Volatile.Read(ref deviceVersion),
            communication_version = Volatile.Read(ref communicationVersion),
            device_type = Volatile.Read(ref deviceType),
            camera_type = Volatile.Read(ref cameraType),
            vendor_id = Volatile.Read(ref vendorId),
            last_auth_duration_ms = Interlocked.Read(ref authDurationMs),
            last_stream_login_duration_ms = Interlocked.Read(ref streamLoginDurationMs),
            process_uptime_seconds = (long)uptime.Elapsed.TotalSeconds,
            process_started_unixtime = startedAt,
            connection_state = Volatile.Read(ref connectionState),
            stream_connected = isConnected,
            video_healthy = isConnected != 0 && videoAge >= 0 && videoAge <= 15 ? 1 : 0,
            audio_healthy = isConnected != 0 && audioAge >= 0 && audioAge <= 15 ? 1 : 0,
            stream_uptime_seconds = sessionUptime,
            last_auth_success_unixtime = Interlocked.Read(ref lastAuthSuccessAt),
            last_stream_start_unixtime = sessionSince,
            last_video_unixtime = MillisecondsToSeconds(Interlocked.Read(ref lastVideoAtMs)),
            last_audio_unixtime = MillisecondsToSeconds(Interlocked.Read(ref lastAudioAtMs)),
            last_packet_unixtime = MillisecondsToSeconds(Interlocked.Read(ref lastPacketAtMs)),
            video_frame_age_seconds = videoAge,
            audio_frame_age_seconds = audioAge,
            packet_age_seconds = packetAge,
            last_error = Volatile.Read(ref lastError),
            last_error_unixtime = Interlocked.Read(ref lastErrorAt),
            connection_attempts_total = Interlocked.Read(ref connectionAttempts),
            auth_attempts_total = Interlocked.Read(ref authAttempts),
            auth_failures_total = Interlocked.Read(ref authFailures),
            stream_login_failures_total = Interlocked.Read(ref streamLoginFailures),
            stream_starts_total = Interlocked.Read(ref streamStarts),
            reconnect_attempts_total = Interlocked.Read(ref reconnectAttempts),
            receive_errors_total = Interlocked.Read(ref receiveErrors),
            invalid_headers_total = Interlocked.Read(ref invalidHeaders),
            unknown_packets_total = Interlocked.Read(ref unknownPackets),
            packets_received_total = Interlocked.Read(ref packetsReceived),
            network_bytes_received_total = Interlocked.Read(ref networkBytesReceived),
            upstream_bitrate_bps_10s = nb * 8.0 / 10.0,
            upstream_packets_per_second_10s = np / 10.0,
            video_frames_total = Interlocked.Read(ref videoFrames),
            video_bytes_total = Interlocked.Read(ref videoBytes),
            video_keyframes_total = Interlocked.Read(ref videoKeyframes),
            video_decode_errors_total = Interlocked.Read(ref videoDecodeErrors),
            audio_frames_total = Interlocked.Read(ref audioFrames),
            audio_bytes_total = Interlocked.Read(ref audioBytes),
            audio_skipped_frames_total = Interlocked.Read(ref audioSkippedFrames),
            video_codec = videoInfo.Known ? videoInfo.Codec.ToString() : "unknown",
            video_width = videoInfo.Known ? videoInfo.Width : 0,
            video_height = videoInfo.Known ? videoInfo.Height : 0,
            video_reported_fps = videoInfo.Known ? videoInfo.FrameRate : 0,
            video_measured_fps_10s = vf / 10.0,
            video_bitrate_bps_10s = vb * 8.0 / 10.0,
            audio_measured_fps_10s = af / 10.0,
            audio_bitrate_bps_10s = ab * 8.0 / 10.0,
            audio_codec = Volatile.Read(ref audioCodec),
            video_decrypt_mode = Volatile.Read(ref videoDecryptMode),
            video_header_bytes = Volatile.Read(ref videoHeaderBytes),
            video_decrypt_score = Volatile.Read(ref videoDecryptScore),
            rtsp_port = rtspPort,
            http_port = httpPort,
            rtsp_clients = rtsp?.SessionCount ?? 0,
            rtsp_playing_clients = rtsp?.PlayingSessionCount ?? 0,
            rtsp_h264_compatibility_clients = rtsp?.CompatibilitySessionCount ?? 0,
            rtsp_video_subscribers = rtsp?.VideoSubscriberCount ?? 0,
            rtsp_audio_subscribers = rtsp?.AudioSubscriberCount ?? 0,
            rtsp_udp_clients = rtsp?.UdpClientCount ?? 0
        };
    }

    private static long AgeSeconds(long nowMs, long eventMs) =>
        eventMs == 0 ? -1 : Math.Max(0, (nowMs - eventMs) / 1000);

    private static long MillisecondsToSeconds(long ms) => ms == 0 ? 0 : ms / 1000;
}

public sealed class CameraMetricsSnapshot
{
    public int schema_version { get; set; }
    public long timestamp_unixtime { get; set; }
    public string camera_id { get; set; }
    public string camera_name { get; set; }
    public string source { get; set; }
    public string upstream_address { get; set; }
    public int upstream_port { get; set; }
    public string device_mac { get; set; }
    public string discovered_device_ip { get; set; }
    public string discovered_subnet { get; set; }
    public string discovered_gateway { get; set; }
    public int device_version { get; set; }
    public int communication_version { get; set; }
    public int device_type { get; set; }
    public int camera_type { get; set; }
    public int vendor_id { get; set; }
    public long last_auth_duration_ms { get; set; }
    public long last_stream_login_duration_ms { get; set; }
    public long process_uptime_seconds { get; set; }
    public long process_started_unixtime { get; set; }
    public string connection_state { get; set; }
    public int stream_connected { get; set; }
    public int video_healthy { get; set; }
    public int audio_healthy { get; set; }
    public long stream_uptime_seconds { get; set; }
    public long last_auth_success_unixtime { get; set; }
    public long last_stream_start_unixtime { get; set; }
    public long last_video_unixtime { get; set; }
    public long last_audio_unixtime { get; set; }
    public long last_packet_unixtime { get; set; }
    public long video_frame_age_seconds { get; set; }
    public long audio_frame_age_seconds { get; set; }
    public long packet_age_seconds { get; set; }
    public string last_error { get; set; }
    public long last_error_unixtime { get; set; }
    public long connection_attempts_total { get; set; }
    public long auth_attempts_total { get; set; }
    public long auth_failures_total { get; set; }
    public long stream_login_failures_total { get; set; }
    public long stream_starts_total { get; set; }
    public long reconnect_attempts_total { get; set; }
    public long receive_errors_total { get; set; }
    public long invalid_headers_total { get; set; }
    public long unknown_packets_total { get; set; }
    public long packets_received_total { get; set; }
    public long network_bytes_received_total { get; set; }
    public double upstream_bitrate_bps_10s { get; set; }
    public double upstream_packets_per_second_10s { get; set; }
    public long video_frames_total { get; set; }
    public long video_bytes_total { get; set; }
    public long video_keyframes_total { get; set; }
    public long video_decode_errors_total { get; set; }
    public long audio_frames_total { get; set; }
    public long audio_bytes_total { get; set; }
    public long audio_skipped_frames_total { get; set; }
    public string video_codec { get; set; }
    public int video_width { get; set; }
    public int video_height { get; set; }
    public int video_reported_fps { get; set; }
    public double video_measured_fps_10s { get; set; }
    public double video_bitrate_bps_10s { get; set; }
    public double audio_measured_fps_10s { get; set; }
    public double audio_bitrate_bps_10s { get; set; }
    public string audio_codec { get; set; }
    public string video_decrypt_mode { get; set; }
    public int video_header_bytes { get; set; }
    public int video_decrypt_score { get; set; }
    public int rtsp_port { get; set; }
    public int http_port { get; set; }
    public int rtsp_clients { get; set; }
    public int rtsp_playing_clients { get; set; }
    public int rtsp_h264_compatibility_clients { get; set; }
    public int rtsp_video_subscribers { get; set; }
    public int rtsp_audio_subscribers { get; set; }
    public int rtsp_udp_clients { get; set; }
}
