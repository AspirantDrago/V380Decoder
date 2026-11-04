using Microsoft.AspNetCore.Http;

namespace V380Decoder.src;

internal static class OnvifEndpoints
{
    internal const string HttpKey = "V380.OnvifHttpUrl", RtspKey = "V380.OnvifRtspUrl";
    public static string HttpBase(HttpContext ctx) => (ctx.Items[HttpKey] as string ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
    public static string StreamUri(HttpContext ctx, int port) => ctx.Items[RtspKey] as string ??
        new UriBuilder("rtsp", new Uri(HttpBase(ctx)).Host, port, "/live").Uri.AbsoluteUri;
    public static string SnapshotUri(HttpContext ctx) => HttpBase(ctx) + "/snapshot";

    public static string Validate(string value, params string[] schemes)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !schemes.Contains(uri.Scheme) || uri.UserInfo != "" ||
            uri.Query != "" || uri.Fragment != "") throw new ArgumentException("Invalid advertised endpoint URL");
        return uri.AbsoluteUri.TrimEnd('/');
    }
}
