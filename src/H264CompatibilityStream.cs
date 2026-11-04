using System.Diagnostics;
using System.Threading.Channels;

namespace V380Decoder.src;

// One on-demand encoder shared by Media1 viewers; never blocks camera reception.
internal sealed class H264CompatibilityStream : IDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(30)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool awaitingKeyframe = true;
    private int disposed;
    public Task Ready => ready.Task;
    internal Task Completion { get; }
    internal VideoStreamInfo Info { get; }

    internal static VideoStreamInfo OutputInfo(VideoStreamInfo input)
    {
        double scale = Math.Min(1, Math.Min(1920.0 / input.Width, 1080.0 / input.Height));
        return input with { Codec = VideoCodec.H264, Width = Math.Max(16, (int)(input.Width * scale) / 2 * 2),
            Height = Math.Max(16, (int)(input.Height * scale) / 2 * 2), H264Profile = "Baseline", Bitrate = 2048, GovLength = input.FrameRate * 2 };
    }

    internal H264CompatibilityStream(VideoStreamInfo input, Action<FrameData> output)
    {
        Info = OutputInfo(input);
        var start = new ProcessStartInfo("ffmpeg")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        string[] args = ["-hide_banner", "-loglevel", "error", "-probesize", "32", "-analyzeduration", "0",
            "-threads", "1", "-f", "hevc", "-r", input.FrameRate.ToString(), "-i", "pipe:0", "-an",
            "-vf", $"scale={Info.Width}:{Info.Height}", "-c:v", "libx264", "-threads", "1", "-preset", "ultrafast",
            "-tune", "zerolatency", "-profile:v", "baseline", "-pix_fmt", "yuv420p", "-b:v", "2048k",
            "-maxrate", "2048k", "-bufsize", "2048k", "-g", (input.FrameRate * 2).ToString(),
            "-x264-params", "aud=1:repeat-headers=1", "-flush_packets", "1", "-f", "h264", "pipe:1"];
        foreach (string arg in args) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("Unable to start FFmpeg H.264 encoder");
        Console.Error.WriteLine($"[RTSP] Media1 HEVC -> H264 encoder started {Info.Width}x{Info.Height}/{Info.FrameRate}");
        Completion = Run(output);
    }

    internal void Push(FrameData frame)
    {
        if (stop.IsCancellationRequested || (awaitingKeyframe && !frame.IsKeyframe)) return;
        awaitingKeyframe = !frames.Writer.TryWrite(frame.Payload);
    }

    private async Task Run(Action<FrameData> output)
    {
        Task errors = DrainErrors();
        Task writer = WriteInput();
        try
        {
            var pending = new List<byte>();
            byte[] buffer = new byte[16384];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, stop.Token)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    pending.Add(buffer[i]);
                    int n = pending.Count;
                    if (n < 4 || (pending[n - 1] & 31) != 9 || pending[n - 2] != 1 || pending[n - 3] != 0 || pending[n - 4] != 0) continue;
                    int boundary = n - 4;
                    if (boundary > 0 && pending[boundary - 1] == 0) boundary--;
                    if (boundary == 0) continue;
                    byte[] payload = pending.GetRange(0, boundary).ToArray();
                    pending.RemoveRange(0, boundary);
                    bool vcl = false, key = false;
                    AnnexB.Parse(payload, VideoCodec.H264, (type, _) => { vcl |= type is 1 or 5; key |= type == 5; });
                    if (!vcl) continue;
                    output(new FrameData { Payload = payload, Codec = VideoCodec.H264, FrameRate = (ushort)Info.FrameRate, RawType = key ? (byte)0 : (byte)1 });
                    ready.TrySetResult();
                }
                if (pending.Count > 8 * 1024 * 1024) throw new IOException("H.264 encoder access unit too large");
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!stop.IsCancellationRequested) Console.Error.WriteLine($"[RTSP] H.264 encoder: {ex.Message}"); }
        finally
        {
            ready.TrySetException(new IOException("H.264 encoder stopped before producing video"));
            stop.Cancel();
            frames.Writer.TryComplete();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await writer; await errors; } catch { }
            process.Dispose();
        }
    }

    private async Task WriteInput()
    {
        try
        {
            await foreach (byte[] frame in frames.Reader.ReadAllAsync(stop.Token))
            {
                await process.StandardInput.BaseStream.WriteAsync(frame, stop.Token);
                await process.StandardInput.BaseStream.FlushAsync(stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task DrainErrors()
    {
        string error = await process.StandardError.ReadToEndAsync();
        if (!stop.IsCancellationRequested && !string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine("[RTSP] H.264 FFmpeg: " + error.Trim());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        frames.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }
}
