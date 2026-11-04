using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Xml;
using Microsoft.AspNetCore.Http.Features;
using System.Xml.Linq;

namespace V380Decoder.src
{
    public class WebServer
    {
        private readonly V380Client client;
        private readonly int httpPort;
        private readonly int rtspPort;
        private readonly bool enableApi;
        private readonly bool enableOnvif;
        private readonly bool enableMjpeg;
        private readonly bool secure;
        private readonly string username;
        private readonly string password;
        private WebApplication app;
        private readonly DigestAuthentication onvifDigest = new();
        private readonly string advertisedHttpUrl, advertisedRtspUrl;
        internal int BoundPort => new Uri(app.Urls.First()).Port;
        public WebServer(
            int httpPort,
            int rtspPort,
            V380Client client,
            bool enableApi,
            bool enableOnvif,
            bool enableMjpeg,
            bool secure,
            string username,
            string password,
            string advertisedHttpUrl = null,
            string advertisedRtspUrl = null)
        {
            this.httpPort = httpPort;
            this.rtspPort = rtspPort;
            this.client = client;
            this.enableApi = enableApi;
            this.enableOnvif = enableOnvif;
            this.enableMjpeg = enableMjpeg;
            this.secure = secure;
            this.username = username;
            this.password = password;
            this.advertisedHttpUrl = advertisedHttpUrl;
            this.advertisedRtspUrl = advertisedRtspUrl;
        }

        public void Start()
        {
            string ipAddress = NetworkHelper.GetLocalIPAddress();
            string basicAuth = string.Empty;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://*:{httpPort}");

            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
            });

            app = builder.Build();

            RouteGroupBuilder api = app.MapGroup("/");
            if (secure)
            {
                basicAuth = $"{username}:{password}@";
                api.AddEndpointFilter(async (context, next) =>
                {
                    var http = context.HttpContext;

                    var auth = http.Request.Headers.Authorization.ToString();
                    string target = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
                    if (string.IsNullOrEmpty(target)) target = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
                    if (!DigestAuthentication.VerifyBasic(auth, username, password) &&
                        !onvifDigest.Verify(auth, http.Request.Method, target, username, password))
                    {
                        http.Response.StatusCode = 401;
                        http.Response.Headers.Append("WWW-Authenticate", onvifDigest.Challenge());
                        http.Response.Headers.Append("WWW-Authenticate", @"Basic realm=""V380 Authentication""");
                        return Results.Empty;
                    }

                    return await next(context);
                });
            }

            Console.Error.WriteLine($"[SNAPSHOT] http://{basicAuth}{ipAddress}:{httpPort}/snapshot");
            api.MapGet("/snapshot", async (HttpContext ctx) =>
            {
                var jpeg = await client.snapshotManager.GetSnapshotAsync(timeoutMs: 5000);

                if (jpeg == null)
                    return Results.Problem("No snapshot available yet", statusCode: 503);

                ctx.Response.Headers.CacheControl = "no-cache";
                return Results.File(jpeg, "image/jpeg");
            });

            if (enableMjpeg)
            {
                Console.Error.WriteLine($"[MJPEG] http://{basicAuth}{ipAddress}:{httpPort}/mjpeg");
                api.MapGet("/mjpeg", async (HttpContext ctx, CancellationToken ct) =>
                {
                    const string boundary = "mjpegframe";
                    ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
                    ctx.Response.Headers.CacheControl = "no-cache";

                    var ch = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                        new System.Threading.Channels.BoundedChannelOptions(2)
                        {
                            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
                        });

                    using var sub = client.snapshotManager.Subscribe(jpeg => ch.Writer.TryWrite(jpeg));

                    try
                    {
                        await foreach (var jpeg in ch.Reader.ReadAllAsync(ct))
                        {
                            var header = System.Text.Encoding.ASCII.GetBytes(
                                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");

                            await ctx.Response.Body.WriteAsync(header, ct);
                            await ctx.Response.Body.WriteAsync(jpeg, ct);
                            await ctx.Response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
                            await ctx.Response.Body.FlushAsync(ct);
                        }
                    }
                    catch (OperationCanceledException) { }
                });
            }

