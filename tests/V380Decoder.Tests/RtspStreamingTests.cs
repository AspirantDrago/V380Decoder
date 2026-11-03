using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class RtspStreamingTests
{
    [Theory]
    [InlineData(VideoCodec.H264, 40)]
    [InlineData(VideoCodec.H264, 3500)]
    [InlineData(VideoCodec.H265, 40)]
    [InlineData(VideoCodec.H265, 3500)]
    public async Task PreservesCodecChannelsFragmentsAndMonotonicTimestamps(VideoCodec codec, int size)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var sender = await listener.AcceptTcpClientAsync();
        var server = new RtspServer(0, false, "", "");
        var session = new RtspSession(1, sender, server);
        session.Start();
        var stream = receiver.GetStream();
        try
        {
            await Request(stream, "SETUP rtsp://localhost/live/trackID=0 RTSP/1.0\r\nCSeq: 1\r\nTransport: RTP/AVP/TCP;unicast;interleaved=6-7\r\n\r\n");
            await Request(stream, "SETUP rtsp://localhost/live/trackID=1 RTSP/1.0\r\nCSeq: 2\r\nTransport: RTP/AVP/TCP;unicast;interleaved=10-11\r\n\r\n");
            await Request(stream, "PLAY rtsp://localhost/live RTSP/1.0\r\nCSeq: 3\r\n\r\n");

            byte[] nal = Enumerable.Repeat((byte)0x55, size).ToArray();
            nal[0] = codec == VideoCodec.H265 ? (byte)0x26 : (byte)0x65;
            nal[1] = 1;
            // A parameter set precedes the slice; only the final slice fragment marks the frame.
            byte[] parameter = codec == VideoCodec.H265 ? [0x40, 1, 0x55] : [0x68, 0x55];
            byte[] payload = [0, 0, 1, ..parameter, 0, 0, 0, 1, ..nal];
            uint previousTimestamp = 0;
            ushort sequence = 0;
            foreach (ulong cameraTimestamp in new ulong[] { 1000, 1000, 1 })
            {
                session.PushVideo(new FrameData { Codec = codec, Payload = payload, FrameRate = 25, Timestamp = cameraTimestamp });
                var reconstructed = new List<byte>();
                bool firstPacket = true;
                uint timestamp = 0;
                while (true)
                {
                    var (channel, rtp) = await ReadPacket(stream);
                    Assert.Equal(6, channel);
                    Assert.Equal(codec == VideoCodec.H265 ? 97 : 96, rtp[1] & 0x7F);
                    Assert.Equal(sequence++, BinaryPrimitives.ReadUInt16BigEndian(rtp.AsSpan(2)));
                    uint packetTimestamp = BinaryPrimitives.ReadUInt32BigEndian(rtp.AsSpan(4));
                    if (firstPacket)
                    {
                        timestamp = packetTimestamp;
                        Assert.Equal(parameter, rtp[12..]);
                        Assert.Equal(0, rtp[1] & 0x80);
                        firstPacket = false;
                        continue;
                    }
                    Assert.Equal(timestamp, packetTimestamp);
                    var data = rtp[12..];
                    bool fragmented = codec == VideoCodec.H265 ? (data[0] >> 1 & 63) == 49 : (data[0] & 31) == 28;
                    if (fragmented)
                    {
                        int header = codec == VideoCodec.H265 ? 2 : 1;
                        bool start = (data[header] & 0x80) != 0;
                        bool end = (data[header] & 0x40) != 0;
                        Assert.Equal(reconstructed.Count == 0, start);
                        Assert.Equal(end, (rtp[1] & 0x80) != 0);
                        if (start) reconstructed.AddRange(nal[..header]);
                        reconstructed.AddRange(data[(header + 1)..]);
                    }
                    else reconstructed.AddRange(data);
                    if ((rtp[1] & 0x80) != 0) break;
                }
                Assert.Equal(nal, reconstructed.ToArray());
                Assert.Equal(previousTimestamp + 3600u, timestamp);
                previousTimestamp = timestamp;
            }

            session.PushAudio(new FrameData { Payload = new byte[200], Timestamp = 999 });
            session.PushAudio(new FrameData { Payload = new byte[160], Timestamp = 0 });
            session.PushAudio(new FrameData { Payload = new byte[505], Timestamp = 0 });
            foreach (uint expected in new uint[] { 0, 160, 200, 360, 520, 680, 840 })
            {
                var (channel, rtp) = await ReadPacket(stream);
                Assert.Equal(10, channel);
                Assert.Equal(8, rtp[1]);
                Assert.Equal(expected, BinaryPrimitives.ReadUInt32BigEndian(rtp.AsSpan(4)));
            }
        }
        finally { session.Close(); server.Dispose(); }
    }

    [Fact]
    public void SdpKeepsHevcParametersAndCanSwitchBackToH264()
    {
        var server = new RtspServer(0, false, "", "");
        server.PushVideo(new FrameData { Codec = VideoCodec.H265, RawType = 0x28, Payload = VideoPayloadDecoderTests.BuildHevcAccessUnit() });
        string hevcSdp = server.BuildSdp();
        Assert.Contains("a=rtpmap:97 H265/90000", hevcSdp);
        Assert.Contains("a=fmtp:97 sprop-vps=", hevcSdp);
        Assert.Contains(";sprop-sps=", hevcSdp);
        Assert.Contains(";sprop-pps=", hevcSdp);
        server.PushVideo(new FrameData { Codec = VideoCodec.H264, RawType = 0, Payload = [0, 0, 1, 0x67, 0x64, 0, 0x1F, 0, 0, 1, 0x68, 0x55] });
        string h264Sdp = server.BuildSdp();
        Assert.Contains("a=rtpmap:96 H264/90000", h264Sdp);
        Assert.Contains("profile-level-id=64001F", h264Sdp);
        Assert.DoesNotContain("H265", h264Sdp);
    }

    private static async Task Request(NetworkStream stream, string request)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var response = new List<byte>();
        var one = new byte[1];
        while (!Encoding.ASCII.GetString(response.ToArray()).EndsWith("\r\n\r\n"))
        {
            await stream.ReadExactlyAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            response.Add(one[0]);
        }
        Assert.StartsWith("RTSP/1.0 200 OK", Encoding.ASCII.GetString(response.ToArray()));
        if (request.StartsWith("PLAY"))
            // The session sets playing immediately after writing its PLAY response.
            await Task.Delay(20);
    }

    private static async Task<(int channel, byte[] rtp)> ReadPacket(NetworkStream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0x24, header[0]);
        var rtp = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2))];
        await stream.ReadExactlyAsync(rtp).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return (header[1], rtp);
    }
}
