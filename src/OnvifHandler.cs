using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using System.Xml.Linq;

namespace V380Decoder.src
{
  public class OnvifHandler
  {
    private static Timer ptzStopTimer;
    private static readonly object ptzLock = new();

    public static string Handle(string action, string body, HttpContext ctx, V380Client camera, int httpPort, int rtspPort, bool secure = false, string username = "", string password = "", bool transportAuthenticated = false)
    {
      var request = new OnvifSoap(body);
      action = request.Action;
      var clock = ctx.Items[OnvifClock.ContextKey] as OnvifClock ?? new OnvifClock();
      bool anonymous = action is "GetSystemDateAndTime" or "GetCapabilities" or "GetScopes";
      if (secure && (request.UsernameToken != null || (!anonymous && !transportAuthenticated)))
      {
        string error = request.ValidateUsernameToken(username, password, clock.UtcNow);
        if (error != null)
        {
          ctx.Response.StatusCode = 400;
          return OnvifSoap.Fault("NotAuthorized", error);
        }
      }
      string media = OnvifMedia.Handle(request, ctx, camera?.StreamInfo ?? new VideoStreamInfo(VideoCodec.H264, 1280, 720, 12), rtspPort);
      if (media != null)
      {
        if (XDocument.Parse(media).Descendants().Any(e => e.Name.LocalName == "Fault")) ctx.Response.StatusCode = 400;
        return media;
      }
      if (Contains(action, body, "GetSystemDateAndTime")) return clock.Get();
      else if (Contains(action, body, "SetSystemDateAndTime"))
      {
        string result = clock.Set(request);
        if (result != null) { ctx.Response.StatusCode = 400; return result; }
        return OnvifSoap.Envelope(new XElement((XNamespace)request.Namespace + "SetSystemDateAndTimeResponse"));
      }
      else if (Contains(action, body, "GetDeviceInformation")) return RespGetDeviceInformation(camera);
      else if (Contains(action, body, "GetServices")) return OnvifServices.GetServices(ctx, request.Parameter("IncludeCapability") == "true");
      else if (Contains(action, body, "GetScopes")) return RespGetScopes();
      else if (Contains(action, body, "GetNetworkInterfaces")) return RespGetNetworkInterfaces(camera);
      else if (Contains(action, body, "GetDNS")) return RespGetDNS();
      else if (Contains(action, body, "GetNTP")) return RespGetNTP();
      else if (Contains(action, body, "GetHostname")) return RespGetHostname();
      else if (Contains(action, body, "GetCapabilities")) return OnvifServices.GetCapabilities(ctx);
      else if (Contains(action, body, "GetServiceCapabilities")) return RespServiceCapabilities(ctx, request.Namespace);
      else if (Contains(action, body, "GetPresets")) return RespGetPresets();
      else if (Contains(action, body, "GetNodes")) return RespGetNodes();
      else if (Contains(action, body, "GetNode"))
      {
        if (request.Parameter("NodeToken") != "PTZNode_1")
        { ctx.Response.StatusCode = 400; return OnvifSoap.Fault("NoEntity", "Unknown PTZ node token"); }
        return RespGetNodes().Replace("GetNodesResponse", "GetNodeResponse", StringComparison.Ordinal);
      }
      else if (Contains(action, body, "GetConfigurationOptions")) return RespGetConfigOptions();
      else if (Contains(action, body, "GetConfigurations")) return RespGetConfigurations();
      else if (Contains(action, body, "GetConfiguration")) return RespGetConfigurations().Replace("GetConfigurationsResponse", "GetConfigurationResponse", StringComparison.Ordinal);
      else if (Contains(action, body, "GetStatus")) return RespGetStatus();
      else if (Contains(action, body, "ContinuousMove")) return HandleContinuousMove(body, camera);
      else if (Contains(action, body, "AbsoluteMove")) return SoapOk("AbsoluteMove");
      else if (Contains(action, body, "RelativeMove")) return SoapOk("RelativeMove");
      else if (Contains(action, body, "GotoHomePosition")) return SoapOk("GotoHomePosition");
      else if (Contains(action, body, "SetPreset")) return SoapOk("SetPreset");
      else if (Contains(action, body, "RemovePreset")) return SoapOk("RemovePreset");
      else if (Contains(action, body, "Stop")) return HandleStop(camera);
      else if (Contains(action, body, "SetImagingSettings")) return HandleSetImaging(body, camera);
      else if (Contains(action, body, "GetImagingSettings")) return RespGetImagingSettings();
      else if (Contains(action, body, "GetMoveOptions")) return RespGetMoveOptions();
      else if (Contains(action, body, "GetOptions")) return RespGetImagingOptions();
      else if (Contains(action, body, "GetNetworkProtocols")) return RespGetGetNetworkProtocols(httpPort, rtspPort);
      else if (Contains(action, body, "GetNetworkDefaultGateway")) return RespGetNetworkDefaultGateway(camera);
      else if (Contains(action, body, "GetDiscoveryMode")) return RespGetDiscoveryMode();
      else
      {
        LogUtils.debug($"[ONVIF] !! Unhandled: {action}");
        ctx.Response.StatusCode = 400;
        return SoapFault("ActionNotSupported", action);
      }
    }


