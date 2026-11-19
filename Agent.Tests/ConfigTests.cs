using System;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Agent.Configuration;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Tests;

public class ConfigTests
{
    private static string Pem(byte[] spki)
    {
        return "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(spki, Base64FormattingOptions.InsertLineBreaks) + "\n-----END PUBLIC KEY-----";
    }

    private static SignedEnvelope Sign(RSA key, object doc)
    {
        var payload = Encoding.UTF8.GetBytes(Json.Serialize(doc));
        return new SignedEnvelope
        {
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        };
    }

    [Fact(DisplayName = "PEM parser: SPKI and PKCS#1 public keys (2048 and 3072 bit)")]
    public void PEMParserSPKIAndPKCS1PublicKeys2048And3072Bit()
    {
        foreach (var bits in new[] { 2048, 3072 })
            using (var rsa = RSA.Create(bits))
            {
                var p = rsa.ExportParameters(false);
                var spki = RsaPublicKeyParser.Parse(Pem(rsa.ExportSubjectPublicKeyInfo()));
                var pkcs1 = RsaPublicKeyParser.Parse(
                    "-----BEGIN RSA PUBLIC KEY-----\n" + Convert.ToBase64String(rsa.ExportRSAPublicKey()) + "\n-----END RSA PUBLIC KEY-----");
                Assert.True(Convert.ToBase64String(spki.Modulus) == Convert.ToBase64String(p.Modulus), "SPKI modulus " + bits);
                Assert.True(Convert.ToBase64String(pkcs1.Modulus) == Convert.ToBase64String(p.Modulus), "PKCS1 modulus " + bits);
                Assert.True(Convert.ToBase64String(spki.Exponent) == Convert.ToBase64String(p.Exponent), "exponent " + bits);
            }
    }

