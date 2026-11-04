using Microsoft.AspNetCore.Http;
using System.Xml.Linq;

namespace V380Decoder.src;

internal static class OnvifMedia
{
    private static readonly XNamespace T = OnvifSoap.Schema;
    private static XElement E(string name, object value) => new(T + name, value);
    private static XElement Entity(XName name, string token, string display, params object[] fields) =>
        new(name, new XAttribute("token", token), E("Name", display), E("UseCount", 1), fields);
    private static XElement Resolution(string name, VideoStreamInfo info) => new(T + name, E("Width", info.Width), E("Height", info.Height));
    private static XElement Multicast() => new(T + "Multicast", new XElement(T + "Address", E("Type", "IPv4"), E("IPv4Address", "0.0.0.0")),
        E("Port", 0), E("TTL", 0), E("AutoStart", false));
    private static XElement VideoSource(XName name, VideoStreamInfo info) => Entity(name, "VideoSrcCfg_1", "VideoSource",
        E("SourceToken", "VideoSource_1"), new XElement(T + "Bounds", new XAttribute("x", 0), new XAttribute("y", 0),
            new XAttribute("width", info.Width), new XAttribute("height", info.Height)));
    private static XElement AudioSource(XName name) => Entity(name, "AudioSrcCfg_1", "AudioSource", E("SourceToken", "AudioSource_1"));

    internal static XElement VideoEncoder(XName name, VideoStreamInfo info, bool media2)
    {
        var result = Entity(name, "VideoEnc_1", "VideoEncoder", E("Encoding", info.Codec.ToString()), Resolution("Resolution", info));
        if (!media2) result.Add(E("Quality", 5));
        result.Add(new XElement(T + "RateControl", E("FrameRateLimit", info.FrameRate), media2 ? null : E("EncodingInterval", 1), E("BitrateLimit", info.Bitrate)));
        if (media2) { result.SetAttributeValue("GovLength", info.GovLength); result.Add(E("Quality", 5)); }
        else result.Add(new XElement(T + "H264", E("GovLength", info.GovLength), E("H264Profile", info.H264Profile)), Multicast(), E("SessionTimeout", "PT60S"));
        return result;
    }

    private static XElement AudioEncoder(XName name, bool media2) => media2
        ? Entity(name, "AudioEnc_1", "PCMA", E("Encoding", "PCMA"), E("Bitrate", 64), E("SampleRate", 8))
        : Entity(name, "AudioEnc_1", "PCMA", E("Encoding", "G711"), E("Bitrate", 64), E("SampleRate", 8), Multicast(), E("SessionTimeout", "PT60S"));

    private static XElement Profile(XName name, VideoStreamInfo info, bool media2, HashSet<string> types)
    {
        var result = new XElement(name, new XAttribute("token", "Profile_1"), new XAttribute("fixed", true));
        XNamespace ns = media2 ? OnvifSoap.Media2 : OnvifSoap.Schema;
        result.Add(new XElement(media2 ? ns + "Name" : T + "Name", "V380 Live"));
        bool Has(string type) => !media2 || types.Count == 0 || types.Contains("All") || types.Contains(type);
        var configs = new List<XElement>();
        if (Has("VideoSource")) configs.Add(VideoSource(ns + (media2 ? "VideoSource" : "VideoSourceConfiguration"), info));
        if (Has("AudioSource")) configs.Add(AudioSource(ns + (media2 ? "AudioSource" : "AudioSourceConfiguration")));
        if (Has("VideoEncoder")) configs.Add(VideoEncoder(ns + (media2 ? "VideoEncoder" : "VideoEncoderConfiguration"), info, media2));
        if (Has("AudioEncoder")) configs.Add(AudioEncoder(ns + (media2 ? "AudioEncoder" : "AudioEncoderConfiguration"), media2));
        if (Has("PTZ")) configs.Add(Entity(ns + (media2 ? "PTZ" : "PTZConfiguration"), "PTZConfig_1", "PTZ",
            E("NodeToken", "PTZNode_1"), E("DefaultContinuousPanTiltVelocitySpace", "http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace"), E("DefaultPTZTimeout", "PT1S")));
        if (media2) result.Add(new XElement(ns + "Configurations", configs));
        else result.Add(configs);
        return result;
    }

