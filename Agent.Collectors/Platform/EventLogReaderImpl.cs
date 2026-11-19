using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>
/// Event Log reader on System.Diagnostics.Eventing.Reader (Vista+). The XPath query carries the record-id and time filters so the
/// service never loads events it has already processed. Reading the Security log needs LocalSystem / administrator rights.
/// </summary>
public sealed class EventLogReaderImpl : IEventLogReader
{
    public IReadOnlyList<RawEventRecord> Read(EventQuerySpec spec, out string error)
    {
        error = null;
        var result = new List<RawEventRecord>();
        try
        {
            var query = new EventLogQuery(spec.LogName, PathType.LogName, BuildXPath(spec)) { ReverseDirection = false, TolerateQueryErrors = true };
            using (var reader = new EventLogReader(query))
            {
                EventRecord rec;
                while (result.Count < spec.MaxEvents && (rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        var raw = Convert(rec);
                        if (raw != null) result.Add(raw);
                    }
                }
            }
        }
        catch (EventLogNotFoundException) { error = "log not found"; }
        catch (UnauthorizedAccessException) { error = "access denied (administrator / LocalSystem rights are required)"; }
        catch (EventLogException ex) { error = ex.Message; }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        return result;
    }

    public long GetLastRecordId(string logName)
    {
        try
        {
            using (var session = new EventLogSession())
            {
                var info = session.GetLogInformation(logName, PathType.LogName);
                return info.RecordCount.HasValue && info.RecordCount.Value > 0 ? (info.OldestRecordNumber ?? 0) + info.RecordCount.Value - 1 : 0;
            }
        }
        catch (Exception) { return 0; }
    }

    public static string BuildXPath(EventQuerySpec spec)
    {
        var terms = new List<string>();
        if (spec.MinRecordIdExclusive > 0) terms.Add("System[EventRecordID>" + spec.MinRecordIdExclusive.ToString(CultureInfo.InvariantCulture) + "]");
        if (spec.SinceUtc.HasValue)
            terms.Add("System[TimeCreated[@SystemTime>='" + spec.SinceUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "']]");
        if (!string.IsNullOrWhiteSpace(spec.Predicate)) terms.Add("(" + spec.Predicate + ")");
        return terms.Count == 0 ? "*" : "*[" + string.Join(" and ", terms) + "]";
    }

    private static RawEventRecord Convert(EventRecord rec)
    {
        if (rec.RecordId == null) return null;
        var raw = new RawEventRecord
        {
            RecordId = rec.RecordId.Value,
            EventId = rec.Id,
            Provider = rec.ProviderName,
            Level = rec.Level ?? 4,
            TimestampUtc = (rec.TimeCreated ?? DateTime.UtcNow).ToUniversalTime(),
            Computer = rec.MachineName,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        try { raw.Message = rec.FormatDescription(); } catch (Exception) { }   // missing provider message DLL is common: fall back to the data below
        try { ParseData(rec.ToXml(), raw.Data); } catch (Exception) { }
        return raw;
    }

    /// <summary>Named EventData values keep their names; unnamed ones become Data1, Data2, ...</summary>
    internal static void ParseData(string xml, Dictionary<string, string> into)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(xml);
        var nodes = doc.GetElementsByTagName("Data");
        int i = 0;
        foreach (XmlNode n in nodes)
        {
            i++;
            var name = n.Attributes != null && n.Attributes["Name"] != null ? n.Attributes["Name"].Value : "Data" + i.ToString(CultureInfo.InvariantCulture);
            if (!into.ContainsKey(name)) into[name] = n.InnerText;
        }
    }
}
