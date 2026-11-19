using System;
using System.Text.RegularExpressions;

namespace Agent.Logging;

/// <summary>Removes credentials from any text before it reaches a log sink.</summary>
public static class SecretRedactor
{
    private const string Mask = "***REDACTED***";

    private static readonly Regex[] Patterns =
    {
        // Authorization: Bearer xxx / Basic xxx / Device xxx
        new Regex(@"(?i)(authorization\s*[:=]\s*)(bearer|basic|device|hmac)?\s*[A-Za-z0-9\-\._~\+/=:]{8,}", RegexOptions.Compiled),
        // key=value style secrets in query strings / JSON / config
        new Regex(@"(?i)(""?(?:password|passwd|pwd|secret|token|registrationtoken|apikey|api_key|api-key|credential|signature|privatekey|psk|passphrase)""?\s*[:=]\s*)""?[^""&\s,;}]{1,}""?", RegexOptions.Compiled),
        // Bearer tokens appearing standalone
        new Regex(@"(?i)\bbearer\s+[A-Za-z0-9\-\._~\+/=]{12,}", RegexOptions.Compiled),
        // Long base64-ish blobs (JWTs, keys) of 40+ characters
        new Regex(@"\b[A-Za-z0-9\-_]{20,}\.[A-Za-z0-9\-_]{20,}\.[A-Za-z0-9\-_]{10,}\b", RegexOptions.Compiled)
    };

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        result = Patterns[0].Replace(result, m => m.Groups[1].Value + Mask);
        result = Patterns[1].Replace(result, m => m.Groups[1].Value + Mask);
        result = Patterns[2].Replace(result, "Bearer " + Mask);
        result = Patterns[3].Replace(result, Mask);
        return result;
    }
}
