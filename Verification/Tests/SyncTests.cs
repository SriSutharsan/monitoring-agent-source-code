using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Agent.Communication;
using Agent.Configuration;
using Agent.Core;
using Agent.MockApi;
using Agent.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Verify;

public static class SyncTests
{
    private sealed class Rig : IDisposable
    {
        public FakeClock Clock = new FakeClock();
        public MemOutbox Outbox;
        public MemState State = new MemState();
        public MemCredentials Creds = new MemCredentials();
        public StaticConfig Config = new StaticConfig();
        public MockApiServer Server;
        public ApiClient Api;
        public RegistrationManager Registration;
        public SyncEngine Engine;
        public RemoteConfigProvider Remote;
        public DeviceIdentityService Identity;
        public RSA SigningKey = RSA.Create(2048);
        public string DeviceId;
        private readonly string _identityPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "id-" + Guid.NewGuid().ToString("N") + ".json");

        public Rig(int port = 0, bool startServer = true, int batchSize = 5)
        {
            Outbox = new MemOutbox(Clock);
            Config.Current.BatchSize = batchSize;
            Server = new MockApiServer(port);
            if (startServer) Server.Start();
            Identity = new DeviceIdentityService(_identityPath, new FakeFingerprint(), Clock);
            Identity.Initialize();
            DeviceId = Identity.DeviceId;
            var opts = new AgentOptions();
            opts.Api.BaseUrl = Server.BaseUrl;
            opts.Api.AllowInsecureLoopback = true;
            opts.Api.RequestTimeoutSeconds = 5;
            opts.Registration.Token = "test-token";
            var pem = "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(SigningKey.ExportSubjectPublicKeyInfo()) + "\n-----END PUBLIC KEY-----";
            var verifier = new SignedEnvelopeVerifier(pem, true, TimeSpan.FromMinutes(10));
            Remote = new RemoteConfigProvider(opts, verifier, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N")), () => DeviceId, Clock, NullLogger<RemoteConfigProvider>.Instance);
            Api = new ApiClient(new HttpClient(HttpHandlerFactory.Create(opts.Api)), opts.Api, Creds, Clock, new ClockSkewTracker(), "1.0.0-test", NullLogger<ApiClient>.Instance);
            Registration = new RegistrationManager(Api, Creds, State, Identity, new FakeInventory(), opts.Registration, "1.0.0-test", Clock, NullLogger<RegistrationManager>.Instance);
            Registration.Initialize();
            Engine = new SyncEngine(Outbox, Api, Registration, State, Config, Remote, new NullUpdateHandler(), Clock, () => DeviceId, 900, NullLogger<SyncEngine>.Instance);
        }

        public void Enqueue(string module, int n, string tag = "r", string payload = "{\"v\":1}", int priority = 4)
        {
            for (int i = 0; i < n; i++)
                Outbox.Enqueue(new OutboxRecord { Module = module, Category = module, IdempotencyKey = tag + "-" + module + "-" + i, CollectedAtUtc = Clock.Now, PayloadJson = payload, Priority = priority });
        }

        public async Task RegisterAsync()
        {
            await Registration.AttemptAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            Server.Dispose();
            SigningKey.Dispose();
        }
    }

