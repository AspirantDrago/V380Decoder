using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class H264CompatibilityTests
{
    [Fact]
    public async Task RealHevcIsEncodedAsDecodableBaselineH264AndProcessStops()
    {
        var output = new MemoryStream();
        var produced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        var encoder = new H264CompatibilityStream(new VideoStreamInfo(VideoCodec.H265, 64, 64, 12), frame =>
        {
            Assert.Equal(VideoCodec.H264, frame.Codec);
            lock (output) output.Write(frame.Payload);
            if (Interlocked.Increment(ref frames) >= 3) produced.TrySetResult();
        });
        try
        {
            byte[] input = VideoPayloadDecoderTests.BuildHevcAccessUnit();
            for (int i = 0; i < 80 && !produced.Task.IsCompleted; i++)
            {
                encoder.Push(new FrameData { Codec = VideoCodec.H265, RawType = 0, FrameRate = 12, Payload = input });
                await Task.Delay(25);
            }
            await produced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await encoder.Ready;
        }
        finally { encoder.Dispose(); await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
        byte[] encoded;
        lock (output) encoded = output.ToArray();
        byte[] sps = null;
        AnnexB.Parse(encoded, VideoCodec.H264, (type, nal) => { if (type == 7) sps = nal; });
        Assert.NotNull(sps);
        Assert.Equal(66, sps[1]); // Baseline, matching the Media1 profile.
        using var decoder = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -f h264 -i pipe:0 -frames:v 1 -f rawvideo -pix_fmt yuv420p pipe:1")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
        Task<string> errors = decoder.StandardError.ReadToEndAsync();
        using var pixels = new MemoryStream();
        Task read = decoder.StandardOutput.BaseStream.CopyToAsync(pixels);
        await decoder.StandardInput.BaseStream.WriteAsync(encoded);
        decoder.StandardInput.Close();
        await decoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await read;
        Assert.True(decoder.ExitCode == 0, await errors);
        Assert.Equal(64 * 64 * 3 / 2, pixels.Length);
    }

    [Fact]
    public async Task NativeHevcAndCompatibilityH264PlayTogetherThroughRealRtsp()
    {
        var server = new RtspServer(0, false, "", "");
        server.Start();
        using var native = new TcpClient();
        using var legacy = new TcpClient();
        using var cancel = new CancellationTokenSource();
        byte[] input = VideoPayloadDecoderTests.BuildHevcAccessUnit();
        Task feed = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                server.PushVideo(new FrameData { Codec = VideoCodec.H265, RawType = 0, FrameRate = 12, Payload = input });
                try { await Task.Delay(30, cancel.Token); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            await native.ConnectAsync("127.0.0.1", server.BoundPort);
            await legacy.ConnectAsync("127.0.0.1", server.BoundPort);
            await Task.Delay(60);
            string root = $"rtsp://127.0.0.1:{server.BoundPort}/live";
            string original = await Request(native.GetStream(), $"DESCRIBE {root} RTSP/1.0\r\nCSeq: 1\r\n\r\n");
            Assert.Contains("H265/90000", original);
            string compatible = await Request(legacy.GetStream(), $"DESCRIBE {root}/h264 RTSP/1.0\r\nCSeq: 1\r\n\r\n");
            Assert.StartsWith("RTSP/1.0 200", compatible);
            Assert.Contains("H264/90000", compatible);
            Assert.DoesNotContain("H265/90000", compatible);
            Assert.Contains("sprop-parameter-sets=", compatible);
            foreach (var (client, path) in new[] { (native, root), (legacy, root + "/h264") })
            {
                await Request(client.GetStream(), $"SETUP {path}/trackID=0 RTSP/1.0\r\nCSeq: 2\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n\r\n");
                await Request(client.GetStream(), $"PLAY {path} RTSP/1.0\r\nCSeq: 3\r\n\r\n");
            }
            Assert.Equal(97, await VideoPayloadType(native.GetStream()));
            Assert.Equal(96, await VideoPayloadType(legacy.GetStream()));
            original = await Request(native.GetStream(), $"DESCRIBE {root} RTSP/1.0\r\nCSeq: 4\r\n\r\n", discardRtp: true);
            Assert.Contains("H265/90000", original);
        }
        finally { cancel.Cancel(); await feed; server.Dispose(); }
    }

    private static async Task<int> VideoPayloadType(NetworkStream stream)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0x24, header[0]);
        byte[] rtp = new byte[(header[2] << 8) | header[3]];
        await stream.ReadExactlyAsync(rtp).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return rtp[1] & 0x7f;
    }

    private static async Task<string> Request(NetworkStream stream, string request, bool discardRtp = false)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var header = new List<byte>();
        byte[] one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, timeout.Token);
            if (header.Count == 0 && one[0] == 0x24 && discardRtp)
            {
                byte[] interleaved = new byte[3];
                await stream.ReadExactlyAsync(interleaved, timeout.Token);
                byte[] data = new byte[(interleaved[1] << 8) | interleaved[2]];
                await stream.ReadExactlyAsync(data, timeout.Token);
                continue;
            }
            header.Add(one[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        string text = Encoding.ASCII.GetString(header.ToArray());
        string length = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:"));
        if (length != null)
        {
            byte[] body = new byte[int.Parse(length.Split(':')[1])];
            await stream.ReadExactlyAsync(body, timeout.Token);
            text += Encoding.ASCII.GetString(body);
        }
        return text;
    }
}
