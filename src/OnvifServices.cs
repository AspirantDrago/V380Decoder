using Microsoft.AspNetCore.Http;
using System.Xml.Linq;

namespace V380Decoder.src;

internal static class OnvifServices
{
    private static readonly XNamespace D = "http://www.onvif.org/ver10/device/wsdl", T = OnvifSoap.Schema;
    internal static XElement DeviceCapabilities() => new(D + "Capabilities",
        new XElement(D + "Network", new XAttribute("IPFilter", false), new XAttribute("ZeroConfiguration", false), new XAttribute("IPVersion6", false), new XAttribute("DynDNS", false)),
        new XElement(D + "Security", new XAttribute("TLS1.0", false), new XAttribute("TLS1.1", false), new XAttribute("TLS1.2", false),
            new XAttribute("OnboardKeyGeneration", false), new XAttribute("AccessPolicyConfig", false), new XAttribute("UsernameToken", true), new XAttribute("HttpDigest", true)),
        new XElement(D + "System", new XAttribute("DiscoveryResolve", false), new XAttribute("DiscoveryBye", false), new XAttribute("RemoteDiscovery", false),
            new XAttribute("SystemBackup", false), new XAttribute("SystemLogging", false), new XAttribute("FirmwareUpgrade", false)));

    public static string GetServices(HttpContext ctx, bool includeCapabilities)
    {
        string url = OnvifEndpoints.HttpBase(ctx);
        var services = new[]
        {
            (D.NamespaceName, "device_service", DeviceCapabilities()),
            (OnvifSoap.Media2, "media2_service", OnvifMedia.Capabilities(true)),
            (OnvifSoap.Media1, "media_service", OnvifMedia.Capabilities(false)),
            ("http://www.onvif.org/ver20/ptz/wsdl", "ptz_service", new XElement((XNamespace)"http://www.onvif.org/ver20/ptz/wsdl" + "Capabilities")),
            ("http://www.onvif.org/ver20/imaging/wsdl", "imaging_service", new XElement((XNamespace)"http://www.onvif.org/ver20/imaging/wsdl" + "Capabilities"))
        };
        return OnvifSoap.Envelope(new XElement(D + "GetServicesResponse", services.Select(service => new XElement(D + "Service",
            new XElement(D + "Namespace", service.Item1), new XElement(D + "XAddr", url + "/onvif/" + service.Item2),
            includeCapabilities ? new XElement(D + "Capabilities", service.Item3) : null,
            new XElement(D + "Version", new XElement(T + "Major", 2), new XElement(T + "Minor", 60))))));
    }

    public static string GetCapabilities(HttpContext ctx) => OnvifSoap.Envelope(new XElement(D + "GetCapabilitiesResponse",
        new XElement(D + "Capabilities",
            new XElement(T + "Device", new XElement(T + "XAddr", OnvifEndpoints.HttpBase(ctx) + "/onvif/device_service")),
            new XElement(T + "Media", new XElement(T + "XAddr", OnvifEndpoints.HttpBase(ctx) + "/onvif/media_service"),
                new XElement(T + "StreamingCapabilities", new XElement(T + "RTPMulticast", false),
                    new XElement(T + "RTP_TCP", false), new XElement(T + "RTP_RTSP_TCP", true))),
            new XElement(T + "PTZ", new XElement(T + "XAddr", OnvifEndpoints.HttpBase(ctx) + "/onvif/ptz_service")),
            new XElement(T + "Imaging", new XElement(T + "XAddr", OnvifEndpoints.HttpBase(ctx) + "/onvif/imaging_service")))));
}
