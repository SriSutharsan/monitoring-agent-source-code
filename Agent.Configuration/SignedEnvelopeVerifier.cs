using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Agent.Models;

namespace Agent.Configuration;

/// <summary>Imports an RSA public key from a standard PEM "PUBLIC KEY" (SPKI) or "RSA PUBLIC KEY" (PKCS#1) block. Works on net48 and net8.</summary>
public static class RsaPublicKeyParser
{
    public static RSAParameters Parse(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) throw new ArgumentException("public key is empty");
        var lines = pem.Replace("\r", "").Split('\n')
            .Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim());
        byte[] der = Convert.FromBase64String(string.Concat(lines));
        int pos = 0;
        ExpectTag(der, ref pos, 0x30);
        ReadLength(der, ref pos);
        // SPKI: SEQUENCE { SEQUENCE { OID, params }, BIT STRING { RSAPublicKey } }   PKCS#1: SEQUENCE { INTEGER n, INTEGER e }
        if (der[pos] == 0x30)
        {
            ExpectTag(der, ref pos, 0x30);
            int algLen = ReadLength(der, ref pos);
            pos += algLen;
            ExpectTag(der, ref pos, 0x03);
            ReadLength(der, ref pos);
            pos++;                              // unused-bits byte
            ExpectTag(der, ref pos, 0x30);
            ReadLength(der, ref pos);
        }
        var n = ReadInteger(der, ref pos);
        var e = ReadInteger(der, ref pos);
        return new RSAParameters { Modulus = n, Exponent = e };
    }

    private static void ExpectTag(byte[] d, ref int pos, byte tag)
    {
        if (pos >= d.Length || d[pos] != tag) throw new CryptographicException("Unsupported public key encoding");
        pos++;
    }

    private static int ReadLength(byte[] d, ref int pos)
    {
        int b = d[pos++];
        if (b < 0x80) return b;
        int count = b & 0x7F;
        int len = 0;
        for (int i = 0; i < count; i++) len = (len << 8) | d[pos++];
        return len;
    }

    private static byte[] ReadInteger(byte[] d, ref int pos)
    {
        ExpectTag(d, ref pos, 0x02);
        int len = ReadLength(d, ref pos);
        int start = pos;
        pos = start + len;                                      // always advance past the whole INTEGER
        while (len > 1 && d[start] == 0) { start++; len--; }    // strip sign padding
        var r = new byte[len];
        Array.Copy(d, start, r, 0, len);
        return r;
    }
}

public enum VerifyResult
{
    Valid,
    NoPublicKey,
    BadEncoding,
    BadSignature,
    Expired,
    NotYetValid,
    WrongDevice
}

/// <summary>Verifies RSA-SHA256 (PKCS#1 v1.5) signatures over base64 payloads (signed config documents and update manifests).</summary>
public sealed class SignedEnvelopeVerifier
{
    private readonly RSAParameters? _key;
    private readonly bool _required;
    private readonly TimeSpan _skew;

    public SignedEnvelopeVerifier(string publicKeyPem, bool requireSigned, TimeSpan maxClockSkew)
    {
        _required = requireSigned;
        _skew = maxClockSkew;
        if (!string.IsNullOrWhiteSpace(publicKeyPem))
        {
            try { _key = RsaPublicKeyParser.Parse(publicKeyPem); }
            catch (Exception) { _key = null; }
        }
    }

    public bool HasKey { get { return _key.HasValue; } }

    public VerifyResult Verify(SignedEnvelope envelope, out byte[] payloadBytes)
    {
        payloadBytes = null;
        if (envelope == null || string.IsNullOrEmpty(envelope.Payload)) return VerifyResult.BadEncoding;
        try { payloadBytes = Convert.FromBase64String(envelope.Payload); }
        catch (FormatException) { return VerifyResult.BadEncoding; }

        if (!_key.HasValue)
            return _required ? VerifyResult.NoPublicKey : VerifyResult.Valid;     // unsigned only allowed in explicit dev mode

        if (string.IsNullOrEmpty(envelope.Signature)) return VerifyResult.BadSignature;
        byte[] signature;
        try { signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { return VerifyResult.BadEncoding; }

        try
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(_key.Value);
                return rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    ? VerifyResult.Valid : VerifyResult.BadSignature;
            }
        }
        catch (CryptographicException) { return VerifyResult.BadSignature; }
    }

    public VerifyResult VerifyConfig(SignedEnvelope envelope, string deviceId, DateTime nowUtc, out AgentConfigDocument doc)
    {
        doc = null;
        byte[] bytes;
        var r = Verify(envelope, out bytes);
        if (r != VerifyResult.Valid) return r;
        if (!Json.TryDeserialize(Encoding.UTF8.GetString(bytes), out doc)) return VerifyResult.BadEncoding;
        if (!string.IsNullOrEmpty(doc.DeviceId) && !string.Equals(doc.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return VerifyResult.WrongDevice;
        if (doc.ExpiresAtUtc.HasValue && doc.ExpiresAtUtc.Value < nowUtc - _skew) return VerifyResult.Expired;
        if (doc.IssuedAtUtc.HasValue && doc.IssuedAtUtc.Value > nowUtc + _skew) return VerifyResult.NotYetValid;
        return VerifyResult.Valid;
    }

    public VerifyResult VerifyManifest(SignedEnvelope envelope, DateTime nowUtc, out UpdateManifest manifest)
    {
        manifest = null;
        byte[] bytes;
        var r = Verify(envelope, out bytes);
        if (r != VerifyResult.Valid) return r;
        if (!Json.TryDeserialize(Encoding.UTF8.GetString(bytes), out manifest)) return VerifyResult.BadEncoding;
        if (manifest.ExpiresAtUtc.HasValue && manifest.ExpiresAtUtc.Value < nowUtc - _skew) return VerifyResult.Expired;
        return VerifyResult.Valid;
    }
}
