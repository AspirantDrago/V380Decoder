using System.Diagnostics;
using System.Xml.Linq;

namespace V380Decoder.src;

// Per-bridge ONVIF clock. NVR time synchronization must never set the host clock.
internal sealed class OnvifClock
{
    internal const string ContextKey = "V380.OnvifClock";
    private readonly object gate = new();
    private DateTimeOffset? manualUtc;
    private long changed;
    private string zone = "UTC0";
    private bool daylight;
    internal DateTimeOffset UtcNow
    {
        get { lock (gate) return manualUtc?.Add(Stopwatch.GetElapsedTime(changed)) ?? DateTimeOffset.UtcNow; }
    }

    internal string Set(OnvifSoap request)
    {
        string mode = request.Parameter("DateTimeType"), dst = request.Parameter("DaylightSavings");
        if (mode is not ("Manual" or "NTP") || dst is not ("true" or "false" or "1" or "0"))
            return OnvifSoap.Fault("InvalidArgVal", "Invalid DateTimeType or DaylightSavings");
        XNamespace t = OnvifSoap.Schema;
        DateTimeOffset? utc = null;
        if (mode == "Manual")
        {
            var dt = request.Operation.Elements().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
            int Value(string parent, string name) => (int?)dt?.Element(t + parent)?.Element(t + name) ?? throw new FormatException();
            try { utc = new DateTimeOffset(Value("Date", "Year"), Value("Date", "Month"), Value("Date", "Day"),
                Value("Time", "Hour"), Value("Time", "Minute"), Value("Time", "Second"), TimeSpan.Zero); }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
            { return OnvifSoap.Fault("InvalidDateTime", "Invalid UTCDateTime"); }
        }
        lock (gate)
        {
            manualUtc = utc;
            changed = Stopwatch.GetTimestamp();
            daylight = dst is "true" or "1";
            zone = request.Operation.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeZone")?.Element(t + "TZ")?.Value ?? zone;
        }
        return null;
    }

    internal string Get()
    {
        lock (gate)
        {
            var now = UtcNow;
            XNamespace d = "http://www.onvif.org/ver10/device/wsdl", t = OnvifSoap.Schema;
            return OnvifSoap.Envelope(new XElement(d + "GetSystemDateAndTimeResponse", new XElement(d + "SystemDateAndTime",
                new XElement(t + "DateTimeType", manualUtc == null ? "NTP" : "Manual"), new XElement(t + "DaylightSavings", daylight),
                new XElement(t + "TimeZone", new XElement(t + "TZ", zone)), new XElement(t + "UTCDateTime",
                    new XElement(t + "Time", new XElement(t + "Hour", now.Hour), new XElement(t + "Minute", now.Minute), new XElement(t + "Second", now.Second)),
                    new XElement(t + "Date", new XElement(t + "Year", now.Year), new XElement(t + "Month", now.Month), new XElement(t + "Day", now.Day))))));
        }
    }
}
