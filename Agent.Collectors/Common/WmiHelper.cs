using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Agent.Core;

namespace Agent.Collectors;

/// <summary>Typed, null-safe reads over WMI objects, plus parsers for WMI's non-standard value formats.</summary>
public static class Wmi
{
    public static string Str(this IWmiObject o, string p)
    {
        try { return Text.ToStringOrNull(o[p]); } catch (Exception) { return null; }
    }

    public static long? Long(this IWmiObject o, string p)
    {
        try { return Text.ToLong(o[p]); } catch (Exception) { return null; }
    }

    public static int? Int(this IWmiObject o, string p)
    {
        try { return Text.ToInt(o[p]); } catch (Exception) { return null; }
    }

    public static double? Dbl(this IWmiObject o, string p)
    {
        try { return Text.ToDouble(o[p]); } catch (Exception) { return null; }
    }

    public static bool? Bool(this IWmiObject o, string p)
    {
        try { return Text.ToBool(o[p]); } catch (Exception) { return null; }
    }

    public static DateTime? Dmtf(this IWmiObject o, string p)
    {
        try
        {
            var v = o[p];
            if (v is DateTime) return Text.ToUtc(v);
            return ParseDmtf(Text.ToStringOrNull(v));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Parses "yyyyMMddHHmmss.ffffff+UUU" (UUU = minutes offset from UTC). Returns UTC.</summary>
    public static DateTime? ParseDmtf(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 14) return null;
        try
        {
            int y = int.Parse(s.Substring(0, 4), CultureInfo.InvariantCulture);
            if (y < 1980) return null;
            var local = new DateTime(y, int.Parse(s.Substring(4, 2), CultureInfo.InvariantCulture), int.Parse(s.Substring(6, 2), CultureInfo.InvariantCulture),
                int.Parse(s.Substring(8, 2), CultureInfo.InvariantCulture), int.Parse(s.Substring(10, 2), CultureInfo.InvariantCulture),
                int.Parse(s.Substring(12, 2), CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
            int offsetMinutes = 0;
            int signPos = s.LastIndexOfAny(new[] { '+', '-' });
            if (signPos > 14 && int.TryParse(s.Substring(signPos), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offsetMinutes)) { }
            return DateTime.SpecifyKind(local.AddMinutes(-offsetMinutes), DateTimeKind.Utc);
        }
        catch (Exception) { return null; }
    }

    /// <summary>yyyyMMdd (registry InstallDate, QFE) -> date at 00:00 UTC.</summary>
    public static DateTime? ParseYmd(string s)
    {
        DateTime d;
        if (!string.IsNullOrEmpty(s) && s.Length == 8 &&
            DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
            return d;
        return null;
    }

    /// <summary>WMI arrays of UTF-16 code units (e.g. monitor names) -> string.</summary>
    public static string UShortArrayToString(object value)
    {
        if (value == null) return null;
        var sb = new StringBuilder();
        var arr = value as System.Collections.IEnumerable;
        if (arr == null) return null;
        foreach (var item in arr)
        {
            var n = Text.ToLong(item);
            if (!n.HasValue || n.Value == 0) continue;
            sb.Append((char)n.Value);
        }
        return Text.NullIfEmpty(sb.ToString());
    }

    public static string Escape(string s)
    {
        return (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
    }

    public static string Gb(long bytes) { return (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB"; }
}
