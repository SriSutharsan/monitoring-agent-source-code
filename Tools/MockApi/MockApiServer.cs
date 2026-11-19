using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Agent.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.MockApi;

public class MockDevice
{
    public string DeviceId;
    public string Hostname;
    public string Status = "pending";
    public string Secret;
    public DateTime RegisteredUtc;
    public RegistrationRequest LastRequest;
    public int RegisterCalls;
}

public class MockRecord
{
    public string DeviceId, Module, Id, Category, CollectedAtUtc, PayloadJson;
    public DateTime ReceivedUtc;
}

/// <summary>
/// Reference implementation of the backend contract, for integration testing the agent. NOT a production server:
/// in-memory storage, no real authorization model. It also has failure injection so offline/retry behaviour can be exercised.
/// </summary>
public sealed class MockApiServer : IDisposable
{
    public string RegistrationToken = "test-token";
    public bool AutoApprove = true;
    public long ConfigVersion;
    public SignedEnvelope ConfigEnvelope;
    public UpdateOffer UpdateToOffer;

    // failure injection
    public int FailNext;                  // next N ingest requests answer FailStatus
    public int FailStatus = 503;
    public int RateLimitNext;             // next N ingest requests answer 429
    public int RetryAfterSeconds = 2;
    public int DropResponseNext;          // process the batch, store it, but answer 500 (simulates a lost acknowledgement)
    public bool RejectPoisonBatches = true;   // batches containing the text "POISON" are answered 400
    public HashSet<string> RejectedByIdList = new HashSet<string>();   // ids returned in the "rejected" list
    public bool RevokeAll;                // answers 401 to every authenticated call

    public readonly ConcurrentDictionary<string, MockDevice> Devices = new ConcurrentDictionary<string, MockDevice>();
    public readonly ConcurrentQueue<MockRecord> Records = new ConcurrentQueue<MockRecord>();
    public readonly ConcurrentDictionary<string, int> IdCounts = new ConcurrentDictionary<string, int>();
    public readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();
    public int IngestRequests;