    [Fact(DisplayName = "Signed config: valid signature accepted, tampering rejected")]
    public void SignedConfigValidSignatureAcceptedTamperingRejected()
    {
        using (var rsa = RSA.Create(2048))
        {
            var v = new SignedEnvelopeVerifier(Pem(rsa.ExportSubjectPublicKeyInfo()), true, TimeSpan.FromMinutes(10));
            var env = Sign(rsa, new AgentConfigDocument { Version = 3 });
            AgentConfigDocument doc;
            Assert.Equal(VerifyResult.Valid, v.VerifyConfig(env, "dev", DateTime.UtcNow, out doc));
            Assert.Equal(3L, doc.Version);

            var tampered = new SignedEnvelope
            {
                Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(new AgentConfigDocument { Version = 99 }))),
                Signature = env.Signature
            };
            Assert.Equal(VerifyResult.BadSignature, v.VerifyConfig(tampered, "dev", DateTime.UtcNow, out doc));
        }
    }

    [Fact(DisplayName = "Signed config: wrong key, unsigned, expired and wrong-device are rejected")]
    public void SignedConfigWrongKeyUnsignedExpiredAndWrongDeviceAreRejected()
    {
        using (var good = RSA.Create(2048))
        using (var evil = RSA.Create(2048))
        {
            var v = new SignedEnvelopeVerifier(Pem(good.ExportSubjectPublicKeyInfo()), true, TimeSpan.FromMinutes(10));
            AgentConfigDocument doc;
            Assert.Equal(VerifyResult.BadSignature, v.VerifyConfig(Sign(evil, new AgentConfigDocument { Version = 1 }), "d", DateTime.UtcNow, out doc), "wrong key");
            var unsigned = new SignedEnvelope { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) };
            Assert.Equal(VerifyResult.BadSignature, v.VerifyConfig(unsigned, "d", DateTime.UtcNow, out doc), "unsigned");
            Assert.Equal(VerifyResult.Expired, v.VerifyConfig(Sign(good, new AgentConfigDocument { Version = 1, ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) }), "d", DateTime.UtcNow, out doc), "expired");
            Assert.Equal(VerifyResult.WrongDevice, v.VerifyConfig(Sign(good, new AgentConfigDocument { Version = 1, DeviceId = "other" }), "d", DateTime.UtcNow, out doc), "wrong device");
        }
    }

    [Fact(DisplayName = "Signed config: without a configured key everything is rejected in production mode")]
    public void SignedConfigWithoutAConfiguredKeyEverythingIsRejectedInProductionMode()
    {
        using (var rsa = RSA.Create(2048))
        {
            var v = new SignedEnvelopeVerifier(null, true, TimeSpan.FromMinutes(10));
            AgentConfigDocument doc;
            Assert.Equal(VerifyResult.NoPublicKey, v.VerifyConfig(Sign(rsa, new AgentConfigDocument { Version = 1 }), "d", DateTime.UtcNow, out doc));
        }
    }

    [Fact(DisplayName = "Update manifest: signature and expiry verified")]
    public void UpdateManifestSignatureAndExpiryVerified()
    {
        using (var rsa = RSA.Create(2048))
        {
            var v = new SignedEnvelopeVerifier(Pem(rsa.ExportSubjectPublicKeyInfo()), true, TimeSpan.FromMinutes(10));
            UpdateManifest m;
            var ok = Sign(rsa, new UpdateManifest { Version = "1.2.0", Sha256 = "ab", PackageUrl = "https://x/y.zip" });
            Assert.Equal(VerifyResult.Valid, v.VerifyManifest(ok, DateTime.UtcNow, out m));
            Assert.Equal("1.2.0", m.Version);
            var old = Sign(rsa, new UpdateManifest { Version = "1.2.0", ExpiresAtUtc = DateTime.UtcNow.AddDays(-2) });
            Assert.Equal(VerifyResult.Expired, v.VerifyManifest(old, DateTime.UtcNow, out m));
        }
    }

    [Fact(DisplayName = "RemoteConfigProvider: applies newer version, ignores stale, persists and reloads cache")]
    public void RemoteConfigProviderAppliesNewerVersionIgnoresStalePersistsAndReloadsCache()
    {
        var cache = Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var rsa = RSA.Create(2048))
            {
                var opts = new AgentOptions();
                var verifier = new SignedEnvelopeVerifier(Pem(rsa.ExportSubjectPublicKeyInfo()), true, TimeSpan.FromMinutes(10));
                Func<RemoteConfigProvider> make = () => new RemoteConfigProvider(opts, verifier, cache, () => "dev", new SystemClock(), NullLogger<RemoteConfigProvider>.Instance);
                var p = make();
                int changed = 0;
                p.Changed += c => changed++;

                var v5 = Sign(rsa, new AgentConfigDocument { Version = 5, IntervalsSeconds = new Dictionary<string, int> { { Categories.PerfCpuMemory, 45 } } });
                Assert.Equal(ConfigApplyResult.Applied, p.Apply(v5));
                Assert.Equal(45, (int)p.Current.IntervalFor(Categories.PerfCpuMemory).TotalSeconds);
                Assert.Equal(1, changed);

                Assert.Equal(ConfigApplyResult.Stale, p.Apply(Sign(rsa, new AgentConfigDocument { Version = 4 })));
                Assert.Equal(ConfigApplyResult.Rejected, p.Apply(new SignedEnvelope { Payload = v5.Payload, Signature = "AAAA" }));
                Assert.Equal(1, changed);

                var p2 = make();
                p2.LoadCached();
                Assert.Equal(5L, p2.Current.Version, "cache reload");
                Assert.Equal(45, (int)p2.Current.IntervalFor(Categories.PerfCpuMemory).TotalSeconds);
            }
        }
        finally { if (File.Exists(cache)) File.Delete(cache); }
    }

    [Fact(DisplayName = "Local appsettings interval overrides are applied and clamped")]
    public void LocalAppsettingsIntervalOverridesAreAppliedAndClamped()
    {
        var opts = new AgentOptions();
        opts.Collection.IntervalsSeconds[Categories.Software] = 3600;
        opts.Collection.IntervalsSeconds[Categories.Wifi] = 1;
        var p = new RemoteConfigProvider(opts, new SignedEnvelopeVerifier(null, true, TimeSpan.FromMinutes(10)), "x", () => "d", new SystemClock(), NullLogger<RemoteConfigProvider>.Instance);
        Assert.Equal(3600, (int)p.Current.IntervalFor(Categories.Software).TotalSeconds);
        Assert.Equal(RuntimeConfig.MinIntervalSeconds, (int)p.Current.IntervalFor(Categories.Wifi).TotalSeconds);
    }
}
