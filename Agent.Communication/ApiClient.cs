using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Agent.Configuration;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Communication;

public static class HttpHandlerFactory
{
    /// <summary>
    /// Builds the HTTP handler. Certificates are validated by the OS (no bypass). Optional pinning adds a second check.
    /// TLS 1.2 is forced on, which is what makes the net48 build work on Windows 7 SP1.
    /// </summary>
    public static HttpClientHandler Create(ApiOptions options)
    {
        try
        {
            // Tls12 = 3072, Tls13 = 12288 (the latter is ignored by OS versions that do not support it)
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch (Exception) { }
            ServicePointManager.Expect100Continue = false;
        }
        catch (Exception) { }

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false          // a redirect could leak the device credential to another host
        };
        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            handler.Proxy = new WebProxy(options.ProxyUrl);
            handler.UseProxy = true;
        }
        var pins = (options.PinnedCertificateSha256 ?? new System.Collections.Generic.List<string>())
            .Select(p => (p ?? "").Replace(":", "").Replace(" ", "").ToLowerInvariant()).Where(p => p.Length > 0).ToList();
        if (pins.Count > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            {
                if (errors != SslPolicyErrors.None || cert == null) return false;
                if (Matches(cert, pins)) return true;
                if (chain != null)
                    foreach (var el in chain.ChainElements)
                        if (Matches(el.Certificate, pins)) return true;
                return false;
            };
        }
        return handler;
    }

    private static bool Matches(X509Certificate2 cert, System.Collections.Generic.List<string> pins)
    {
        if (cert == null) return false;
        return pins.Contains(Hashing.Sha256Hex(cert.RawData));
    }
}

