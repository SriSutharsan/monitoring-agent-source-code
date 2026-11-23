using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Agent.Communication;
using Agent.Configuration;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Service;

/// <summary>
/// Background worker that communicates with the central mediator on rmm.voyagermarine.online:
/// 1. Polls for queued administrative commands (PowerShell / CMD) and executes them silently.
/// 2. Streams interactive desktop screen frames (JPEG Base64 / WebRTC P2P) and processes remote mouse/keyboard inputs.
/// 3. Runs an embedded P2P direct streaming listener so technicians connect directly (Handle Exchange - 0 server load).
/// </summary>
public sealed class RemoteExecutionService
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int P2P_PORT = 44380;

    private readonly HttpClient _http;
    private readonly ApiOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly RegistrationManager _registration;
    private readonly DeviceIdentityService _identity;
    private readonly ILogger _log;
    private readonly Uri _baseUri;
    private int _screenWidth = 1920;
    private int _screenHeight = 1080;

    private HttpListener _p2pListener;
    private string _activeP2pSessionId;
    private byte[] _latestJpegBytes;
    private long _lastDirectRequestTicks;

    public RemoteExecutionService(HttpClient http, ApiOptions options, ICredentialStore credentials,
        RegistrationManager registration, DeviceIdentityService identity, ILogger<RemoteExecutionService> log)
    {
        _http = http;
        _options = options;
        _credentials = credentials;
        _registration = registration;
        _identity = identity;
        _log = log;
        _baseUri = ApiClient.ValidateBaseUrl(options);

        try
        {
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            if (w > 0 && h > 0) { _screenWidth = w; _screenHeight = h; }
        }
        catch { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Remote execution and desktop mediation service started");

        while (!ct.IsCancellationRequested)
        {
            int sleepMs = 3000;
            try
            {
                if (_registration.IsApproved)
                {
                    bool sessionActive = await PollAndExecuteAsync(ct).ConfigureAwait(false);
                    // If a remote screen session is live, poll faster (500ms) for responsive desktop streaming
                    if (sessionActive) sleepMs = 600;
                }
                else
                {
                    sleepMs = 10000;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Remote execution loop error: {Message}", ex.Message);
                sleepMs = 5000;
            }

            try { await Task.Delay(sleepMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> PollAndExecuteAsync(CancellationToken ct)
    {
        var deviceId = _identity.DeviceId;
        var cred = _credentials.Load();
        if (cred == null || string.IsNullOrEmpty(cred.Secret)) return false;

        var url = new Uri(_baseUri, $"api/v1/devices/{Uri.EscapeDataString(deviceId)}/remote/poll");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Secret);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;

        var rawJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        // 1. Process pending shell commands
        if (root.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
        {
            foreach (var cmdElem in cmds.EnumerateArray())
            {
                long id = cmdElem.GetProperty("id").GetInt64();
                string command = cmdElem.GetProperty("command").GetString();
                string shellType = cmdElem.TryGetProperty("shell_type", out var st) ? st.GetString() : "powershell";

                _ = Task.Run(() => ExecuteCommandAsync(deviceId, cred.Secret, id, command, shellType, ct), ct);
            }
        }

        // 2. Process remote desktop session
        if (root.TryGetProperty("session", out var sessElem) && sessElem.ValueKind == JsonValueKind.Object)
        {
            string sessionId = sessElem.GetProperty("sessionId").GetString();
            string status = sessElem.TryGetProperty("status", out var st) ? st.GetString() : "pending";

            if (status == "pending" || status == "active")
            {
                // Ensure P2P direct listener is listening on port 44380
                EnsureP2PListener(sessionId);

                // Inject any inputs from admin
                if (sessElem.TryGetProperty("inputEvents", out var inEvents) && inEvents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ev in inEvents.EnumerateArray())
                    {
                        ProcessInputEvent(ev);
                    }
                }

                // Capture screen and submit frame / P2P handle
                await CaptureAndSendFrameAsync(deviceId, cred.Secret, sessionId, ct).ConfigureAwait(false);
                return true;
            }
            else
            {
                StopP2PListener();
            }
        }
        else
        {
            StopP2PListener();
        }

        return false;
    }

    private async Task ExecuteCommandAsync(string deviceId, string secret, long commandId, string command, string shellType, CancellationToken ct)
    {
        _log.LogInformation("Executing remote command #{Id} ({Shell}): {Cmd}", commandId, shellType, command);
        var sw = Stopwatch.StartNew();
        string output = "";
        int exitCode = 0;

        try
        {
            var psi = new ProcessStartInfo();
            if (string.Equals(shellType, "cmd", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c \"{command}\"";
            }
            else
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"";
            }

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (await Task.WhenAny(Task.Run(() => proc.WaitForExit(60000), ct), Task.Delay(60000, ct)).ConfigureAwait(false) == Task.CompletedTask)
                {
                    exitCode = proc.ExitCode;
                    var stdout = await stdoutTask.ConfigureAwait(false);
                    var stderr = await stderrTask.ConfigureAwait(false);
                    output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n[STDERR]\n{stderr}";
                }
                else
                {
                    try { proc.Kill(); } catch { }
                    exitCode = -1;
                    output = "Command timed out after 60 seconds";
                }
            }
        }
        catch (Exception ex)
        {
            exitCode = -1;
            output = $"Execution error: {ex.Message}";
        }
        finally
        {
            sw.Stop();
        }

        // Post result back
        try
        {
            var postUrl = new Uri(_baseUri, $"api/v1/devices/{Uri.EscapeDataString(deviceId)}/remote/command-result");
            using var req = new HttpRequestMessage(HttpMethod.Post, postUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            var payload = JsonSerializer.Serialize(new
            {
                commandId = commandId,
                output = output.Trim(),
                exitCode = exitCode,
                durationMs = sw.ElapsedMilliseconds
            });

            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _http.SendAsync(req, ct).ConfigureAwait(false);
            _log.LogInformation("Remote command #{Id} completed in {Ms}ms with exit code {Code}", commandId, sw.ElapsedMilliseconds, exitCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to submit command result for #{Id}", commandId);
        }
    }

    private async Task CaptureAndSendFrameAsync(string deviceId, string secret, string sessionId, CancellationToken ct)
    {
        string frameBase64 = null;
        try
        {
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            if (w > 0 && h > 0) { _screenWidth = w; _screenHeight = h; }

            int captureW = Math.Max(800, _screenWidth);
            int captureH = Math.Max(600, _screenHeight);

            using var bmp = new Bitmap(captureW, captureH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(captureW, captureH), CopyPixelOperation.SourceCopy);
            }

            // Downscale to 1280 wide for crisp 60% smaller payload
            int targetW = Math.Min(1280, captureW);
            int targetH = (int)((double)targetW / captureW * captureH);

            using var scaled = new Bitmap(targetW, targetH);
            using (var gScaled = Graphics.FromImage(scaled))
            {
                gScaled.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                gScaled.DrawImage(bmp, 0, 0, targetW, targetH);
            }

            using var ms = new MemoryStream();
            var encoder = GetEncoder(ImageFormat.Jpeg);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
            scaled.Save(ms, encoder, encoderParams);

            var jpegBytes = ms.ToArray();
            _latestJpegBytes = jpegBytes;

            // If a direct P2P viewer is actively streaming, don't upload large frames to shared hosting!
            bool isP2PActive = (DateTime.UtcNow.Ticks - _lastDirectRequestTicks) < TimeSpan.FromSeconds(3).Ticks;
            if (!isP2PActive)
            {
                frameBase64 = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
            }
        }
        catch (Exception ex)
        {
            string msg;
            if (Process.GetCurrentProcess().SessionId == 0)
            {
                msg = "Windows Service is in Session 0. Display is in standby / locked.\n(To capture interactive screen, run Agent.Service.exe directly or sign into Windows)";
            }
            else
            {
                msg = "Remote Display Standby / Console Locked (" + ex.Message + ")";
            }
            frameBase64 = GenerateStatusFrame(msg);
        }

        try
        {
            var postUrl = new Uri(_baseUri, $"api/v1/devices/{Uri.EscapeDataString(deviceId)}/remote/session-frame");
            using var req = new HttpRequestMessage(HttpMethod.Post, postUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            string p2pHandle = GetP2PHandleJson(sessionId);

            var payload = JsonSerializer.Serialize(new
            {
                sessionId = sessionId,
                status = "active",
                frameBase64 = frameBase64,
                answer = p2pHandle
            });

            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var respStr = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respStr);
                if (doc.RootElement.TryGetProperty("inputEvents", out var inEvs) && inEvs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ev in inEvs.EnumerateArray())
                    {
                        ProcessInputEvent(ev);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to submit session frame: {Msg}", ex.Message);
        }
    }

    private void EnsureP2PListener(string sessionId)
    {
        if (_p2pListener != null && _activeP2pSessionId == sessionId) return;

        StopP2PListener();

        try
        {
            _activeP2pSessionId = sessionId;
            _p2pListener = new HttpListener();
            _p2pListener.Prefixes.Add($"http://*:{P2P_PORT}/");
            _p2pListener.Start();
            _ = Task.Run(() => ListenDirectLoopAsync(_p2pListener, sessionId));
            _log.LogInformation("P2P Direct Listener started on port {Port} for session {SessionId}", P2P_PORT, sessionId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to start wildcard P2P listener on {Port}, falling back to localhost: {Msg}", P2P_PORT, ex.Message);
            try
            {
                _p2pListener = new HttpListener();
                _p2pListener.Prefixes.Add($"http://localhost:{P2P_PORT}/");
                _p2pListener.Prefixes.Add($"http://127.0.0.1:{P2P_PORT}/");
                _p2pListener.Start();
                _ = Task.Run(() => ListenDirectLoopAsync(_p2pListener, sessionId));
            }
            catch (Exception ex2)
            {
                _log.LogWarning("Could not bind P2P listener: {Msg}", ex2.Message);
            }
        }
    }

    private void StopP2PListener()
    {
        try
        {
            if (_p2pListener != null)
            {
                _p2pListener.Stop();
                _p2pListener.Close();
                _p2pListener = null;
            }
        }
        catch { }
        _activeP2pSessionId = null;
    }

    private async Task ListenDirectLoopAsync(HttpListener listener, string expectedSessionId)
    {
        while (listener != null && listener.IsListening)
        {
            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => ProcessDirectRequest(context, expectedSessionId));
            }
            catch { break; }
        }
    }

    private void ProcessDirectRequest(HttpListenerContext ctx, string expectedSessionId)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // Allow CORS so dashboard on rmm.voyagermarine.online can fetch directly P2P
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "*");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                return;
            }

            var token = req.QueryString["token"];
            if (string.IsNullOrEmpty(token) || token != expectedSessionId)
            {
                res.StatusCode = 403;
                byte[] unauth = Encoding.UTF8.GetBytes("{\"error\":\"Invalid session token\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(unauth, 0, unauth.Length);
                res.Close();
                return;
            }

            _lastDirectRequestTicks = DateTime.UtcNow.Ticks;

            string path = req.Url.AbsolutePath.ToLowerInvariant();
            if (path == "/ping")
            {
                res.StatusCode = 200;
                res.ContentType = "application/json";
                byte[] ping = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"mode\":\"p2p_direct\"}");
                res.OutputStream.Write(ping, 0, ping.Length);
                res.Close();
                return;
            }

            if (path == "/stream" || path == "/frame")
            {
                var bytes = _latestJpegBytes;
                if (bytes != null && bytes.Length > 0)
                {
                    res.StatusCode = 200;
                    res.ContentType = "image/jpeg";
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    res.StatusCode = 204;
                }
                res.Close();
                return;
            }

            if (path == "/input" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                string body = reader.ReadToEnd();
                if (!string.IsNullOrEmpty(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    ProcessInputEvent(doc.RootElement);
                }
                res.StatusCode = 200;
                byte[] ok = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(ok, 0, ok.Length);
                res.Close();
                return;
            }

            res.StatusCode = 404;
            res.Close();
        }
        catch { }
    }

    private string GetP2PHandleJson(string sessionId)
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ipStr = addr.Address.ToString();
                        if (!ipStr.StartsWith("127.")) ips.Add(ipStr);
                    }
                }
            }
        }
        catch { }

        var endpoints = ips.Select(ip => $"http://{ip}:{P2P_PORT}").ToList();
        endpoints.Add($"http://127.0.0.1:{P2P_PORT}");

        return JsonSerializer.Serialize(new
        {
            type = "p2p_direct_handle",
            sessionId = sessionId,
            port = P2P_PORT,
            ips = ips,
            endpoints = endpoints
        });
    }

    private void ProcessInputEvent(JsonElement ev)
    {
        try
        {
            string type = ev.GetProperty("type").GetString();
            double normX = ev.TryGetProperty("x", out var xProp) ? xProp.GetDouble() : 0;
            double normY = ev.TryGetProperty("y", out var yProp) ? yProp.GetDouble() : 0;
            int button = ev.TryGetProperty("button", out var bProp) ? bProp.GetInt32() : 0;
            string key = ev.TryGetProperty("key", out var kProp) ? kProp.GetString() : null;

            int pixelX = (int)Math.Round(normX * _screenWidth);
            int pixelY = (int)Math.Round(normY * _screenHeight);

            switch (type)
            {
                case "mousemove":
                    SetCursorPos(pixelX, pixelY);
                    break;

                case "mousedown":
                    SetCursorPos(pixelX, pixelY);
                    if (button == 0) mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    else if (button == 2) mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                    else if (button == 1) mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
                    break;

                case "mouseup":
                    SetCursorPos(pixelX, pixelY);
                    if (button == 0) mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    else if (button == 2) mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                    else if (button == 1) mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                    break;

                case "click":
                    SetCursorPos(pixelX, pixelY);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    break;

                case "keydown":
                case "keyup":
                    if (!string.IsNullOrEmpty(key))
                    {
                        byte vk = KeyToVk(key);
                        if (vk != 0)
                        {
                            uint flags = (type == "keyup") ? KEYEVENTF_KEYUP : 0;
                            keybd_event(vk, 0, flags, UIntPtr.Zero);
                        }
                    }
                    break;
            }
        }
        catch { }
    }

    private static byte KeyToVk(string key)
    {
        return key.ToUpperInvariant() switch
        {
            "ENTER" => 0x0D,
            "BACKSPACE" => 0x08,
            "TAB" => 0x09,
            "ESCAPE" => 0x1B,
            "SPACE" or " " => 0x20,
            "ARROWLEFT" or "LEFT" => 0x25,
            "ARROWUP" or "UP" => 0x26,
            "ARROWRIGHT" or "RIGHT" => 0x27,
            "ARROWDOWN" or "DOWN" => 0x28,
            "DELETE" => 0x2E,
            _ => (key.Length == 1 && char.IsLetterOrDigit(key[0])) ? (byte)char.ToUpperInvariant(key[0]) : (byte)0
        };
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageDecoders().FirstOrDefault(codec => codec.FormatID == format.Guid);
    }

    private static string GenerateStatusFrame(string statusMessage)
    {
        using var bmp = new Bitmap(1280, 720);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(20, 24, 33));
            using var font = new Font(FontFamily.GenericSansSerif, 20, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.DrawString("Remote Endpoint Screen", font, Brushes.White, new PointF(40, 40));
            g.DrawString(statusMessage, font, brush, new PointF(40, 90));
            g.DrawString(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC", font, brush, new PointF(40, 140));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
