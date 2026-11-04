using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class CameraNamingTests
{
    [Theory]
    [InlineData(null, "V380")]
    [InlineData("Вход & двор / №1", "Вход & двор / №1")]
    public async Task ContainerNameAppearsInOnvifScopesProfilesAndDiscovery(string configured, string expected)
    {
        var server = new WebServer(0, 8554, null, false, true, false, false, "", "", cameraName: configured);
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}") };
            const string device = "http://www.onvif.org/ver10/device/wsdl";
            using var scopes = await http.PostAsync("/onvif/device_service", new StringContent(OnvifCompatibilityTests.Soap("GetScopes", device), Encoding.UTF8, "application/soap+xml"));
            Assert.Equal(HttpStatusCode.OK, scopes.StatusCode);
            var doc = XDocument.Parse(await scopes.Content.ReadAsStringAsync());
            string scope = doc.Descendants((XNamespace)OnvifSoap.Schema + "ScopeItem").Single(e => e.Value.Contains("/name/")).Value;
            Assert.DoesNotContain(" ", scope);
            Assert.Equal(expected, Uri.UnescapeDataString(scope.Split("/name/")[1]));
            foreach (string ns in new[] { OnvifSoap.Media1, OnvifSoap.Media2 })
            {
                using var profiles = await http.PostAsync(ns == OnvifSoap.Media1 ? "/onvif/media_service" : "/onvif/media2_service",
                    new StringContent(OnvifCompatibilityTests.Soap("GetProfiles", ns), Encoding.UTF8, "application/soap+xml"));
                Assert.Equal(HttpStatusCode.OK, profiles.StatusCode);
                doc = XDocument.Parse(await profiles.Content.ReadAsStringAsync());
                Assert.Equal(expected, doc.Descendants((XNamespace)ns + "Profiles").Single().Elements().First(e => e.Name.LocalName == "Name").Value);
            }
            const string d = "http://schemas.xmlsoap.org/ws/2005/04/discovery", w = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            string probe = OnvifCompatibilityTests.Soap("Probe", d, header: new XElement((XNamespace)w + "MessageID", "urn:uuid:test"));
            using var discovery = new OnvifDiscovery(8080, "1234", cameraName: configured);
            doc = XDocument.Parse(discovery.BuildProbeMatch(probe, "127.0.0.1"));
            Assert.Contains(scope, doc.Descendants((XNamespace)d + "Scopes").Single().Value.Split(' '));
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(null, "V380")]
    [InlineData("Вход & двор", "Вход & двор")]
    public async Task RtspDescribeUsesUtf8AndCorrectContentLength(string configured, string expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var sender = await listener.AcceptTcpClientAsync();
        var server = new RtspServer(0, false, "", "", configured);
        var session = new RtspSession(1, sender, server);
        session.Start();
        try
        {
            var stream = receiver.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("DESCRIBE rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\n\r\n"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new List<byte>();
            byte[] one = new byte[1];
            while (!bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
            {
                await stream.ReadExactlyAsync(one, timeout.Token);
                bytes.Add(one[0]);
            }
            string header = Encoding.ASCII.GetString(bytes.ToArray());
            int length = int.Parse(header.Split("\r\n").Single(s => s.StartsWith("Content-Length:")).Split(':')[1]);
            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, timeout.Token);
            Assert.Equal(server.BuildSdp(), new UTF8Encoding(false, true).GetString(body));
            Assert.Contains("s=" + expected + "\r\n", Encoding.UTF8.GetString(body));
        }
        finally { session.Close(); server.Dispose(); }
    }
}
