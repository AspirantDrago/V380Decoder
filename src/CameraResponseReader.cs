using System.Diagnostics;
using System.Net.Sockets;

namespace V380Decoder.src
{
    internal static class CameraResponseReader
    {
        // Some firmware returns fewer than 412 bytes for command 401. Once
        // the minimum response arrives, allow a short gap for TCP fragments
        // instead of delaying command 303 until the whole login times out.
        public static byte[] Read(
            TcpClient client, int maximumLength, int minimumLength,
            CancellationToken cancellationToken = default,
            int timeoutMs = 5000, int idleTimeoutMs = 200)
        {
            var buffer = new byte[maximumLength];
            var stream = client.GetStream();
            var socket = client.Client;
            int total = 0;
            var elapsed = Stopwatch.StartNew();
            long lastRead = 0;
            while (total < maximumLength && elapsed.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int available = socket.Available;
                if (available > 0)
                {
                    int read = stream.Read(buffer, total, Math.Min(available, maximumLength - total));
                    if (read == 0) break;
                    total += read;
                    lastRead = elapsed.ElapsedMilliseconds;
                    continue;
                }
                if (socket.Poll(0, SelectMode.SelectRead)) break; // EOF
                if (total >= minimumLength && elapsed.ElapsedMilliseconds - lastRead >= idleTimeoutMs)
                    break;
                cancellationToken.WaitHandle.WaitOne(10);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return total == 0 ? null : buffer[..total];
        }

        public static void ReadExactly(
            TcpClient client, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken = default, int timeoutMs = 15000)
        {
            var stream = client.GetStream();
            var socket = client.Client;
            int total = 0;
            var elapsed = Stopwatch.StartNew();
            while (total < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int available = socket.Available;
                if (available > 0)
                {
                    int read = stream.Read(buffer, offset + total, Math.Min(available, count - total));
                    if (read == 0) throw new EndOfStreamException("Camera closed the media connection");
                    total += read;
                    continue;
                }
                if (socket.Poll(0, SelectMode.SelectRead))
                    throw new EndOfStreamException("Camera closed the media connection");
                if (elapsed.ElapsedMilliseconds >= timeoutMs)
                    throw new TimeoutException("Timed out waiting for camera media");
                cancellationToken.WaitHandle.WaitOne(10);
            }
        }
    }
}
