using Microsoft.AspNetCore.Http;
using System.Xml.Linq;

namespace V380Decoder.src;

// Read-only channel title metadata, configured by --camera-name.
internal static class OnvifOsd
{
    internal static string Handle(OnvifSoap request, HttpContext ctx, bool compatibility)
    {
        XNamespace ns = request.Namespace, t = OnvifSoap.Schema;
        bool media2 = ns == OnvifSoap.Media2;
        string source = compatibility ? "VideoSrcCfg_H264" : "VideoSrcCfg_1";
        string token = compatibility ? "OSD_Name_H264" : "OSD_Name_1";
        string configuration = request.Parameter("ConfigurationToken");
        if (configuration != null && configuration != source)
            return OnvifSoap.Fault("NoConfig", "Unknown video source configuration token");
        string suppliedToken = request.Parameter("OSDToken");
        if ((suppliedToken != null && suppliedToken != token) || (request.Action == "GetOSD" && suppliedToken == null))
            return OnvifSoap.Fault("NoOSD", "Unknown OSD token");
        string Reply(XElement value) => OnvifSoap.Envelope(new XElement(ns + (request.Action + "Response"), value));
        switch (request.Action)
        {
            case "GetOSDs":
            case "GetOSD" when !media2:
                return Reply(new XElement(ns + (request.Action == "GetOSDs" ? "OSDs" : "OSD"), new XAttribute("token", token),
                    new XElement(t + "VideoSourceConfigurationToken", source), new XElement(t + "Type", "Text"),
                    new XElement(t + "Position", new XElement(t + "Type", "LowerLeft")),
                    new XElement(t + "TextString", new XAttribute("IsPersistentText", true),
                        new XElement(t + "Type", "Plain"), new XElement(t + "PlainText", CameraName.From(ctx, "V380")))));
            case "GetOSDOptions":
                if (configuration == null) return OnvifSoap.Fault("NoConfig", "Missing video source configuration token");
                return Reply(new XElement(ns + "OSDOptions",
                    new XElement(t + "MaximumNumberOfOSDs", new XAttribute("Total", 1), new XAttribute("PlainText", 1)),
                    new XElement(t + "Type", "Text"), new XElement(t + "PositionOption", "LowerLeft"),
                    new XElement(t + "TextOption", new XElement(t + "Type", "Plain"))));
            default:
                return OnvifSoap.Fault("ActionNotSupported", "Channel title is read-only; configure --camera-name in the container arguments");
        }
    }
}