    private static string HandleContinuousMove(string body, V380Client camera)
    {
      float x = ParseFloat(body, "x");
      float y = ParseFloat(body, "y");
      float durationSec = ParseFloat(body, "Timeout", 1.0f);
      int durationMs = Math.Clamp((int)(durationSec * 1000), 100, 5000);

      lock (ptzLock)
      {
        ptzStopTimer?.Dispose();

        if (x > 0.1f) { camera.PtzRight(); LogUtils.debug("[ONVIF] PTZ → RIGHT"); }
        else if (x < -0.1f) { camera.PtzLeft(); LogUtils.debug("[ONVIF] PTZ → LEFT"); }
        else if (y > 0.1f) { camera.PtzUp(); LogUtils.debug("[ONVIF] PTZ → UP"); }
        else if (y < -0.1f) { camera.PtzDown(); LogUtils.debug("[ONVIF] PTZ → DOWN"); }

        ptzStopTimer = new Timer(_ => camera.PtzStop(), null, durationMs, Timeout.Infinite);
      }

      return SoapOk("ContinuousMove");
    }

    private static string HandleStop(V380Client camera)
    {
      lock (ptzLock)
      {
        ptzStopTimer?.Dispose();
        camera.PtzStop();
      }
      LogUtils.debug("[ONVIF] PTZ → STOP");
      return SoapOk("Stop");
    }

    private static string HandleSetImaging(string body, V380Client camera)
    {
      string ir = ParseTag(body, "IrCutFilter").ToUpper();
      switch (ir)
      {
        case "ON": camera.LightOn(); LogUtils.debug("[ONVIF] Light → ON"); break;
        case "OFF": camera.LightOff(); LogUtils.debug("[ONVIF] Light → OFF"); break;
        default: camera.LightAuto(); LogUtils.debug("[ONVIF] Light → AUTO"); break;
      }
      return SoapOk("SetImagingSettings");
    }

