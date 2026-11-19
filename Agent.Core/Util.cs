using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Core;

public static class Hashing
{
    public static string Sha256Hex(string text)
    {
        using (var sha = SHA256.Create())
        {
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
        }
    }

    public static string Sha256Hex(byte[] data)
    {
        using (var sha = SHA256.Create())
        {
            return ToHex(sha.ComputeHash(data));
        }
    }

    public static string Short(string text, int length = 16)
    {
        var h = Sha256Hex(text);
        return h.Substring(0, Math.Min(length, h.Length));
    }

    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

public static class Backoff
{
    private static readonly Random Rng = new Random();
    private static readonly object Gate = new object();

    public static double NextDouble()
    {
        lock (Gate) return Rng.NextDouble();
    }

    public static int Next(int maxExclusive)
    {
        lock (Gate) return Rng.Next(maxExclusive);
    }

    /// <summary>Exponential backoff with +/-20% jitter, capped.</summary>
    public static TimeSpan Exponential(int attempt, TimeSpan baseDelay, TimeSpan max)
    {
        if (attempt < 0) attempt = 0;
        double factor = Math.Pow(2, Math.Min(attempt, 20));
        double ms = Math.Min(baseDelay.TotalMilliseconds * factor, max.TotalMilliseconds);
        double jitter = 1.0 + (NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromMilliseconds(Math.Max(0, ms * jitter));
    }
}

public static class Wildcard
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new ConcurrentDictionary<string, Regex>();

    public static bool IsMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern) || text == null) return false;
        var rx = Cache.GetOrAdd(pattern, p =>
            new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline));
        return rx.IsMatch(text);
    }

    public static string FirstMatch(IEnumerable<string> patterns, string text)
    {
        if (patterns == null) return null;
        foreach (var p in patterns) if (IsMatch(p, text)) return p;
        return null;
    }
}

public static class VersionCompare
{
    private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled);

    /// <summary>Compares dotted version strings numerically. Returns null when either side has no digits.</summary>
    public static int? Compare(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        if (pa == null || pb == null) return null;
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            long x = i < pa.Length ? pa[i] : 0;
            long y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    public static bool IsOlderThan(string current, string minimum)
    {
        var c = Compare(current, minimum);
        return c.HasValue && c.Value < 0;
    }

    private static long[] Parse(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var ms = Digits.Matches(v);
        if (ms.Count == 0) return null;
        int n = Math.Min(ms.Count, 6);
        var r = new long[n];
        for (int i = 0; i < n; i++)
        {
            long x;
            r[i] = long.TryParse(ms[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out x) ? x : 0;
        }
        return r;
    }
}

public static class Num
{
    public static double Clamp(double v, double min, double max)
    {
        return v < min ? min : (v > max ? max : v);
    }

    public static int Clamp(int v, int min, int max)
    {
        return v < min ? min : (v > max ? max : v);
    }

    public static double Round(double v, int digits = 1)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        return Math.Round(v, digits, MidpointRounding.AwayFromZero);
    }

    public static double? Round(double? v, int digits = 1)
    {
        return v.HasValue ? Round(v.Value, digits) : (double?)null;
    }
}

public static class Text
{
    public static string NullIfEmpty(string s)
    {
        if (s == null) return null;
        var t = s.Trim().Trim('\0');
        return t.Length == 0 ? null : t;
    }

    public static string Truncate(string s, int max)
    {
        if (s == null) return null;
        return s.Length <= max ? s : s.Substring(0, max);
    }

    public static string ToStringOrNull(object o)
    {
        if (o == null) return null;
        var s = Convert.ToString(o, CultureInfo.InvariantCulture);
        return NullIfEmpty(s);
    }

    public static long? ToLong(object o)
    {
        if (o == null) return null;
        try
        {
            if (o is long) return (long)o;
            if (o is ulong) return unchecked((long)(ulong)o);
            if (o is bool) return (bool)o ? 1 : 0;
            long r;
            if (long.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            double d;
            if (double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (long)d;
        }
        catch (Exception) { }
        return null;
    }

    public static int? ToInt(object o)
    {
        var l = ToLong(o);
        if (!l.HasValue) return null;
        if (l.Value > int.MaxValue || l.Value < int.MinValue) return null;
        return (int)l.Value;
    }

    public static double? ToDouble(object o)
    {
        if (o == null) return null;
        double d;
        return double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
    }

    public static bool? ToBool(object o)
    {
        if (o == null) return null;
        if (o is bool) return (bool)o;
        var s = Convert.ToString(o, CultureInfo.InvariantCulture);
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
        return null;
    }

    public static DateTime? ToUtc(object o)
    {
        if (o == null) return null;
        if (o is DateTime)
        {
            var d = (DateTime)o;
            if (d == DateTime.MinValue || d.Year < 1980) return null;
            return d.Kind == DateTimeKind.Utc ? d : d.ToUniversalTime();
        }
        return null;
    }
}

public static class DictionaryExtensions
{
    public static TValue GetOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key)
    {
        TValue v;
        return dict != null && dict.TryGetValue(key, out v) ? v : default(TValue);
    }
}
