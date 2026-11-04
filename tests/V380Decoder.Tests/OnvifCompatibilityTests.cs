using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class OnvifCompatibilityTests
{
    private static readonly XNamespace T = OnvifSoap.Schema;
    internal static string Soap(string action, string ns, object fields = null, XElement header = null, string version = OnvifSoap.Soap12) =>
        new XElement((XNamespace)version + "Envelope", header == null ? null : new XElement((XNamespace)version + "Header", header),
            new XElement((XNamespace)version + "Body", new XElement((XNamespace)ns + action, fields))).ToString();

    internal static string Digest(string challenge, string method, string target, string username = "admin", string password = "test")
    {
        string Field(string name) => Regex.Match(challenge, name + "=\"([^\"]+)\"").Groups[1].Value;
        string H(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        string realm = Field("realm"), nonce = Field("nonce");
        string response = H($"{H(username + ":" + realm + ":" + password)}:{nonce}:00000001:clientnonce:auth:{H(method + ":" + target)}");
        return $"Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{target}\", algorithm=MD5, qop=auth, nc=00000001, cnonce=\"clientnonce\", response=\"{response}\"";
    }

    private static XElement Token(string username, string password, bool invalid = false)
    {
        XNamespace ws = OnvifSoap.Wsse, util = OnvifSoap.Wsu;
        string created = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        byte[] nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        string digest = Convert.ToBase64String(SHA1.HashData([..nonce, ..Encoding.UTF8.GetBytes(created), ..Encoding.UTF8.GetBytes(password)]));
        return new XElement(ws + "Security",
            new XElement(util + "Timestamp", new XElement(util + "Created", "2000-01-01T00:00:00Z")),
            new XElement(ws + "UsernameToken", new XElement(ws + "Username", username),
                new XElement(ws + "Password", new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"), invalid ? "AAAA" : digest),
                new XElement(ws + "Nonce", Convert.ToBase64String(nonce)), new XElement(util + "Created", created)));
    }

    [Fact]
    public void UsernameTokenUsesItsOwnTimestampAndDecodesXmlEscaping()
    {
        string xml = Soap("GetProfiles", OnvifSoap.Media1, header: Token("admin&camera", "test"));
        var request = new OnvifSoap(xml.Replace("Type=\"", "Type='", StringComparison.Ordinal).Replace("#PasswordDigest\"", "#PasswordDigest'", StringComparison.Ordinal));
        Assert.Null(request.ValidateUsernameToken("admin&camera", "test"));
        Assert.NotNull(request.ValidateUsernameToken("admin&camera", "wrong"));
    }

    [Fact]
    public void CredentialsAndActionHeaderCannotBypassBodyOperationAuthentication()
    {
        var ctx = new DefaultHttpContext();
        string xml = Soap("GetProfiles", OnvifSoap.Media1, header: Token("GetSystemDateAndTime", "wrong"));
        string response = OnvifHandler.Handle("GetSystemDateAndTime", xml, ctx, null, 8080, 8554, true, "admin", "test");
        Assert.Equal(400, ctx.Response.StatusCode);
        var doc = XDocument.Parse(response);
        Assert.Contains("NotAuthorized", response);
        Assert.Equal("en", (string)doc.Descendants((XNamespace)OnvifSoap.Soap12 + "Text").Single().Attribute(XNamespace.Xml + "lang"));
    }

    [Fact]
    public void DigestBindsPasswordMethodTargetAndServerNonce()
    {
        var auth = new DigestAuthentication();
        string challenge = auth.Challenge();
        string header = Digest(challenge, "POST", "/onvif/device_service");
        Assert.True(auth.Verify(header, "POST", "/onvif/device_service", "admin", "test"));
        Assert.False(auth.Verify(header, "POST", "/onvif/media_service", "admin", "test"));
        Assert.False(auth.Verify(header, "GET", "/onvif/device_service", "admin", "test"));
        Assert.False(auth.Verify(header, "POST", "/onvif/device_service", "admin", "wrong"));
        Assert.False(new DigestAuthentication().Verify(header, "POST", "/onvif/device_service", "admin", "test"));
        Assert.False(auth.Verify(header + ", username=\"admin\"", "POST", "/onvif/device_service", "admin", "test"));
    }

    [Theory]
    [InlineData(OnvifSoap.Soap11)]
    [InlineData(OnvifSoap.Soap12)]
    public void SoapResponsePreservesVersionAndAddressingCorrelation(string version)
    {
        XNamespace addressing = "http://www.w3.org/2005/08/addressing";
        var envelope = XDocument.Parse(Soap("GetCapabilities", "http://www.onvif.org/ver10/device/wsdl", version: version));
        envelope.Root.AddFirst(new XElement((XNamespace)version + "Header", new XElement(addressing + "MessageID", "urn:uuid:request"),
            new XElement(addressing + "ReplyTo", new XElement(addressing + "Address", "http://www.w3.org/2005/08/addressing/anonymous"))));
        var request = new OnvifSoap(envelope.ToString());
        var reply = XDocument.Parse(request.FormatResponse(OnvifServices.GetCapabilities(Context())));
        Assert.Equal(version, reply.Root.Name.NamespaceName);
        Assert.Equal("urn:uuid:request", reply.Descendants(addressing + "RelatesTo").Single().Value);
        Assert.EndsWith("/GetCapabilitiesResponse", reply.Descendants(addressing + "Action").Single().Value);
        Assert.Equal("http://www.w3.org/2005/08/addressing/anonymous", reply.Descendants(addressing + "To").Single().Value);
    }

    [Fact]
    public void Media1ProfilesAreCompleteConsistentAndSchemaOrdered()
    {
        var ctx = Context();
        var info = new VideoStreamInfo(VideoCodec.H264, 1920, 1080, 15);
        var all = Media("GetProfiles", OnvifSoap.Media1, ctx, info);
        var single = Media("GetProfile", OnvifSoap.Media1, ctx, info, new XElement((XNamespace)OnvifSoap.Media1 + "ProfileToken", "Profile_1"));
        XElement profile = all.Descendants((XNamespace)OnvifSoap.Media1 + "Profiles").Single();
        Assert.Equal(profile.Elements().Select(e => e.Name), single.Descendants((XNamespace)OnvifSoap.Media1 + "Profile").Single().Elements().Select(e => e.Name));
        Assert.Equal(new[] { "Name", "VideoSourceConfiguration", "AudioSourceConfiguration", "VideoEncoderConfiguration", "AudioEncoderConfiguration", "PTZConfiguration" }, profile.Elements().Select(e => e.Name.LocalName));
        var video = profile.Element(T + "VideoEncoderConfiguration");
        Assert.Equal(new[] { "Name", "UseCount", "Encoding", "Resolution", "Quality", "RateControl", "H264", "Multicast", "SessionTimeout" }, video.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("1920", video.Element(T + "Resolution").Element(T + "Width").Value);
        Assert.Equal("15", video.Element(T + "RateControl").Element(T + "FrameRateLimit").Value);
        Assert.Equal("G711", profile.Element(T + "AudioEncoderConfiguration").Element(T + "Encoding").Value);
        var encoder = Media("GetVideoEncoderConfigurations", OnvifSoap.Media1, ctx, info).Descendants((XNamespace)OnvifSoap.Media1 + "Configurations").Single();
        Assert.True(XNode.DeepEquals(video.Elements().First(), encoder.Elements().First()));
        Assert.True(video.Elements().Zip(encoder.Elements()).All(pair => XNode.DeepEquals(pair.First, pair.Second)));
        Assert.Equal((string)video.Attribute("token"), (string)encoder.Attribute("token"));
    }

    [Fact]
    public void Media2ReportsHevcAndRealResolutionWithoutInventingLegacyH264()
    {
        var info = new VideoStreamInfo(VideoCodec.H265, 1920, 3240, 12);
        var doc = Media("GetProfiles", OnvifSoap.Media2, Context(), info);
        XNamespace ns = OnvifSoap.Media2;
        var encoder = doc.Descendants(ns + "VideoEncoder").Single();
        Assert.Equal("H265", encoder.Element(T + "Encoding").Value);
        Assert.Equal("3240", encoder.Element(T + "Resolution").Element(T + "Height").Value);
        Assert.Equal(new[] { "Name", "UseCount", "Encoding", "Resolution", "RateControl", "Quality" }, encoder.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("PCMA", doc.Descendants(ns + "AudioEncoder").Single().Element(T + "Encoding").Value);
        Assert.Empty(Media("GetProfiles", OnvifSoap.Media1, Context(), info).Descendants((XNamespace)OnvifSoap.Media1 + "Profiles"));
        Assert.Contains("Media2", Media("GetProfiles", OnvifSoap.Media1, Context(), info).ToString());
    }

    [Fact]
    public void HevcSpsUpdatesAdvertisedVideoMetadata()
    {
        using var client = new V380Client("127.0.0.1", 8800, 1, "admin", "test", SourceStream.Lan, OutputMode.Rtsp, false);
        Assert.False(client.StreamInfo.Known);
        client.UpdateVideoInfo(VideoPayloadDecoderTests.BuildHevcAccessUnit(), VideoCodec.H265, 15, true);
        Assert.True(client.VideoReady.IsCompletedSuccessfully);
        Assert.Equal(new VideoStreamInfo(VideoCodec.H265, 64, 64, 15), client.StreamInfo);
    }

    [Theory]
    [InlineData(OnvifSoap.Media1)]
    [InlineData(OnvifSoap.Media2)]
    public void UnsupportedStreamingProtocolsReturnAnExplicitFault(string ns)
    {
        object fields = ns == OnvifSoap.Media2 ? new XElement((XNamespace)ns + "Protocol", "RtspMulticast") :
            new XElement((XNamespace)ns + "StreamSetup", new XElement(T + "Stream", "RTP-Multicast"));
        Assert.Contains("ActionNotSupported", Media("GetStreamUri", ns, Context(), new VideoStreamInfo(VideoCodec.H264, 1280, 720, 12), fields).ToString());
    }

    [Fact]
    public void Media2HonorsTypeFilterAndRejectsUnknownTokens()
    {
        XNamespace ns = OnvifSoap.Media2;
        var info = new VideoStreamInfo(VideoCodec.H265, 1920, 1080, 12);
        var doc = Media("GetProfiles", ns.NamespaceName, Context(), info, new XElement(ns + "Type", "VideoEncoder"));
        Assert.Single(doc.Descendants(ns + "Configurations").Single().Elements());
        Assert.Contains("NoProfile", Media("GetProfiles", ns.NamespaceName, Context(), info, new XElement(ns + "Token", "missing")).ToString());
    }

    [Fact]
    public void ServicesAdvertiseMedia2CorrectPtzNamespaceAndRequestedCapabilities()
    {
        var ctx = Context();
        var doc = XDocument.Parse(OnvifServices.GetServices(ctx, true));
        XNamespace ns = "http://www.onvif.org/ver10/device/wsdl";
        var services = doc.Descendants(ns + "Service").ToArray();
        Assert.Equal(5, services.Length);
        Assert.All(services, e => Assert.NotEmpty(e.Element(ns + "Capabilities").Elements()));
        Assert.Contains(services, e => e.Element(ns + "Namespace").Value == OnvifSoap.Media2);
        Assert.Contains(services, e => e.Element(ns + "Namespace").Value == "http://www.onvif.org/ver20/ptz/wsdl");
        Assert.All(services, e => Assert.StartsWith("http://192.0.2.10:8081/onvif/", e.Element(ns + "XAddr").Value));
        Assert.Empty(XDocument.Parse(OnvifServices.GetServices(ctx, false)).Descendants(ns + "Service").Elements(ns + "Capabilities"));
    }

    [Fact]
    public void PtzConfigurationUsesAdvertisedNamespaceAndSingularResponse()
    {
        const string ns = "http://www.onvif.org/ver20/ptz/wsdl";
        var ctx = Context();
        ctx.Request.Path = "/onvif/ptz_service";
        string body = Soap("GetConfiguration", ns, new XElement((XNamespace)ns + "PTZConfigurationToken", "PTZConfig_1"));
        var doc = XDocument.Parse(OnvifHandler.Handle("GetConfiguration", body, ctx, null, 8081, 8554));
        var configuration = doc.Descendants((XNamespace)ns + "GetConfigurationResponse").Single().Element((XNamespace)ns + "PTZConfiguration");
        Assert.Equal("PTZConfig_1", (string)configuration.Attribute("token"));
        Assert.NotNull(configuration.Element(T + "UseCount"));
    }

    [Theory]
    [InlineData(OnvifSoap.Media1)]
    [InlineData(OnvifSoap.Media2)]
    public void EndpointOverridesPreservePublishedPorts(string ns)
    {
        var ctx = Context();
        ctx.Items[OnvifEndpoints.HttpKey] = "http://192.0.2.10:9001";
        ctx.Items[OnvifEndpoints.RtspKey] = "rtsp://192.0.2.10:9554/live";
        var info = new VideoStreamInfo(VideoCodec.H264, 1280, 720, 12);
        Assert.Equal("rtsp://192.0.2.10:9554/live", Media("GetStreamUri", ns, ctx, info).Descendants().Single(e => e.Name.LocalName == "Uri").Value);
        Assert.Equal("http://192.0.2.10:9001/snapshot", Media("GetSnapshotUri", ns, ctx, info).Descendants().Single(e => e.Name.LocalName == "Uri").Value);
    }

    [Theory]
    [InlineData("http://schemas.xmlsoap.org/ws/2004/08/addressing", "http://schemas.xmlsoap.org/ws/2005/04/discovery")]
    [InlineData("http://www.w3.org/2005/08/addressing", "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01")]
    public void DiscoveryUsesStableUniqueCameraIdentityAndCorrelatesProbes(string addressing, string discovery)
    {
        using var first = new OnvifDiscovery(8081, "camera1");
        using var second = new OnvifDiscovery(8082, "camera2", "http://192.0.2.10:9002");
        var header = new XElement((XNamespace)addressing + "MessageID", "urn:uuid:test&escaped");
        string request = Soap("Probe", discovery, header: header);
        var response = XDocument.Parse(first.BuildProbeMatch(request, "192.0.2.10"));
        var repeated = XDocument.Parse(first.BuildProbeMatch(request, "192.0.2.10"));
        var other = XDocument.Parse(second.BuildProbeMatch(request, "172.17.0.2"));
        string Id(XDocument d) => d.Descendants((XNamespace)addressing + "EndpointReference").Single().Element((XNamespace)addressing + "Address").Value;
        Assert.StartsWith("urn:uuid:", Id(response));
        Assert.Equal(Id(response), Id(repeated));
        Assert.NotEqual(Id(response), Id(other));
        Assert.Equal("urn:uuid:test&escaped", response.Descendants((XNamespace)addressing + "RelatesTo").Single().Value);
        Assert.Equal("http://192.0.2.10:9002/onvif/device_service", other.Descendants((XNamespace)discovery + "XAddrs").Single().Value);
    }

    [Fact]
    public async Task HttpDigestAndWsSecurityWorkThroughRealHttpEndpoint()
    {
        var server = new WebServer(0, 8554, null, true, true, false, true, "admin", "test");
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}") };
            var xml = Soap("GetProfiles", OnvifSoap.Media1);
            using var initial = await http.PostAsync("/onvif/media_service", new StringContent(xml, Encoding.UTF8, "application/soap+xml"));
            Assert.Equal(HttpStatusCode.Unauthorized, initial.StatusCode);
            string challenge = initial.Headers.WwwAuthenticate.Single().ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/onvif/media_service") { Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml") };
            request.Headers.TryAddWithoutValidation("Authorization", Digest(challenge, "POST", "/onvif/media_service"));
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("VideoEncoderConfiguration", await response.Content.ReadAsStringAsync());
            using var wss = await http.PostAsync("/onvif/media_service", new StringContent(Soap("GetProfiles", OnvifSoap.Media1, header: Token("admin", "test")), Encoding.UTF8, "application/soap+xml"));
            Assert.Equal(HttpStatusCode.OK, wss.StatusCode);
            using var both = new HttpRequestMessage(HttpMethod.Post, "/onvif/media_service")
            { Content = new StringContent(Soap("GetProfiles", OnvifSoap.Media1, header: Token("admin", "test", true)), Encoding.UTF8, "application/soap+xml") };
            both.Headers.TryAddWithoutValidation("Authorization", Digest(challenge, "POST", "/onvif/media_service"));
            using var rejected = await http.SendAsync(both);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("NotAuthorized", await rejected.Content.ReadAsStringAsync());
            using var soap11 = await http.PostAsync("/onvif/device_service", new StringContent(Soap("GetSystemDateAndTime", "http://www.onvif.org/ver10/device/wsdl", version: OnvifSoap.Soap11), Encoding.UTF8, "text/xml"));
            Assert.Equal(HttpStatusCode.OK, soap11.StatusCode);
            Assert.Equal(OnvifSoap.Soap11, XDocument.Parse(await soap11.Content.ReadAsStringAsync()).Root.Name.NamespaceName);
            using var apiChallenge = await http.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.Unauthorized, apiChallenge.StatusCode);
            string getChallenge = apiChallenge.Headers.WwwAuthenticate.Single(h => h.Scheme == "Digest").ToString();
            using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            apiRequest.Headers.TryAddWithoutValidation("Authorization", Digest(getChallenge, "GET", "/api/status"));
            using var apiResponse = await http.SendAsync(apiRequest);
            Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
        }
        finally { server.Stop(); }
    }

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("192.0.2.10", 8081);
        return ctx;
    }

    private static XDocument Media(string action, string ns, HttpContext ctx, VideoStreamInfo info, object fields = null) =>
        XDocument.Parse(OnvifMedia.Handle(new OnvifSoap(Soap(action, ns, fields)), ctx, info, 8554));
}
