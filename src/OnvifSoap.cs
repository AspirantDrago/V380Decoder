using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace V380Decoder.src;

internal sealed class OnvifSoap
{
    internal const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    internal const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    internal const string Schema = "http://www.onvif.org/ver10/schema";
    internal const string Media1 = "http://www.onvif.org/ver10/media/wsdl";
    internal const string Media2 = "http://www.onvif.org/ver20/media/wsdl";
    internal const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    internal const string Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    public XDocument Document { get; }
    public XElement Operation { get; }
    public string Action => Operation.Name.LocalName;
    public string Namespace => Operation.Name.NamespaceName;
    public XNamespace EnvelopeNamespace { get; }
    public XElement Header => Document.Root.Element(EnvelopeNamespace + "Header");
    public XElement UsernameToken => Header?.Element((XNamespace)Wsse + "Security")?.Element((XNamespace)Wsse + "UsernameToken");

    public OnvifSoap(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        Document = XDocument.Load(reader);
        EnvelopeNamespace = Document.Root?.Name.Namespace ?? XNamespace.None;
        if (Document.Root?.Name.LocalName != "Envelope" || (EnvelopeNamespace != Soap11 && EnvelopeNamespace != Soap12))
            throw new XmlException("Expected SOAP Envelope");
        var operations = Document.Root.Element(EnvelopeNamespace + "Body")?.Elements().ToArray();
        if (operations?.Length != 1) throw new XmlException("Expected one SOAP operation");
        Operation = operations[0];
    }

    public string Parameter(string name) => Operation.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    public string ValidateUsernameToken(string username, string password, DateTimeOffset? utcNow = null)
    {
        XNamespace ws = Wsse, util = Wsu;
        var token = UsernameToken;
        if (token == null) return "Missing UsernameToken credentials";
        if ((string)token.Element(ws + "Username") != username) return "Invalid username";
        var supplied = token.Element(ws + "Password");
        if (supplied == null) return "Missing password";
        string type = (string)supplied.Attribute("Type") ?? "";
        if (type.EndsWith("#PasswordDigest", StringComparison.Ordinal))
        {
            string created = (string)token.Element(util + "Created");
            string nonce = (string)token.Element(ws + "Nonce");
            if (created == null || nonce == null) return "Missing Nonce or Created";
            if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ||
                ((utcNow ?? DateTimeOffset.UtcNow) - time).Duration() > TimeSpan.FromMinutes(5)) return "UsernameToken time outside allowed window";
            try
            {
                byte[] bytes = [..Convert.FromBase64String(nonce), ..Encoding.UTF8.GetBytes(created), ..Encoding.UTF8.GetBytes(password)];
                byte[] expected = SHA1.HashData(bytes);
                return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(supplied.Value.Trim())) ? null : "Invalid password digest";
            }
            catch (FormatException) { return "Invalid UsernameToken encoding"; }
        }
        if (type != "" && !type.EndsWith("#PasswordText", StringComparison.Ordinal)) return "Unsupported password type";
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied.Value), Encoding.UTF8.GetBytes(password)) ? null : "Invalid password";
    }

    internal static string Envelope(XElement operation) => new XDocument(
        new XElement((XNamespace)Soap12 + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap12),
            new XAttribute(XNamespace.Xmlns + "tt", Schema), new XElement((XNamespace)Soap12 + "Body", operation))).ToString();

    internal static string Fault(string code, string reason) => Envelope(new XElement((XNamespace)Soap12 + "Fault",
        new XElement((XNamespace)Soap12 + "Code", new XElement((XNamespace)Soap12 + "Value", "s:Sender"),
            new XElement((XNamespace)Soap12 + "Subcode", new XAttribute(XNamespace.Xmlns + "ter", "http://www.onvif.org/ver10/error"),
                new XElement((XNamespace)Soap12 + "Value", "ter:" + code))),
        new XElement((XNamespace)Soap12 + "Reason", new XElement((XNamespace)Soap12 + "Text", new XAttribute(XNamespace.Xml + "lang", "en"), reason))));

    public string FormatResponse(string response)
    {
        var doc = XDocument.Parse(response);
        XNamespace original = Soap12;
        if (EnvelopeNamespace == Soap11)
        {
            var fault = doc.Descendants(original + "Fault").FirstOrDefault();
            if (fault != null)
                fault.ReplaceWith(new XElement((XNamespace)Soap11 + "Fault", new XElement("faultcode", "s:Client"),
                    new XElement("faultstring", fault.Descendants(original + "Text").FirstOrDefault()?.Value ?? "ONVIF fault")));
            foreach (var element in doc.Descendants().Where(e => e.Name.Namespace == original).ToArray())
                element.Name = EnvelopeNamespace + element.Name.LocalName;
            doc.Root.SetAttributeValue(XNamespace.Xmlns + "s", Soap11);
        }
        var addressing = Header?.Elements().FirstOrDefault(e => e.Name.LocalName == "MessageID");
        if (addressing != null)
        {
            XNamespace ns = addressing.Name.Namespace;
            string destination = (string)Header.Element(ns + "ReplyTo")?.Element(ns + "Address") ??
                ns.NamespaceName + (ns.NamespaceName.Contains("2004/08") ? "/role/anonymous" : "/anonymous");
            doc.Root.AddFirst(new XElement(EnvelopeNamespace + "Header", new XAttribute(XNamespace.Xmlns + "wsa", ns.NamespaceName),
                new XElement(ns + "Action", Namespace + "/" + Action + "Response"), new XElement(ns + "MessageID", "urn:uuid:" + Guid.NewGuid()),
                new XElement(ns + "RelatesTo", addressing.Value), new XElement(ns + "To", destination)));
        }
        return doc.ToString();
    }
}