    private static string RespGetDeviceInformation(V380Client camera) => Envelope($@"
              <tds:GetDeviceInformationResponse>
                <tds:Manufacturer>V380</tds:Manufacturer>
                <tds:Model>V380 Pro</tds:Model>
                <tds:FirmwareVersion>1.0.0</tds:FirmwareVersion>
                <tds:SerialNumber>{camera.GetDeviceId()}</tds:SerialNumber>
                <tds:HardwareId>{camera.GetDeviceVersion()}</tds:HardwareId>
              </tds:GetDeviceInformationResponse>");

    private static string RespGetScopes() => Envelope(@"
              <tds:GetScopesResponse>
                <tds:Scopes>
                  <tt:ScopeDef>Fixed</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/type/video_encoder</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Fixed</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/type/ptz</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Fixed</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/hardware/V380Pro</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Configurable</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/name/V380</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Configurable</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/location/</tt:ScopeItem>
                </tds:Scopes>
              </tds:GetScopesResponse>");

    private static string RespServiceCapabilities(HttpContext ctx, string serviceNamespace)
    {
      if (serviceNamespace == "http://www.onvif.org/ver20/imaging/wsdl")
        return OnvifSoap.Envelope(new XElement((XNamespace)serviceNamespace + "GetServiceCapabilitiesResponse", new XElement((XNamespace)serviceNamespace + "Capabilities")));
      string path = ctx.Request.Path.Value ?? "";

      if (path.Contains("ptz"))
        return RespPtzServiceCapabilities();
      else if (path.Contains("media"))
        return RespMediaServiceCapabilities();
      else
        return OnvifSoap.Envelope(new XElement((XNamespace)"http://www.onvif.org/ver10/device/wsdl" + "GetServiceCapabilitiesResponse", OnvifServices.DeviceCapabilities()));
    }

    private static string RespMediaServiceCapabilities() => Envelope(@"
              <trt:GetServiceCapabilitiesResponse>
                <trt:Capabilities SnapshotUri=""true"" Rotation=""false""
                  VideoSourceMode=""false"" OSD=""false""/>
              </trt:GetServiceCapabilitiesResponse>");

    private static string RespPtzServiceCapabilities() => Envelope(@"
              <tptz:GetServiceCapabilitiesResponse>
                <tptz:Capabilities EFlip=""false"" Reverse=""false""
                  GetCompatibleConfigurations=""false"" MoveStatus=""false""/>
              </tptz:GetServiceCapabilitiesResponse>");

    private static string RespGetNodes() => Envelope(@"
              <tptz:GetNodesResponse>
                <tptz:PTZNode token=""PTZNode_1"" FixedHomePosition=""false"">
                  <tt:Name>V380 PTZ</tt:Name>
                  <tt:SupportedPTZSpaces>
                    <tt:ContinuousPanTiltVelocitySpace>
                      <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                      <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
                    </tt:ContinuousPanTiltVelocitySpace>
                  </tt:SupportedPTZSpaces>
                  <tt:MaximumNumberOfPresets>0</tt:MaximumNumberOfPresets>
                  <tt:HomeSupported>false</tt:HomeSupported>
                </tptz:PTZNode>
              </tptz:GetNodesResponse>");

    private static string RespGetConfigurations() => Envelope(@"
              <tptz:GetConfigurationsResponse>
                <tptz:PTZConfiguration token=""PTZConfig_1"">
                  <tt:Name>PTZ</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:NodeToken>PTZNode_1</tt:NodeToken>
                </tptz:PTZConfiguration>
              </tptz:GetConfigurationsResponse>");

    private static string RespGetConfigOptions() => Envelope(@"
              <tptz:GetConfigurationOptionsResponse>
                <tptz:PTZConfigurationOptions>
                  <tt:Spaces>
                    <tt:ContinuousPanTiltVelocitySpace>
                      <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                      <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
                    </tt:ContinuousPanTiltVelocitySpace>
                  </tt:Spaces>
                  <tt:PTZTimeout><tt:Min>PT0S</tt:Min><tt:Max>PT10S</tt:Max></tt:PTZTimeout>
                </tptz:PTZConfigurationOptions>
              </tptz:GetConfigurationOptionsResponse>");

    private static string RespGetImagingSettings() => Envelope(@"
              <timg:GetImagingSettingsResponse>
                <timg:ImagingSettings>
                  <tt:Brightness>50</tt:Brightness>
                  <tt:ColorSaturation>50</tt:ColorSaturation>
                  <tt:Contrast>50</tt:Contrast>
                  <tt:Sharpness>50</tt:Sharpness>
                  <tt:IrCutFilter>AUTO</tt:IrCutFilter>
                  <tt:Exposure>
                    <tt:Mode>AUTO</tt:Mode>
                  </tt:Exposure>
                  <tt:Focus>
                    <tt:AutoFocusMode>AUTO</tt:AutoFocusMode>
                  </tt:Focus>
                  <tt:WideDynamicRange>
                    <tt:Mode>OFF</tt:Mode>
                  </tt:WideDynamicRange>
                  <tt:WhiteBalance>
                    <tt:Mode>AUTO</tt:Mode>
                  </tt:WhiteBalance>
                </timg:ImagingSettings>
              </timg:GetImagingSettingsResponse>");

    private static string RespGetNetworkInterfaces(V380Client camera)
    {
      DeviceInfo info = camera.GetDeviceInfo();
      string mac = info?.Mac ?? "00:00:00:00:00:00";
      string ip = info?.Ip ?? NetworkHelper.GetLocalIPAddress();
      int prefixLength = !string.IsNullOrEmpty(info?.Subnet) ? NetworkHelper.SubnetToPrefixLength(info.Subnet) : 24;

      return Envelope($@"
              <tds:GetNetworkInterfacesResponse>
                <tds:NetworkInterfaces token=""eth0"">
                  <tt:Enabled>true</tt:Enabled>
                  <tt:Info>
                    <tt:Name>eth0</tt:Name>
                    <tt:HwAddress>{mac}</tt:HwAddress>
                    <tt:MTU>1500</tt:MTU>
                  </tt:Info>
                  <tt:IPv4>
                    <tt:Enabled>true</tt:Enabled>
                    <tt:Config>
                      <tt:Manual>
                        <tt:Address>{ip}</tt:Address>
                        <tt:PrefixLength>{prefixLength}</tt:PrefixLength>
                      </tt:Manual>
                      <tt:DHCP>false</tt:DHCP>
                    </tt:Config>
                  </tt:IPv4>
                </tds:NetworkInterfaces>
              </tds:GetNetworkInterfacesResponse>");
    }

    private static string RespGetDNS() => Envelope(@"
              <tds:GetDNSResponse>
                <tds:DNSInformation>
                  <tt:FromDHCP>true</tt:FromDHCP>
                </tds:DNSInformation>
              </tds:GetDNSResponse>");

    private static string RespGetNTP() => Envelope(@"
              <tds:GetNTPResponse>
                <tds:NTPInformation>
                  <tt:FromDHCP>false</tt:FromDHCP>
                </tds:NTPInformation>
              </tds:GetNTPResponse>");

    private static string RespGetHostname() => Envelope(@"
              <tds:GetHostnameResponse>
                <tds:HostnameInformation>
                  <tt:FromDHCP>false</tt:FromDHCP>
                  <tt:Name>V380Pro</tt:Name>
                </tds:HostnameInformation>
              </tds:GetHostnameResponse>");

    private static string RespGetStatus() => Envelope($@"
              <tptz:GetStatusResponse>
                <tptz:PTZStatus>
                  <tt:Position>
                    <tt:PanTilt x=""0"" y=""0""
                      space=""http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace""/>
                    <tt:Zoom x=""0""
                      space=""http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace""/>
                  </tt:Position>
                  <tt:MoveStatus>
                    <tt:PanTilt>IDLE</tt:PanTilt>
                    <tt:Zoom>IDLE</tt:Zoom>
                  </tt:MoveStatus>
                  <tt:UtcTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</tt:UtcTime>
                </tptz:PTZStatus>
              </tptz:GetStatusResponse>");

    private static string RespGetPresets() => Envelope(@"
              <tptz:GetPresetsResponse>
              </tptz:GetPresetsResponse>");

    private static string RespGetMoveOptions() => Envelope(@"
              <timg:GetMoveOptionsResponse>
                <timg:MoveOptions>
                  <tt:Focus>
                    <tt:Continuous><tt:Speed><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:Speed></tt:Continuous>
                  </tt:Focus>
                </timg:MoveOptions>
              </timg:GetMoveOptionsResponse>");

    private static string RespGetImagingOptions() => Envelope(@"
              <timg:GetOptionsResponse>
                <timg:ImagingOptions>
                  <tt:Brightness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Brightness>
                  <tt:ColorSaturation><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:ColorSaturation>
                  <tt:Contrast><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Contrast>
                  <tt:Sharpness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Sharpness>
                  <tt:IrCutFilterModes>ON</tt:IrCutFilterModes>
                  <tt:IrCutFilterModes>OFF</tt:IrCutFilterModes>
                  <tt:IrCutFilterModes>AUTO</tt:IrCutFilterModes>
                  <tt:WhiteBalanceModes>AUTO</tt:WhiteBalanceModes>
                  <tt:WhiteBalanceModes>AUTO</tt:WhiteBalanceModes>
                  <tt:IrCutFilterModes>AUTO</tt:IrCutFilterModes>
                </timg:ImagingOptions>
              </timg:GetOptionsResponse>");

    private static string RespGetGetNetworkProtocols(int httpPort, int rtspPort) => Envelope($@"
            <tds:GetNetworkProtocolsResponse>
              <tds:NetworkProtocols>
                <tt:Name>HTTP</tt:Name>
                <tt:Enabled>true</tt:Enabled>
                <tt:Port>{httpPort}</tt:Port>
              </tds:NetworkProtocols>
              <tds:NetworkProtocols>
                <tt:Name>RTSP</tt:Name>
                <tt:Enabled>true</tt:Enabled>
                <tt:Port>{rtspPort}</tt:Port>
              </tds:NetworkProtocols>

            </tds:GetNetworkProtocolsResponse>");
    private static string RespGetNetworkDefaultGateway(V380Client camera)
    {
      DeviceInfo info = camera.GetDeviceInfo();
      string gateway = info?.Gateway ?? "0.0.0.0";
      return Envelope($@"
            <tds:GetNetworkDefaultGatewayResponse>
              <tds:NetworkGateway>
              <tt:IPv4Address>{gateway}</tt:IPv4Address>
              </tds:NetworkGateway>
            </tds:GetNetworkDefaultGatewayResponse>");
    }

    private static string RespGetDiscoveryMode() => Envelope($@"
            <tds:GetDiscoveryModeResponse>
              <tds:DiscoveryMode>Discoverable</tds:DiscoveryMode>
            </tds:GetDiscoveryModeResponse>");
    private static string SoapOk(string action)
    {
      string ns = (action.Contains("Move") || action.Contains("Stop") || action.Contains("Home") || action.Contains("Preset")) ? "tptz" : "trt";
      return Envelope($"<{ns}:{action}Response/>");
    }

    private static string SoapFault(string code, string detail) => OnvifSoap.Fault(code, detail);

    private static string Envelope(string body) =>
        $@"<?xml version=""1.0"" encoding=""UTF-8""?>
            <s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
                        xmlns:tt=""http://www.onvif.org/ver10/schema""
                        xmlns:tds=""http://www.onvif.org/ver10/device/wsdl""
                        xmlns:trt=""http://www.onvif.org/ver10/media/wsdl""
                        xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
                        xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl"">
            <s:Body>{body}
            </s:Body>
            </s:Envelope>";

    static bool Contains(string action, string body, string keyword)
    {
      bool match = action.Equals(keyword, StringComparison.Ordinal);
      if (match) LogUtils.debug($"[ONVIF] request: {keyword}");
      return match;
    }

    static float ParseFloat(string body, string tag, float def = 0f)
    {
      // match  tag="value"  or  <tag>value</tag>
      var m = System.Text.RegularExpressions.Regex.Match(
          body, $@"{tag}=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      if (!m.Success)
        m = System.Text.RegularExpressions.Regex.Match(
            body, $@"<[^>]*{tag}[^>]*>([^<]+)<", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      return m.Success && float.TryParse(m.Groups[1].Value,
          System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
    }

    static string ParseTag(string body, string tag)
    {
      var m = System.Text.RegularExpressions.Regex.Match(
          body, $@"<[^>]*{tag}[^>]*>([^<]*)<", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      return m.Success ? m.Groups[1].Value.Trim() : "";
    }
  }
}