    private WebApplication _app;
    public int Port { get; private set; }
    public string BaseUrl { get { return "http://127.0.0.1:" + Port; } }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.Server.LocalEndPoint).Port;
        l.Stop();
        return p;
    }

    public MockApiServer(int port = 0)
    {
        Port = port == 0 ? FreePort() : port;
    }

    public int UniqueRecordCount { get { return IdCounts.Count; } }
    public int DuplicateDeliveries { get { return IdCounts.Values.Count(v => v > 1); } }

    public MockApiServer Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Run(Handle);
        _app.StartAsync().GetAwaiter().GetResult();
        return this;
    }

    public void Dispose()
    {
        if (_app != null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _app = null;
        }
    }

    private static async Task Write(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        if (body == null) return;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(Json.Serialize(body));
    }

    private static Task Error(HttpContext ctx, int status, string code, string message)
    {
        return Write(ctx, status, new ApiError { Error = new ApiErrorBody { Code = code, Message = message, RequestId = Guid.NewGuid().ToString("N") } });
    }

    private async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value.Trim('/');
        var parts = path.Split('/');
        ctx.Response.Headers["Date"] = DateTime.UtcNow.ToString("R");

        if (path == "api/v1/ping") { await Write(ctx, 200, null); return; }

        if (path == "api/v1/devices/register" && ctx.Request.Method == "POST")
        {
            var req = Json.Deserialize<RegistrationRequest>(await Read(ctx));
            if (req == null || string.IsNullOrEmpty(req.DeviceId)) { await Error(ctx, 400, "validation_failed", "deviceId is required"); return; }
            MockDevice existing;
            Devices.TryGetValue(req.DeviceId, out existing);
            if (existing == null && req.RegistrationToken != RegistrationToken) { await Error(ctx, 401, "invalid_token", "registration token is not valid"); return; }
            var dev = Devices.GetOrAdd(req.DeviceId, id => new MockDevice { DeviceId = id, RegisteredUtc = DateTime.UtcNow });
            dev.Hostname = req.Hostname;
            dev.RegisterCalls++;
            if (req.Inventory != null || dev.LastRequest == null) dev.LastRequest = req;
            if (dev.Status == "pending" && AutoApprove) Approve(dev);
            await Write(ctx, 200, ToRegistrationResponse(dev));
            return;
        }

        if (parts.Length == 3 && parts[0] == "admin" && parts[1] == "approve")
        {
            MockDevice d;
            if (Devices.TryGetValue(parts[2], out d)) { Approve(d); await Write(ctx, 200, null); } else await Write(ctx, 404, null);
            return;
        }

        // /api/v1/devices/{id}/{module|config}
        if (parts.Length == 5 && parts[0] == "api" && parts[2] == "devices")
        {
            var deviceId = parts[3];
            var what = parts[4];
            MockDevice dev;
            var auth = ctx.Request.Headers["Authorization"].ToString();
            if (RevokeAll || !Devices.TryGetValue(deviceId, out dev) || dev.Status != "approved" || auth != "Bearer " + dev.Secret)
            {
                await Error(ctx, 401, "unauthorized", "invalid device credential");
                return;
            }

            if (what == "config" && ctx.Request.Method == "GET")
            {
                if (ConfigEnvelope == null) await Write(ctx, 204, null); else await Write(ctx, 200, ConfigEnvelope);
                return;
            }

            if (ctx.Request.Method == "POST")
            {
                Interlocked_Increment();
                var body = await Read(ctx);
                if (RateLimitNext > 0) { RateLimitNext--; ctx.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString(); await Error(ctx, 429, "rate_limited", "slow down"); return; }
                if (FailNext > 0) { FailNext--; await Error(ctx, FailStatus, "server_error", "injected failure"); return; }
                if (RejectPoisonBatches && body.Contains("POISON")) { await Error(ctx, 400, "validation_failed", "payload failed validation"); return; }

                JsonDocument doc;
                try { doc = JsonDocument.Parse(body); }
                catch (JsonException) { await Error(ctx, 400, "invalid_json", "body is not JSON"); return; }
                var accepted = new List<string>();
                var rejected = new List<RejectedRecord>();
                using (doc)
                {
                    foreach (var rec in doc.RootElement.GetProperty("records").EnumerateArray())
                    {
                        var id = rec.GetProperty("id").GetString();
                        if (RejectedByIdList.Contains(id)) { rejected.Add(new RejectedRecord { Id = id, Code = "schema", Message = "unsupported", Retryable = false }); continue; }
                        IdCounts.AddOrUpdate(id, 1, (k, v) => v + 1);
                        Records.Enqueue(new MockRecord
                        {
                            DeviceId = deviceId, Module = what, Id = id, Category = rec.GetProperty("category").GetString(),
                            CollectedAtUtc = rec.GetProperty("collectedAtUtc").GetString(), PayloadJson = rec.GetProperty("payload").GetRawText(), ReceivedUtc = DateTime.UtcNow
                        });
                        accepted.Add(id);
                    }
                }
                if (DropResponseNext > 0) { DropResponseNext--; await Error(ctx, 500, "server_error", "lost after processing"); return; }
                await Write(ctx, 200, new IngestResponse
                {
                    Status = "ok", Accepted = accepted, Rejected = rejected.Count > 0 ? rejected : null,
                    ServerTimeUtc = DateTime.UtcNow, ConfigVersion = ConfigVersion, Update = UpdateToOffer
                });
                return;
            }
        }
        await Error(ctx, 404, "not_found", "unknown route");
    }

    private void Interlocked_Increment()
    {
        System.Threading.Interlocked.Increment(ref IngestRequests);
    }

    private static async Task<string> Read(HttpContext ctx)
    {
        using (var sr = new System.IO.StreamReader(ctx.Request.Body, Encoding.UTF8)) return await sr.ReadToEndAsync();
    }

    public void Approve(MockDevice d)
    {
        d.Status = "approved";
        if (d.Secret == null)
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            d.Secret = Convert.ToBase64String(bytes);
        }
    }

    private static RegistrationResponse ToRegistrationResponse(MockDevice d)
    {
        var r = new RegistrationResponse { Status = d.Status, DeviceId = d.DeviceId, PollAfterSeconds = 10, ServerTimeUtc = DateTime.UtcNow };
        if (d.Status == "approved") r.Credential = new DeviceCredential { Secret = d.Secret, KeyId = "k1" };
        return r;
    }
}
