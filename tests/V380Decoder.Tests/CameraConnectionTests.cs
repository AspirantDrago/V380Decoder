using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class CameraConnectionTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(412)]
    public async Task StartsBeforeCameraDeadlineAndBlocksControlDuringLogin(int responseLength)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = NewClient(listener);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loginReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var camera = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            Assert.Equal(301u, await ReadCommand(stream, 256, deadline.Token));
            loginReceived.SetResult();
            await releaseResponse.Task.WaitAsync(deadline.Token);
            byte[] response = LoginResponse(responseLength);
            await stream.WriteAsync(response.AsMemory(0, 8), deadline.Token);
            await Task.Delay(100, deadline.Token);
            await stream.WriteAsync(response.AsMemory(8), deadline.Token);
            // A short response must not postpone START for the old five-second timeout.
            using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            startDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            Assert.Equal(303u, await ReadCommand(stream, 256, startDeadline.Token));
            var control = new byte[V380Commands.PTZ_RIGHT.Length];
            await stream.ReadExactlyAsync(control, deadline.Token);
            Assert.Equal(V380Commands.PTZ_RIGHT, control);
        });
        var login = Task.Run(() => client.StreamLogin(deadline.Token));
        await loginReceived.Task.WaitAsync(deadline.Token);
        for (int i = 0; i < 10; i++) Assert.False(client.PtzRight());
        releaseResponse.SetResult();
        Assert.True(await login.WaitAsync(deadline.Token));
        Assert.False(client.PtzRight());
        Assert.True(client.StartStream());
        Assert.True(client.PtzRight());
        await camera.WaitAsync(deadline.Token);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-11)]
    [InlineData(-12)]
    public async Task NegativeLoginResultDoesNotEnableControls(int error)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = NewClient(listener);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var camera = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            Assert.Equal(301u, await ReadCommand(peer.GetStream(), 256, deadline.Token));
            var response = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(response, 401);
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4), error);
            await peer.GetStream().WriteAsync(response, deadline.Token);
        });
        Assert.False(await Task.Run(() => client.StreamLogin(deadline.Token)).WaitAsync(deadline.Token));
        Assert.False(client.PtzRight());
        await camera.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task FixedAuthResponseWaitsForAllTcpFragments()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connection = new TcpClient();
        await connection.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] response = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var read = Task.Run(() => CameraResponseReader.Read(connection, 256, 256, deadline.Token));
        await peer.GetStream().WriteAsync(response.AsMemory(0, 16), deadline.Token);
        await Task.Delay(350, deadline.Token); // greater than the variable-length reply idle gap
        Assert.False(read.IsCompleted);
        await peer.GetStream().WriteAsync(response.AsMemory(16), deadline.Token);
        Assert.Equal(response, await read.WaitAsync(deadline.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ClosedMediaConnectionIsDetectedWithoutWaitingForTimeout(int partialLength)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connection = new TcpClient();
        await connection.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using (var peer = await listener.AcceptTcpClientAsync())
        {
            if (partialLength > 0) await peer.GetStream().WriteAsync(new byte[partialLength]);
        }
        var read = Task.Run(() => CameraResponseReader.ReadExactly(connection, new byte[12], 0, 12));
        await Assert.ThrowsAsync<EndOfStreamException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancellationInterruptsIdleMediaRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connection = new TcpClient();
        await connection.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync();
        using var stop = new CancellationTokenSource();
        var read = Task.Run(() => CameraResponseReader.ReadExactly(connection, new byte[12], 0, 12, stop.Token));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ReauthenticatesAfterDisconnectAndStopsWhenCancelled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = NewClient(listener);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var secondStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authTimes = new List<DateTime>();
        var camera = Task.Run(async () =>
        {
            foreach (uint ticket in new uint[] { 100, 200 })
            {
                using (var auth = await listener.AcceptTcpClientAsync(stop.Token))
                {
                    Assert.Equal(1167u, await ReadCommand(auth.GetStream(), 520, stop.Token));
                    authTimes.Add(DateTime.UtcNow);
                    var response = new byte[256];
                    BinaryPrimitives.WriteUInt32LittleEndian(response, 1168);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 1001);
                    response[12] = 31;
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(13), ticket);
                    await auth.GetStream().WriteAsync(response, stop.Token);
                }
                using var media = await listener.AcceptTcpClientAsync(stop.Token);
                var command = new byte[256];
                await media.GetStream().ReadExactlyAsync(command, stop.Token);
                Assert.Equal(301u, BinaryPrimitives.ReadUInt32LittleEndian(command));
                Assert.Equal(ticket, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(14)));
                await media.GetStream().WriteAsync(LoginResponse(64), stop.Token);
                Assert.Equal(303u, await ReadCommand(media.GetStream(), 256, stop.Token));
                if (ticket == 200)
                {
                    secondStreamStarted.SetResult();
                    try { await Task.Delay(Timeout.Infinite, stop.Token); }
                    catch (OperationCanceledException) { }
                }
                // Closing the first media socket must initiate a fresh 1167 auth request.
            }
        });
        var run = Task.Run(() => client.RunConnectionLoop(null, stop.Token));
        try
        {
            await secondStreamStarted.Task.WaitAsync(stop.Token);
            Assert.Equal(2, authTimes.Count);
            Assert.True(authTimes[1] - authTimes[0] >= TimeSpan.FromSeconds(1));
        }
        finally { stop.Cancel(); }
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await camera.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(client.PtzRight());
    }

    private static V380Client NewClient(TcpListener listener) =>
        new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 1, "admin", "test",
            SourceStream.Lan, OutputMode.Rtsp, false);

    private static byte[] LoginResponse(int length)
    {
        var response = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(response, 401);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(8), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(10), 1280);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(14), 720);
        return response;
    }

    private static async Task<uint> ReadCommand(NetworkStream stream, int length, CancellationToken ct)
    {
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return BinaryPrimitives.ReadUInt32LittleEndian(data);
    }
}