            if (enableApi)
            {
                Console.Error.WriteLine($"[WEB] http://{basicAuth}{ipAddress}:{httpPort}");
                Console.Error.WriteLine($"[API] http://{basicAuth}{ipAddress}:{httpPort}/api/");

                api.MapGet("/", () => Results.Content(WebPage.GetHtml(enableMjpeg), "text/html"));

                api.MapPost("/api/ptz/right", () => { client.PtzRight(); LogUtils.debug("[API] PTZ Right"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/left", () => { client.PtzLeft(); LogUtils.debug("[API] PTZ Left"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/up", () => { client.PtzUp(); LogUtils.debug("[API] PTZ Up"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/down", () => { client.PtzDown(); LogUtils.debug("[API] PTZ Down"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/stop", () => { client.PtzStop(); LogUtils.debug("[API] PTZ Stop"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });

                api.MapPost("/api/light/on", () => { client.LightOn(); LogUtils.debug("[API] Light On"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/light/off", () => { client.LightOff(); LogUtils.debug("[API] Light Off"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/light/auto", () => { client.LightAuto(); LogUtils.debug("[API] Light Auto"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });

                api.MapPost("/api/image/color", () => { client.ImageColor(); LogUtils.debug("[API] Image Color"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/image/bw", () => { client.ImageBW(); LogUtils.debug("[API] Image B&W"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/image/auto", () => { client.ImageAuto(); LogUtils.debug("[API] Image Auto"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/image/flip", () => { client.ImageFlip(); LogUtils.debug("[API] Image Flip"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });

                api.MapGet("/api/status", () => Results.Ok(new StatusResponse
                {
                    status = "running",
                    timestamp = DateTime.Now
                }));
            }

            if (enableOnvif)
            {
                if (secure)
                    Console.Error.WriteLine($"[ONVIF] http://{ipAddress}:{httpPort}/onvif/device_service (WS-Security enabled: {username})");
                else
                    Console.Error.WriteLine($"[ONVIF] http://{ipAddress}:{httpPort}/onvif/device_service");

                var onvifGroup = app.MapGroup("/");

                onvifGroup.MapPost("/onvif/device_service", async (HttpContext ctx) =>
                await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/media_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/media2_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/ptz_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/imaging_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));
            }

            app.StartAsync().GetAwaiter().GetResult();
        }

        internal async Task HandleOnvif(HttpContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            OnvifSoap request;
            try { request = new OnvifSoap(body); }
            catch (XmlException)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
                await ctx.Response.WriteAsync(OnvifSoap.Fault("InvalidArgVal", "Invalid SOAP request"));
                Console.Error.WriteLine("[ONVIF] Invalid SOAP request status=400");
                return;
            }
            if (advertisedHttpUrl != null) ctx.Items[OnvifEndpoints.HttpKey] = advertisedHttpUrl;
            if (advertisedRtspUrl != null) ctx.Items[OnvifEndpoints.RtspKey] = advertisedRtspUrl;
            bool transportAuthenticated = false;
            string authorization = ctx.Request.Headers.Authorization.ToString();
            bool anonymous = request.Action is "GetSystemDateAndTime" or "GetCapabilities" or "GetScopes";
            if (secure && (authorization != "" || (!anonymous && request.UsernameToken == null)))
            {
                string target = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;
                if (string.IsNullOrEmpty(target)) target = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
                transportAuthenticated = onvifDigest.Verify(authorization, ctx.Request.Method, target, username, password);
                if (!transportAuthenticated)
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Headers.WWWAuthenticate = onvifDigest.Challenge();
                    Console.Error.WriteLine($"[ONVIF] {request.Action} status=401 HTTP Digest challenge");
                    return;
                }
            }
            if (client != null && !client.StreamInfo.Known &&
                request.Action is "GetProfiles" or "GetProfile" or "GetVideoEncoderConfigurations" or "GetVideoEncoderConfiguration")
            {
                try { await client.VideoReady.WaitAsync(TimeSpan.FromSeconds(3), ctx.RequestAborted); }
                catch (TimeoutException)
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
                    await ctx.Response.WriteAsync(request.FormatResponse(OnvifSoap.Fault("ActionNotSupported", "Camera video format is not available yet; retry after the camera connects")));
                    Console.Error.WriteLine($"[ONVIF] {request.Action} status=503 camera video format not available");
                    return;
                }
            }
            string response = OnvifHandler.Handle(request.Action, body, ctx, client, httpPort, rtspPort,
                secure, username, password, transportAuthenticated);
            response = request.FormatResponse(response);
            ctx.Response.ContentType = request.EnvelopeNamespace == OnvifSoap.Soap11 ? "text/xml; charset=utf-8" : "application/soap+xml; charset=utf-8";
            if (ctx.Response.StatusCode >= 400)
                Console.Error.WriteLine($"[ONVIF] {request.Action} status={ctx.Response.StatusCode} " +
                    XDocument.Parse(response).Descendants().FirstOrDefault(e => e.Name.LocalName is "Text" or "faultstring")?.Value);
            else if (request.Action is "GetServices" or "GetProfiles" or "GetStreamUri")
                Console.Error.WriteLine($"[ONVIF] {request.Namespace}/{request.Action} status={ctx.Response.StatusCode}");
            else LogUtils.debug($"[ONVIF] {request.Namespace}/{request.Action} status={ctx.Response.StatusCode}");
            await ctx.Response.WriteAsync(response);
        }

        public void Stop()
        {
            app?.StopAsync().Wait();
            app?.DisposeAsync().AsTask().Wait();
        }
    }
}