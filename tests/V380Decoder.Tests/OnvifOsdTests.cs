using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class OnvifOsdTests
{
    private static readonly XNamespace T = OnvifSoap.Schema;

    [Theory]
    [InlineData(OnvifSoap.Media1, VideoCodec.H264, "VideoSrcCfg_1")]
    [InlineData(OnvifSoap.Media1, VideoCodec.H265, "VideoSrcCfg_H264")]
    [InlineData(OnvifSoap.Media2, VideoCodec.H265, "VideoSrcCfg_1")]
    public void OsdNameReferencesTheAdvertisedSourceAndSupportsTokenReads(string service, VideoCodec codec, string source)
    {
        XNamespace ns = service;
        var ctx = new DefaultHttpContext();
        ctx.Items[CameraName.ContextKey] = "Вход & двор <1>";
        var info = new VideoStreamInfo(codec, 1920, 1080, 12);
        XDocument Call(string action, object fields = null) => XDocument.Parse(OnvifMedia.Handle(
            new OnvifSoap(OnvifCompatibilityTests.Soap(action, service, fields)), ctx, info, 8554));
        var capabilities = Call("GetServiceCapabilities").Descendants(ns + "Capabilities").Single();
        Assert.Equal("true", (string)capabilities.Attribute("OSD"));
        var profile = Call("GetProfiles");
        string advertised = (string)profile.Descendants(ns == OnvifSoap.Media2 ? ns + "VideoSource" : T + "VideoSourceConfiguration").Single().Attribute("token");
        Assert.Equal(source, advertised);
        var all = Call("GetOSDs");
        var osd = all.Descendants(ns + "OSDs").Single();
        string token = (string)osd.Attribute("token");
        Assert.Equal(new[] { "VideoSourceConfigurationToken", "Type", "Position", "TextString" }, osd.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(source, osd.Element(T + "VideoSourceConfigurationToken").Value);
        Assert.Equal("Text", osd.Element(T + "Type").Value);
        Assert.Equal("Plain", osd.Element(T + "TextString").Element(T + "Type").Value);
        Assert.Equal("Вход & двор <1>", osd.Descendants(T + "PlainText").Single().Value);
        Assert.Equal("true", (string)osd.Element(T + "TextString").Attribute("IsPersistentText"));
        var filtered = Call("GetOSDs", new XElement(ns + "ConfigurationToken", source)).Descendants(ns + "OSDs").Single();
        Assert.True(XNode.DeepEquals(osd, filtered));
        var single = Call(service == OnvifSoap.Media1 ? "GetOSD" : "GetOSDs", new XElement(ns + "OSDToken", token));
        Assert.Equal("Вход & двор <1>", single.Descendants(T + "PlainText").Single().Value);
        Assert.Contains("NoOSD", Call(service == OnvifSoap.Media1 ? "GetOSD" : "GetOSDs", new XElement(ns + "OSDToken", "bad")).ToString());
        Assert.Contains("NoConfig", Call("GetOSDs", new XElement(ns + "ConfigurationToken", "bad")).ToString());
        var options = Call("GetOSDOptions", new XElement(ns + "ConfigurationToken", source));
        Assert.Equal("1", (string)options.Descendants(T + "MaximumNumberOfOSDs").Single().Attribute("PlainText"));
        Assert.Equal("Plain", options.Descendants(T + "TextOption").Single().Element(T + "Type").Value);
        foreach (string action in new[] { "SetOSD", "CreateOSD", "DeleteOSD" })
            Assert.Contains("ActionNotSupported", Call(action).ToString());
        Assert.Equal("Вход & двор <1>", Call("GetOSDs").Descendants(T + "PlainText").Single().Value);
    }

    [Theory]
    [InlineData(null, "V380")]
    [InlineData("Вход & двор", "Вход & двор")]
    public async Task OsdReadsWorkThroughAuthenticatedHttpAndServicesAdvertiseSupport(string name, string expected)
    {
        var server = new WebServer(0, 8554, null, false, true, false, true, "admin", "test", cameraName: name);
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}") };
            const string path = "/onvif/media_service";
            string xml = OnvifCompatibilityTests.Soap("GetOSDs", OnvifSoap.Media1);
            using var initial = await http.PostAsync(path, new StringContent(xml, Encoding.UTF8, "application/soap+xml"));
            Assert.Equal(HttpStatusCode.Unauthorized, initial.StatusCode);
            string challenge = initial.Headers.WwwAuthenticate.Single().ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml") };
            request.Headers.TryAddWithoutValidation("Authorization", OnvifCompatibilityTests.Digest(challenge, "POST", path));
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected, XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(T + "PlainText").Single().Value);
            var ctx = new DefaultHttpContext();
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("localhost", server.BoundPort);
            var services = XDocument.Parse(OnvifServices.GetServices(ctx, true));
            foreach (string ns in new[] { OnvifSoap.Media1, OnvifSoap.Media2 })
                Assert.Equal("true", (string)services.Descendants((XNamespace)ns + "Capabilities").Single().Attribute("OSD"));
        }
        finally { server.Stop(); }
    }
}
