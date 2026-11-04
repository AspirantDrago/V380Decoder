using System.Net;
using System.Net.Sockets;
using System.Text;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class RtspNvrCompatibilityTests
{
    [Fact]
    public async Task DigestAuthenticationAndKeepaliveSurviveClientRtcpAndRequestBodies()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var sender = await listener.AcceptTcpClientAsync();
        var server = new RtspServer(0, true, "admin", "test");
        var session = new RtspSession(1, sender, server, true);
        session.Start();
        try
        {
            var stream = receiver.GetStream();
            const string target = "rtsp://127.0.0.1:8554/live";
            string unauthorized = await Request(stream, $"DESCRIBE {target} RTSP/1.0\r\nCSeq: 1\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 401", unauthorized);
            string challenge = unauthorized.Split("\r\n").Single(l => l.StartsWith("WWW-Authenticate: Digest"))[18..];
            string auth = OnvifCompatibilityTests.Digest(challenge.Trim(), "DESCRIBE", target);
            string description = await Request(stream, $"DESCRIBE {target} RTSP/1.0\r\nCSeq: 2\r\nAuthorization: {auth}\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 200", description);
            Assert.Contains("Content-Base: " + target + "/", description);
            // Client RTCP is binary and may contain bytes resembling an RTSP header terminator.
            await stream.WriteAsync(new byte[] { 0x24, 1, 0, 8, 0x80, 0xC9, 13, 10, 13, 10, 0, 0 });
            string keepalive = await Request(stream, $"GET_PARAMETER {target} RTSP/1.0\r\nCSeq: 3\r\nSession: 1\r\nContent-Length: 4\r\n\r\nping");
            Assert.StartsWith("RTSP/1.0 200", keepalive);
            string options = await Request(stream, $"OPTIONS {target} RTSP/1.0\r\nCSeq: 4\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 200", options);
            Assert.Contains("GET_PARAMETER", options);
        }
        finally { session.Close(); server.Dispose(); }
    }

    [Theory]
    [InlineData(VideoCodec.H264, 96)]
    [InlineData(VideoCodec.H265, 97)]
    public async Task NegotiatesUdpAndSendsRawRtpWithoutUnrequestedAudio(VideoCodec codec, int payloadType)
    {
        using var video = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var sender = await listener.AcceptTcpClientAsync();
        var server = new RtspServer(0, false, "", "");
        var session = new RtspSession(1, sender, server);
        session.Start();
        try
        {
            var stream = receiver.GetStream();
            string setup = await Request(stream, $"SETUP rtsp://localhost/live/trackID=0 RTSP/1.0\r\nCSeq: 1\r\nTransport: RTP/AVP;unicast;client_port={((IPEndPoint)video.Client.LocalEndPoint).Port}-{((IPEndPoint)rtcp.Client.LocalEndPoint).Port}\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 200", setup);
            Assert.Contains("Transport: RTP/AVP;unicast;client_port=", setup);
            Assert.Contains("server_port=", setup);
            await Request(stream, "PLAY rtsp://localhost/live RTSP/1.0\r\nCSeq: 2\r\n\r\n");
            await Task.Delay(20);
            byte[] nal = codec == VideoCodec.H265 ? [0x26, 1, 0x55] : [0x65, 0x55];
            session.PushVideo(new FrameData { Codec = codec, Payload = [0, 0, 0, 1, ..nal] });
            var datagram = await video.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0x80, datagram.Buffer[0]);
            Assert.Equal(payloadType, datagram.Buffer[1] & 0x7F);
            Assert.Equal(nal, datagram.Buffer[12..]);
            Assert.Equal(15 - (codec == VideoCodec.H264 ? 1 : 0), datagram.Buffer.Length);
            session.PushAudio(new FrameData { Payload = new byte[160] });
            string options = await Request(stream, "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 3\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 200", options); // no unsolicited interleaved audio before this response
            using var audio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var audioControl = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await Request(stream, $"SETUP rtsp://localhost/live/trackID=1 RTSP/1.0\r\nCSeq: 4\r\nTransport: RTP/AVP;unicast;client_port={((IPEndPoint)audio.Client.LocalEndPoint).Port}-{((IPEndPoint)audioControl.Client.LocalEndPoint).Port}\r\n\r\n");
            session.PushAudio(new FrameData { Payload = Enumerable.Repeat((byte)0xD5, 160).ToArray() });
            var sound = await audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(8, sound.Buffer[1] & 0x7F);
            Assert.Equal(172, sound.Buffer.Length);
            Assert.All(sound.Buffer[12..], b => Assert.Equal(0xD5, b));
        }
        finally { session.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task InvalidTransportDoesNotPretendTcpWasNegotiated()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var sender = await listener.AcceptTcpClientAsync();
        var server = new RtspServer(0, false, "", "");
        var session = new RtspSession(1, sender, server);
        session.Start();
        try
        {
            var response = await Request(receiver.GetStream(), "SETUP rtsp://localhost/live/trackID=0 RTSP/1.0\r\nCSeq: 1\r\nTransport: RTP/AVP;multicast\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 461", response);
        }
        finally { session.Close(); server.Dispose(); }
    }

    private static async Task<string> Request(NetworkStream stream, string request)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n"))
        {
            await stream.ReadExactlyAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            bytes.Add(one[0]);
        }
        string header = Encoding.ASCII.GetString(bytes.ToArray());
        string length = header.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:"));
        if (length != null)
        {
            var body = new byte[int.Parse(length.Split(':', 2)[1])];
            await stream.ReadExactlyAsync(body).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return header + Encoding.ASCII.GetString(body);
        }
        return header;
    }
}
