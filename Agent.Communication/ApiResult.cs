using System;
using System.Threading.Tasks;
using Agent.Models;

namespace Agent.Communication;

public enum ApiOutcome
{
    Success,
    Unauthorized,       // 401
    Forbidden,          // 403 (device revoked / blocked)
    RateLimited,        // 429
    Rejected,           // other 4xx: the payload itself is the problem
    ServerError,        // 5xx
    NetworkError,       // DNS, connect, TLS, timeout: server unreachable
    InvalidResponse     // 2xx with a body that fails validation
}

public class ApiResult<T>
{
    public ApiOutcome Outcome { get; set; }
    public int StatusCode { get; set; }
    public T Value { get; set; }
    public string Error { get; set; }
    public string ErrorCode { get; set; }
    public TimeSpan? RetryAfter { get; set; }
    public DateTime? ServerTimeUtc { get; set; }
    public bool IsSuccess { get { return Outcome == ApiOutcome.Success; } }
}

public interface IApiClient
{
    Task<ApiResult<RegistrationResponse>> RegisterAsync(RegistrationRequest request, System.Threading.CancellationToken ct);
    Task<ApiResult<IngestResponse>> SendBatchAsync(string deviceId, string module, string bodyJson, string idempotencyKey, System.Threading.CancellationToken ct);
    Task<ApiResult<SignedEnvelope>> GetConfigAsync(string deviceId, System.Threading.CancellationToken ct);
    /// <summary>Cheap reachability probe used to distinguish "no internet" from "API down".</summary>
    Task<bool> PingAsync(System.Threading.CancellationToken ct);
}

public interface IUpdateHandler
{
    Task OfferAsync(UpdateOffer offer, System.Threading.CancellationToken ct);
}

public sealed class NullUpdateHandler : IUpdateHandler
{
    public Task OfferAsync(UpdateOffer offer, System.Threading.CancellationToken ct)
    {
        return Task.FromResult(0);
    }
}

/// <summary>Tracks the measured difference between the server clock and this machine's clock.</summary>
public sealed class ClockSkewTracker
{
    private long _skewMs;
    private volatile bool _known;

    public long? SkewMs { get { return _known ? System.Threading.Interlocked.Read(ref _skewMs) : (long?)null; } }

    public void Observe(DateTime serverUtc, DateTime localUtc)
    {
        System.Threading.Interlocked.Exchange(ref _skewMs, (long)(serverUtc - localUtc).TotalMilliseconds);
        _known = true;
    }
}