public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private readonly ApiOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly IClock _clock;
    private readonly ClockSkewTracker _skew;
    private readonly string _agentVersion;
    private readonly ILogger _log;
    private readonly Uri _baseUri;
    private volatile DeviceCredential _cachedCredential;

    public ApiClient(HttpClient http, ApiOptions options, ICredentialStore credentials, IClock clock, ClockSkewTracker skew,
        string agentVersion, ILogger<ApiClient> log)
    {
        _http = http;
        _options = options;
        _credentials = credentials;
        _clock = clock;
        _skew = skew;
        _agentVersion = agentVersion;
        _log = log;
        _baseUri = ValidateBaseUrl(options);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds));
    }

    /// <summary>HTTPS is mandatory. Plain http is accepted only for loopback addresses and only when explicitly enabled for development.</summary>
    public static Uri ValidateBaseUrl(ApiOptions o)
    {
        Uri uri;
        if (string.IsNullOrWhiteSpace(o.BaseUrl) || !Uri.TryCreate(o.BaseUrl.Trim(), UriKind.Absolute, out uri))
            throw new InvalidOperationException("Agent:Api:BaseUrl is not a valid absolute URL");
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && o.AllowInsecureLoopback && uri.IsLoopback) return uri;
        throw new InvalidOperationException("Agent:Api:BaseUrl must use https");
    }

    public void InvalidateCredentialCache()
    {
        _cachedCredential = null;
    }

    private Uri Url(string relative)
    {
        var path = "api/" + _options.ApiVersion.Trim('/') + "/" + relative.TrimStart('/');
        return new Uri(_baseUri, path);
    }

    public Task<ApiResult<RegistrationResponse>> RegisterAsync(RegistrationRequest request, CancellationToken ct)
    {
        return SendAsync<RegistrationResponse>(HttpMethod.Post, Url("devices/register"), Json.Serialize(request), null, false, ct);
    }

    public Task<ApiResult<IngestResponse>> SendBatchAsync(string deviceId, string module, string bodyJson, string idempotencyKey, CancellationToken ct)
    {
        var url = Url("devices/" + Uri.EscapeDataString(deviceId) + "/" + Uri.EscapeDataString(module));
        return SendAsync<IngestResponse>(HttpMethod.Post, url, bodyJson, idempotencyKey, true, ct);
    }

    public Task<ApiResult<SignedEnvelope>> GetConfigAsync(string deviceId, CancellationToken ct)
    {
        return SendAsync<SignedEnvelope>(HttpMethod.Get, Url("devices/" + Uri.EscapeDataString(deviceId) + "/config"), null, null, true, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        var r = await SendAsync<object>(HttpMethod.Get, Url("ping"), null, null, false, ct).ConfigureAwait(false);
        return r.Outcome != ApiOutcome.NetworkError;
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, Uri url, string body, string idempotencyKey, bool authenticated, CancellationToken ct) where T : class
    {
        var result = new ApiResult<T>();
        try
        {
            using (var req = new HttpRequestMessage(method, url))
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.UserAgent.ParseAdd("EndpointAgent/" + _agentVersion);
                req.Headers.TryAddWithoutValidation("X-Agent-Version", _agentVersion);
                if (idempotencyKey != null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                if (authenticated)
                {
                    var cred = _cachedCredential ?? (_cachedCredential = _credentials.Load());
                    if (cred == null || string.IsNullOrEmpty(cred.Secret))
                    {
                        result.Outcome = ApiOutcome.Unauthorized;
                        result.Error = "no device credential";
                        return result;
                    }
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Secret);
                    if (!string.IsNullOrEmpty(cred.KeyId)) req.Headers.TryAddWithoutValidation("X-Key-Id", cred.KeyId);
                }
                if (body != null) req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");

                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    result.StatusCode = (int)resp.StatusCode;
                    if (resp.Headers.Date.HasValue)
                    {
                        result.ServerTimeUtc = resp.Headers.Date.Value.UtcDateTime;
                        _skew.Observe(result.ServerTimeUtc.Value, _clock.UtcNow);
                    }
                    if (resp.Headers.RetryAfter != null)
                    {
                        if (resp.Headers.RetryAfter.Delta.HasValue) result.RetryAfter = resp.Headers.RetryAfter.Delta;
                        else if (resp.Headers.RetryAfter.Date.HasValue)
                            result.RetryAfter = resp.Headers.RetryAfter.Date.Value.UtcDateTime - _clock.UtcNow;
                        if (result.RetryAfter.HasValue && result.RetryAfter.Value < TimeSpan.Zero) result.RetryAfter = TimeSpan.Zero;
                    }
                    var text = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Classify(result, resp.StatusCode, text);
                }
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Outcome = ApiOutcome.NetworkError;
            result.Error = "request timed out";
        }
        catch (HttpRequestException ex)
        {
            result.Outcome = ApiOutcome.NetworkError;
            result.Error = Text.Truncate(Describe(ex), 300);
        }
        catch (WebException ex)
        {
            result.Outcome = ApiOutcome.NetworkError;
            result.Error = Text.Truncate(ex.Message, 300);
        }
        return result;
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder(ex.Message);
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) sb.Append(" -> ").Append(inner.Message);
        return sb.ToString();
    }

    private static void Classify<T>(ApiResult<T> r, HttpStatusCode status, string text) where T : class
    {
        int code = (int)status;
        if (code >= 200 && code < 300)
        {
            if (string.IsNullOrWhiteSpace(text) || typeof(T) == typeof(object))
            {
                r.Outcome = ApiOutcome.Success;                      // 204 / empty body: nothing to validate, all accepted
                return;
            }
            T value;
            if (Json.TryDeserialize(text, out value)) { r.Value = value; r.Outcome = ApiOutcome.Success; }
            else { r.Outcome = ApiOutcome.InvalidResponse; r.Error = "response body is not valid JSON for this endpoint"; }
            return;
        }

        ApiError err;
        if (!string.IsNullOrWhiteSpace(text) && Json.TryDeserialize(text, out err) && err.Error != null)
        {
            r.Error = Text.Truncate(err.Error.Message, 300);
            r.ErrorCode = err.Error.Code;
        }
        else r.Error = "HTTP " + code;

        if (code == 401) r.Outcome = ApiOutcome.Unauthorized;
        else if (code == 403) r.Outcome = ApiOutcome.Forbidden;
        else if (code == 429) r.Outcome = ApiOutcome.RateLimited;
        else if (code == 408 || code == 425) r.Outcome = ApiOutcome.ServerError;        // transient by definition
        else if (code >= 400 && code < 500) r.Outcome = ApiOutcome.Rejected;
        else if (code >= 300 && code < 400) { r.Outcome = ApiOutcome.Rejected; r.Error = "unexpected redirect (not followed)"; }
        else r.Outcome = ApiOutcome.ServerError;
    }
}
