using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class CameraMonitoringTests
{
    [Fact]
    public void MetricsReflectDecodedFramesAndConnectionState()
    {
        var metrics = new CameraMonitoring();
        var info = new VideoStreamInfo(VideoCodec.H265, 1920, 1080, 20);
        var before = metrics.Snapshot("123", "Yard", "192.168.1.10", 8800, "lan", info, null, null, 8080, 8554);
        Assert.Equal(0, before.stream_connected);
        Assert.Equal(-1, before.video_frame_age_seconds);
        Assert.Equal(0, before.video_healthy);

        metrics.BeginConnection();
        metrics.Authenticated(32);
        metrics.SetCommunicationVersion(21);
        metrics.Streaming();
        metrics.PacketReceived(2048);
        metrics.VideoDecoded(1024, true, "FullBlocks", 16, 50);
        metrics.VideoDecoded(512, false, "FullBlocks", 16, 50);
        metrics.AudioSkipped();
        metrics.AudioDecoded(505, "IMA_ADPCM_to_PCMA");
        metrics.InvalidHeader();
        metrics.UnknownPacket();

        var current = metrics.Snapshot("123", "Yard", "192.168.1.10", 8800, "lan", info, null, null, 8080, 8554);
        Assert.Equal(1, current.stream_connected);
        Assert.Equal(1, current.video_healthy);
        Assert.Equal(1, current.audio_healthy);
        Assert.Equal(32, current.device_version);
        Assert.Equal(21, current.communication_version);
        Assert.Equal(2, current.video_frames_total);
        Assert.Equal(1536, current.video_bytes_total);
        Assert.Equal(1, current.video_keyframes_total);
        Assert.Equal(1, current.audio_frames_total);
        Assert.Equal(505, current.audio_bytes_total);
        Assert.Equal(1, current.audio_skipped_frames_total);
        Assert.Equal(1, current.invalid_headers_total);
        Assert.Equal(1, current.unknown_packets_total);
        Assert.Equal(1, current.packets_received_total);
        Assert.Equal(2060, current.network_bytes_received_total);
        Assert.Equal("H265", current.video_codec);
        Assert.Equal(1920, current.video_width);
        Assert.Equal(1080, current.video_height);
        Assert.Equal(20, current.video_reported_fps);
        Assert.Equal("FullBlocks", current.video_decrypt_mode);
        Assert.Equal("IMA_ADPCM_to_PCMA", current.audio_codec);
        Assert.InRange(current.video_measured_fps_10s, 0.1, 0.2);
        Assert.Equal(0, current.video_decode_errors_total);

        metrics.Disconnected();
        var disconnected = metrics.Snapshot("123", "Yard", "192.168.1.10", 8800, "lan", info, null, null, 8080, 8554);
        Assert.Equal(0, disconnected.stream_connected);
        Assert.Equal(0, disconnected.video_healthy);
        Assert.Equal(0, disconnected.stream_uptime_seconds);
    }

    [Fact]
    public void RejectedAuthenticationIsObservableWithoutExposingCredentials()
    {
        var metrics = new CameraMonitoring();
        metrics.BeginConnection();
        metrics.AuthenticationFailed(true);
        metrics.Disconnected();
        metrics.Stopped();
        var result = metrics.Snapshot("123", "Entry", "192.168.1.10", 8800,
            "lan", new VideoStreamInfo(VideoCodec.H264, 1280, 720, 12, false),
            null, null, 8080, 8554);
        Assert.Equal("authentication_rejected", result.connection_state);
        Assert.Equal("authentication_rejected", result.last_error);
        Assert.Equal(1, result.auth_failures_total);
        Assert.Equal(0, result.video_width);
        Assert.Equal(-1, result.audio_frame_age_seconds);
    }

    [Fact]
    public async Task MetricsEndpointUsesExistingSecureAuthentication()
    {
        using var client = new V380Client("127.0.0.1", 8800, 123u, "admin", "secret",
            SourceStream.Lan, OutputMode.Rtsp, false);
        var server = new WebServer(0, 8554, client, true, false, false, true, "admin", "secret");
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}") };
            using var unauthorized = await http.GetAsync("/api/metrics");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret")));
            using var authorized = await http.GetAsync("/api/metrics");
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            using var json = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
            Assert.Equal("123", json.RootElement.GetProperty("camera_id").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("stream_connected").GetInt32());
            Assert.Equal(-1, json.RootElement.GetProperty("video_frame_age_seconds").GetInt64());
            Assert.False(json.RootElement.TryGetProperty("password", out _));

            using var status = await http.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }
        finally { server.Stop(); }
    }
}
