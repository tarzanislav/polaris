using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;

namespace Polaris.Windows;

/// <summary>Windows measures each of its starts itself (Diagnostics-Performance, event 100, BootTime in ms). Reading
/// these numbers compares the layered start with the original one without any measuring of our own.</summary>
public static unsafe class BootEvents
{
    private const string Channel = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public sealed record BootTime(DateTimeOffset Created, int Milliseconds);

    /// <summary>The newest measurements, newest first.</summary>
    public static List<BootTime> Recent(int max)
    {
        var list = new List<BootTime>();
        var query = Wevtapi.EvtQuery(0, Channel, "*[System[(EventID=100)]]", Wevtapi.QueryChannelPath | Wevtapi.QueryReverseDirection);
        if (query == 0)
        {
            return list;
        }

        try
        {
            var events = stackalloc nint[16];
            while (list.Count < max && Wevtapi.EvtNext(query, 16, events, 1000, 0, out var count) && count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        if (list.Count < max && Parse(Render(events[i])) is { } b)
                        {
                            list.Add(b);
                        }
                    }
                    finally
                    {
                        Wevtapi.EvtClose(events[i]);
                    }
                }
            }
        }
        finally
        {
            Wevtapi.EvtClose(query);
        }

        return list;
    }

    private static string Render(nint e)
    {
        _ = Wevtapi.EvtRender(0, e, Wevtapi.RenderEventXml, 0, null, out var used, out _);
        var buffer = Marshal.AllocHGlobal(used);
        try
        {
            if (!Wevtapi.EvtRender(0, e, Wevtapi.RenderEventXml, used, (void*)buffer, out used, out _))
            {
                return "";
            }

            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static BootTime? Parse(string xml)
    {
        if (xml.Length == 0)
        {
            return null;
        }

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        DateTimeOffset? created = null;
        int? ms = null;
        foreach (XmlElement el in doc.GetElementsByTagName("*"))
        {
            if (el.LocalName == "TimeCreated" && DateTimeOffset.TryParse(el.GetAttribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            {
                created = t;
            }
            else if (el.LocalName == "Data" && el.GetAttribute("Name") == "BootTime"
                && int.TryParse(el.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                ms = v;
            }
        }

        return created is { } c && ms is > 0 and { } m ? new BootTime(c, m) : null;
    }
}