    internal static XElement Capabilities(bool media2)
    {
        XNamespace ns = media2 ? OnvifSoap.Media2 : OnvifSoap.Media1;
        return new XElement(ns + "Capabilities", new XAttribute("SnapshotUri", true),
            new XElement(ns + "ProfileCapabilities", new XAttribute("MaximumNumberOfProfiles", 1),
                media2 ? new XAttribute("ConfigurationsSupported", "VideoSource AudioSource VideoEncoder AudioEncoder PTZ") : null),
            new XElement(ns + "StreamingCapabilities", media2 ? new XAttribute("RTSPStreaming", true) : null,
                new XAttribute("RTPMulticast", false), new XAttribute("RTP_RTSP_TCP", true)));
    }

    public static string Handle(OnvifSoap request, HttpContext ctx, VideoStreamInfo info, int rtspPort)
    {
        bool media2 = request.Namespace == OnvifSoap.Media2;
        if (!media2 && request.Namespace != OnvifSoap.Media1) return null;
        bool compatibility = !media2 && info.Codec == VideoCodec.H265;
        if (compatibility) info = H264CompatibilityStream.OutputInfo(info);
        XNamespace ns = media2 ? OnvifSoap.Media2 : OnvifSoap.Media1;
        string action = request.Action;
        string Reply(params object[] fields)
        {
            var response = new XElement(ns + (action + "Response"), fields);
            if (compatibility)
            {
                string Map(string value) => value switch { "Profile_1" => "Profile_H264", "VideoEnc_1" => "VideoEnc_H264",
                    "VideoSrcCfg_1" => "VideoSrcCfg_H264", "VideoSource_1" => "VideoSource_H264", _ => value };
                foreach (var attribute in response.DescendantsAndSelf().Attributes("token")) attribute.Value = Map(attribute.Value);
                foreach (var source in response.Descendants(T + "SourceToken")) source.Value = Map(source.Value);
            }
            return OnvifSoap.Envelope(response);
        }
        string profile = request.Parameter(media2 && action == "GetProfiles" ? "Token" : "ProfileToken");
        if (profile != null && profile != (compatibility ? "Profile_H264" : "Profile_1")) return OnvifSoap.Fault("NoProfile", "Unknown profile token");
        string token = request.Parameter("ConfigurationToken");
        string expectedToken = action.Contains("VideoEncoder") ? "VideoEnc_1" : action.Contains("AudioEncoder") ? "AudioEnc_1" :
            action.Contains("VideoSource") ? "VideoSrcCfg_1" : action.Contains("AudioSource") ? "AudioSrcCfg_1" : null;
        if (compatibility && expectedToken is "VideoEnc_1" or "VideoSrcCfg_1") expectedToken = expectedToken.Replace("_1", "_H264");
        if (token != null && token != expectedToken) return OnvifSoap.Fault("NoConfig", "Unknown configuration token");
        switch (action)
        {
            case "GetProfiles":
            case "GetProfile":
                var types = request.Operation.Elements(ns + "Type").Select(e => e.Value).ToHashSet();
                return Reply(Profile(ns + (action == "GetProfiles" ? "Profiles" : "Profile"), info, media2, types));
            case "GetStreamUri":
                string protocol = request.Operation.Descendants().FirstOrDefault(e => e.Name.LocalName == "Protocol")?.Value;
                string streamType = request.Operation.Descendants().FirstOrDefault(e => e.Name.LocalName == "Stream")?.Value;
                if ((media2 && protocol != null && protocol != "RtspUnicast" && protocol != "RTSP") ||
                    (!media2 && (streamType == "RTP-Multicast" || protocol == "HTTP")))
                    return OnvifSoap.Fault("ActionNotSupported", "Only unicast RTSP streaming is supported");
                string uri = compatibility ? OnvifEndpoints.CompatibilityStreamUri(ctx, rtspPort) : OnvifEndpoints.StreamUri(ctx, rtspPort);
                return media2 ? Reply(new XElement(ns + "Uri", uri)) : Reply(new XElement(ns + "MediaUri",
                    E("Uri", uri), E("InvalidAfterConnect", false), E("InvalidAfterReboot", false), E("Timeout", "PT0S")));
            case "GetSnapshotUri":
                string snapshot = OnvifEndpoints.SnapshotUri(ctx);
                return media2 ? Reply(new XElement(ns + "Uri", snapshot)) : Reply(new XElement(ns + "MediaUri",
                    E("Uri", snapshot), E("InvalidAfterConnect", false), E("InvalidAfterReboot", false), E("Timeout", "PT0S")));
            case "GetVideoEncoderConfigurations":
            case "GetVideoEncoderConfiguration":
            case "GetCompatibleVideoEncoderConfigurations":
                return Reply(VideoEncoder(ns + (action == "GetVideoEncoderConfiguration" ? "Configuration" : "Configurations"), info, media2));
            case "GetAudioEncoderConfigurations":
            case "GetAudioEncoderConfiguration":
            case "GetCompatibleAudioEncoderConfigurations":
                return Reply(AudioEncoder(ns + (action == "GetAudioEncoderConfiguration" ? "Configuration" : "Configurations"), media2));
            case "GetVideoSourceConfigurations":
            case "GetVideoSourceConfiguration":
            case "GetCompatibleVideoSourceConfigurations":
                return Reply(VideoSource(ns + (action == "GetVideoSourceConfiguration" ? "Configuration" : "Configurations"), info));
            case "GetAudioSourceConfigurations":
            case "GetAudioSourceConfiguration":
            case "GetCompatibleAudioSourceConfigurations":
                return Reply(AudioSource(ns + (action == "GetAudioSourceConfiguration" ? "Configuration" : "Configurations")));
            case "GetVideoSources":
                return Reply(new XElement(ns + "VideoSources", new XAttribute("token", "VideoSource_1"), E("Framerate", info.FrameRate), Resolution("Resolution", info)));
            case "GetAudioSources": return Reply(new XElement(ns + "AudioSources", new XAttribute("token", "AudioSource_1"), E("Channels", 1)));
            case "GetVideoEncoderConfigurationOptions":
                var range = new XElement(T + "QualityRange", E("Min", 5), E("Max", 5));
                if (media2) return Reply(new XElement(ns + "Options", new XAttribute("GovLengthRange", "30 30"),
                    new XAttribute("FrameRatesSupported", info.FrameRate), E("Encoding", info.Codec.ToString()), range,
                    Resolution("ResolutionsAvailable", info), new XElement(T + "BitrateRange", E("Min", 4096), E("Max", 4096))));
                return Reply(new XElement(ns + "Options", range, new XElement(T + "H264", Resolution("ResolutionsAvailable", info),
                    new XElement(T + "GovLengthRange", E("Min", info.GovLength), E("Max", info.GovLength)), new XElement(T + "FrameRateRange", E("Min", info.FrameRate), E("Max", info.FrameRate)),
                    new XElement(T + "EncodingIntervalRange", E("Min", 1), E("Max", 1)), E("H264ProfilesSupported", info.H264Profile))));
            case "GetAudioEncoderConfigurationOptions":
                var option = new XElement(media2 ? ns + "Options" : T + "Options", E("Encoding", media2 ? "PCMA" : "G711"),
                    new XElement(T + "BitrateList", E("Items", 64)), new XElement(T + "SampleRateList", E("Items", 8)));
                return media2 ? Reply(option) : Reply(new XElement(ns + "Options", option));
            case "GetServiceCapabilities":
                return Reply(Capabilities(media2));
            default: return media2 ? OnvifSoap.Fault("ActionNotSupported", action) : null;
        }
    }
}
