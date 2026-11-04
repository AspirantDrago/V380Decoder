using Microsoft.AspNetCore.Http;
using System.Xml;

namespace V380Decoder.src;

internal static class CameraName
{
    internal const string ContextKey = "V380.CameraName";
    internal static string Validate(string value)
    {
        if (value == null) return "V380";
        value = value.Trim();
        if (value.Length is 0 or > 64 || value.Any(char.IsControl))
            throw new ArgumentException("--camera-name must contain 1-64 characters without control characters");
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException) { throw new ArgumentException("--camera-name contains invalid characters"); }
        return value;
    }

    internal static string From(HttpContext ctx, string fallback) => ctx.Items[ContextKey] as string ?? fallback;
    internal static string NameScope(string name) => "onvif://www.onvif.org/name/" + Uri.EscapeDataString(name ?? "V380");
}
