using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace V380Decoder.src;

public class OnvifDiscovery : IDisposable
{
    private readonly int port;
    private readonly string advertisedUrl;
    private readonly string endpointId;
    private readonly long instanceId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long messageNumber;
    private UdpClient udp;
    private Thread acceptThread;
    private volatile bool running;

    public OnvifDiscovery(int port, string deviceIdentity = null, string advertisedUrl = null)
    {
        this.port = port;
        this.advertisedUrl = advertisedUrl;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("V380Decoder:" + (deviceIdentity ?? port.ToString())));
        endpointId = "urn:uuid:" + new Guid(hash.AsSpan(0, 16));
    }

    public void Start()
    {
        udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 3702));
        udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "onvif-discovery" };
        acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (running)
        {
            try
            {
                IPEndPoint remote = null;
                var packet = udp.Receive(ref remote);
                string response = BuildProbeMatch(Encoding.UTF8.GetString(packet), NetworkHelper.GetLocalIPAddressFor(remote.Address));
                if (response == null) continue;
                byte[] bytes = Encoding.UTF8.GetBytes(response);
                udp.Send(bytes, bytes.Length, remote);
                LogUtils.debug($"[ONVIF] ProbeMatches to {remote.Address} endpoint={endpointId}");
            }
            catch (Exception ex) { if (running) LogUtils.debug($"[ONVIF] discovery: {ex.Message}"); }
        }
    }

    internal string BuildProbeMatch(string xml, string localAddress)
    {
        var request = new OnvifSoap(xml);
        if (request.Action != "Probe" || (request.Namespace != "http://schemas.xmlsoap.org/ws/2005/04/discovery" &&
            request.Namespace != "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01")) return null;
        var id = request.Header?.Elements().FirstOrDefault(e => e.Name.LocalName == "MessageID");
        if (id == null || (id.Name.NamespaceName != "http://schemas.xmlsoap.org/ws/2004/08/addressing" &&
            id.Name.NamespaceName != "http://www.w3.org/2005/08/addressing")) return null;
        XNamespace soap = OnvifSoap.Soap12, discovery = request.Namespace, addressing = id.Name.Namespace;
        string anonymous = addressing.NamespaceName + (addressing.NamespaceName.Contains("2004/08") ? "/role/anonymous" : "/anonymous");
        string baseUrl = advertisedUrl ?? new UriBuilder("http", localAddress, port).Uri.AbsoluteUri.TrimEnd('/');
        return new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap),
            new XAttribute(XNamespace.Xmlns + "d", discovery), new XAttribute(XNamespace.Xmlns + "wsa", addressing),
            new XAttribute(XNamespace.Xmlns + "dn", "http://www.onvif.org/ver10/network/wsdl"),
            new XElement(soap + "Header", new XElement(addressing + "MessageID", "urn:uuid:" + Guid.NewGuid()),
                new XElement(addressing + "RelatesTo", id.Value), new XElement(addressing + "To", anonymous),
                new XElement(addressing + "Action", discovery.NamespaceName + "/ProbeMatches"),
                new XElement(discovery + "AppSequence", new XAttribute("InstanceId", instanceId), new XAttribute("MessageNumber", Interlocked.Increment(ref messageNumber)))),
            new XElement(soap + "Body", new XElement(discovery + "ProbeMatches", new XElement(discovery + "ProbeMatch",
                new XElement(addressing + "EndpointReference", new XElement(addressing + "Address", endpointId)),
                new XElement(discovery + "Types", "dn:NetworkVideoTransmitter"),
                new XElement(discovery + "Scopes", "onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/Profile/Streaming"),
                new XElement(discovery + "XAddrs", baseUrl + "/onvif/device_service"), new XElement(discovery + "MetadataVersion", 1)))))).ToString();
    }

    public void Dispose()
    {
        running = false;
        udp?.Dispose();
        if (acceptThread?.IsAlive == true) acceptThread.Join(TimeSpan.FromMilliseconds(500));
    }
}