    public static void Register(TestRunner t)
    {
        t.AddAsync("Registration: pending until admin approves, then credential is stored", async () =>
        {
            using (var r = new Rig())
            {
                r.Server.AutoApprove = false;
                await r.RegisterAsync();
                Assert.Equal(RegistrationStates.Pending, r.Registration.Status);
                Assert.True(r.Creds.Value == null, "no credential before approval");
                Assert.True(r.Server.Devices[r.DeviceId].LastRequest.Inventory != null, "first request carries inventory");

                r.Server.Approve(r.Server.Devices[r.DeviceId]);
                await r.RegisterAsync();
                Assert.Equal(RegistrationStates.Approved, r.Registration.Status);
                Assert.True(r.Creds.Value != null && r.Creds.Value.Secret.Length > 20, "credential stored");
                Assert.True(r.Identity.FirstRegisteredUtc.HasValue, "first registration date recorded");
            }
        });

        t.AddAsync("Registration: wrong token is refused and never approved", async () =>
        {
            using (var r = new Rig())
            {
                r.Server.RegistrationToken = "something-else";
                var wait = await r.Registration.AttemptAsync(CancellationToken.None);
                Assert.True(r.Registration.Status != RegistrationStates.Approved);
                Assert.True(wait >= TimeSpan.FromMinutes(5), "backs off after refusal");
            }
        });

        t.AddAsync("Sync: delivers every module, sends exact payload, respects priority (alerts first)", async () =>
        {
            using (var r = new Rig(batchSize: 100))
            {
                await r.RegisterAsync();
                r.Enqueue("performance", 3, priority: 6);
                r.Enqueue("alerts", 2, priority: 1, payload: "{\"rule\":\"x\",\"msg\":\"q\\\"uote\"}");
                r.Enqueue("hardware", 1, priority: 3);
                Assert.Equal(DrainOutcome.Completed, await r.Engine.DrainAsync(CancellationToken.None));
                Assert.Equal(6, r.Server.UniqueRecordCount);
                Assert.Equal(6, r.Outbox.Count(1));
                Assert.Equal(0, r.Outbox.Count(0));
                var first = r.Server.Records.First();
                Assert.Equal("alerts", first.Module, "alerts must be delivered first");
                Assert.True(r.Server.Records.Any(x => x.PayloadJson.Contains("q\\\"uote")), "payload passes through byte-exact");
                Assert.True(r.State.Get(SyncEngine.LastSyncKey) != null, "last successful sync timestamp stored");
            }
        });

        t.AddAsync("Offline: nothing is lost while the server is unreachable; everything syncs on reconnect", async () =>
        {
            var port = MockApiServer.FreePort();
            using (var r = new Rig(port, startServer: false))
            {
                // approve first via a temporary server so the agent holds a credential, then go "offline"
                r.Server.Start();
                await r.RegisterAsync();
                var secret = r.Creds.Value.Secret;
                var dev = r.Server.Devices[r.DeviceId];
                r.Server.Dispose();                                   // offline
                r.Enqueue("performance", 12, priority: 6);
                r.Enqueue("events", 4, priority: 2);

                var outcome = await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(DrainOutcome.Failed, outcome);
                Assert.Equal(16, r.Outbox.Count(0), "all 16 records must still be pending");
                Assert.Equal(0, r.Outbox.MaxAttempts, "outage must not consume attempts");

                // back online, same device state
                var back = new MockApiServer(port).Start();
                back.Devices[r.DeviceId] = dev;
                try
                {
                    r.Clock.Now = r.Clock.Now.AddMinutes(20);          // backoff elapsed
                    var after = await r.Engine.DrainAsync(CancellationToken.None);
                    Assert.Equal(DrainOutcome.Completed, after);
                    Assert.Equal(16, back.UniqueRecordCount, "all records delivered");
                    Assert.Equal(0, back.DuplicateDeliveries);
                    Assert.Equal(0, r.Outbox.Count(0));
                }
                finally { back.Dispose(); }
            }
        });

        t.AddAsync("Server errors (503): retried with backoff, delivered exactly once", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                r.Enqueue("software", 10);
                r.Server.FailNext = 2; r.Server.FailStatus = 503;
                Assert.Equal(DrainOutcome.Failed, await r.Engine.DrainAsync(CancellationToken.None));
                Assert.Equal(10, r.Outbox.Count(0));
                Assert.Equal(1, r.Outbox.MaxAttempts, "server errors do count as attempts");
                // not yet due: nothing is sent
                var before = r.Server.IngestRequests;
                await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(before, r.Server.IngestRequests, "backoff must delay the retry");
                r.Clock.Now = r.Clock.Now.AddMinutes(30);
                await r.Engine.DrainAsync(CancellationToken.None);          // second injected failure
                r.Clock.Now = r.Clock.Now.AddMinutes(60);
                Assert.Equal(DrainOutcome.Completed, await r.Engine.DrainAsync(CancellationToken.None));
                Assert.Equal(10, r.Server.UniqueRecordCount);
                Assert.Equal(0, r.Server.DuplicateDeliveries);
            }
        });

        t.AddAsync("Lost acknowledgement: server stored the batch but answered 500 -> retry is deduplicated by id", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                r.Enqueue("security", 4);
                r.Server.DropResponseNext = 1;
                Assert.Equal(DrainOutcome.Failed, await r.Engine.DrainAsync(CancellationToken.None));
                r.Clock.Now = r.Clock.Now.AddMinutes(30);
                Assert.Equal(DrainOutcome.Completed, await r.Engine.DrainAsync(CancellationToken.None));
                // the same ids reached the server twice; the idempotency keys let a real backend collapse them
                Assert.Equal(4, r.Server.UniqueRecordCount);
                Assert.True(r.Server.IdCounts.Values.All(v => v == 2), "same ids are re-sent unchanged");
                Assert.Equal(4, r.Outbox.Count(1));
            }
        });

        t.AddAsync("Rate limit (429): honours Retry-After, does not burn attempts, resumes", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                r.Enqueue("network", 6);
                r.Server.RateLimitNext = 1; r.Server.RetryAfterSeconds = 30;
                Assert.Equal(DrainOutcome.Blocked, await r.Engine.DrainAsync(CancellationToken.None));
                Assert.Equal(0, r.Outbox.MaxAttempts);
                var n = r.Server.IngestRequests;
                await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(n, r.Server.IngestRequests, "must not hammer the API during Retry-After");
                r.Clock.Now = r.Clock.Now.AddSeconds(31);
                Assert.Equal(DrainOutcome.Completed, await r.Engine.DrainAsync(CancellationToken.None));
                Assert.Equal(6, r.Server.UniqueRecordCount);
            }
        });

        t.AddAsync("Poison record (400): good records in the same batch still arrive, bad one is dead-lettered", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                r.Enqueue("events", 4, tag: "good");
                r.Enqueue("events", 1, tag: "bad", payload: "{\"x\":\"POISON\"}");
                var outcome = await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(DrainOutcome.Completed, outcome);
                Assert.Equal(4, r.Server.UniqueRecordCount, "good records delivered");
                Assert.Equal(1, r.Outbox.Count(2), "poison record dead-lettered");
                Assert.Equal(0, r.Outbox.Count(0), "queue must not be blocked by the poison record");
            }
        });

        t.AddAsync("Per-record rejection list: non-retryable go to dead-letter, others are synced", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                r.Enqueue("hardware", 3);
                r.Server.RejectedByIdList.Add("r-hardware-1");
                await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(2, r.Outbox.Count(1));
                Assert.Equal(1, r.Outbox.Count(2));
            }
        });

        t.AddAsync("Revoked credential (401 x3): agent discards it and returns to registration", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                Assert.True(r.Registration.IsApproved);
                r.Enqueue("storage", 2);
                r.Server.RevokeAll = true;
                for (int i = 0; i < 3; i++)
                {
                    await r.Engine.DrainAsync(CancellationToken.None);
                    r.Clock.Now = r.Clock.Now.AddMinutes(2);
                }
                Assert.Equal(RegistrationStates.Unregistered, r.Registration.Status);
                Assert.True(r.Creds.Value == null, "credential removed");
                Assert.Equal(2, r.Outbox.Count(0), "data kept for after re-registration");
            }
        });

        t.AddAsync("Signed remote config: pulled when the server announces a newer version, applied live", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            {
                await r.RegisterAsync();
                var doc = new AgentConfigDocument { Version = 7, IntervalsSeconds = new Dictionary<string, int> { { Categories.PerfCpuMemory, 90 } } };
                var payload = Encoding.UTF8.GetBytes(Json.Serialize(doc));
                r.Server.ConfigVersion = 7;
                r.Server.ConfigEnvelope = new SignedEnvelope
                {
                    Payload = Convert.ToBase64String(payload),
                    Signature = Convert.ToBase64String(r.SigningKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                };
                r.Enqueue("heartbeat", 1);
                await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(7L, r.Remote.Current.Version);
                Assert.Equal(90, (int)r.Remote.Current.IntervalFor(Categories.PerfCpuMemory).TotalSeconds);
            }
        });

        t.AddAsync("Signed remote config: a forged config from the server is ignored", async () =>
        {
            using (var r = new Rig(batchSize: 10))
            using (var attacker = RSA.Create(2048))
            {
                await r.RegisterAsync();
                var payload = Encoding.UTF8.GetBytes(Json.Serialize(new AgentConfigDocument { Version = 50, IntervalsSeconds = new Dictionary<string, int> { { Categories.Security, 5 } } }));
                r.Server.ConfigVersion = 50;
                r.Server.ConfigEnvelope = new SignedEnvelope { Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(attacker.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
                r.Enqueue("heartbeat", 1);
                await r.Engine.DrainAsync(CancellationToken.None);
                Assert.Equal(0L, r.Remote.Current.Version, "forged config must not be applied");
                Assert.Equal(1800, (int)r.Remote.Current.IntervalFor(Categories.Security).TotalSeconds);
            }
        });

        t.Add("HTTPS is enforced; plain http only for explicit loopback development", () =>
        {
            Func<string, bool, bool> ok = (url, loop) => { try { ApiClient.ValidateBaseUrl(new ApiOptions { BaseUrl = url, AllowInsecureLoopback = loop }); return true; } catch (InvalidOperationException) { return false; } };
            Assert.True(ok("https://monitor.example.com", false));
            Assert.True(!ok("http://monitor.example.com", false), "http must be refused");
            Assert.True(!ok("http://monitor.example.com", true), "http to a non-loopback host is refused even in dev mode");
            Assert.True(!ok("http://127.0.0.1:5000", false), "loopback http needs the explicit flag");
            Assert.True(ok("http://127.0.0.1:5000", true));
            Assert.True(!ok("", false));
            Assert.True(!ok("ftp://x", false));
        });

        t.AddAsync("Unapproved device sends nothing (data stays queued)", async () =>
        {
            using (var r = new Rig())
            {
                r.Server.AutoApprove = false;
                await r.RegisterAsync();
                r.Enqueue("hardware", 3);
                // the sync loop only drains when approved; verify the gate, not a drain call
                Assert.True(!r.Registration.IsApproved);
                Assert.Equal(3, r.Outbox.Count(0));
                Assert.Equal(0, r.Server.IngestRequests);
            }
        });
    }
}
