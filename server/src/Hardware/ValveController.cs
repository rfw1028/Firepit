using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FirepitController.Hardware;

/// <summary>Snapshot of the ball-valve state from the shim.</summary>
public sealed record ValveStatus(bool Online, int Position, string? Mode, string? Error = null);

/// <summary>
/// Talks to the Python valve shim (Tuya protocol 3.5) over localhost HTTP.
/// The valve meters gas flow (0–100%); the relay shutoff is the safety cutoff.
/// </summary>
public sealed class ValveController
{
    private readonly HttpClient _http;
    private readonly ValveOptions _opts;
    private readonly ILogger<ValveController> _log;

    public ValveController(HttpClient http, IOptions<ValveOptions> opts, ILogger<ValveController> log)
    {
        _opts = opts.Value;
        _http = http;
        _http.BaseAddress = new Uri(_opts.ShimUrl);
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        _log = log;
    }

    public bool Enabled => _opts.Enabled;

    private int _gen;                 // bumped on every open/close request; stale completions are ignored
    private volatile bool _fullyOpen;

    /// <summary>True once the valve has CONFIRMED 100% for the current open request
    /// (always true when the valve is disabled, so ignition is never blocked by a missing valve).</summary>
    public bool FullyOpen => !_opts.Enabled || _fullyOpen;

    public async Task<ValveStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (!_opts.Enabled) return new ValveStatus(false, 0, null, "valve disabled");
        try
        {
            var s = await _http.GetFromJsonAsync<ValveStatus>("/status", ct);
            return s ?? new ValveStatus(false, 0, null, "no response");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Valve status query failed.");
            return new ValveStatus(false, 0, null, ex.Message);
        }
    }

    /// <summary>Set valve position 0–100% (clamped). Returns the resulting status.</summary>
    public async Task<ValveStatus> SetPositionAsync(int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (!_opts.Enabled) return new ValveStatus(false, percent, null, "valve disabled");
        // The valve is a WiFi device and can drop a request; retry once if the shim call
        // itself fails (timeout/connection). A shim reply (even "not confirmed") is final —
        // the shim already retries against the valve.
        var s = await TrySetOnceAsync(percent, ct);
        if (s is null && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);
            s = await TrySetOnceAsync(percent, ct);
        }
        return s ?? new ValveStatus(false, percent, null, "valve shim did not respond");
    }

    private async Task<ValveStatus?> TrySetOnceAsync(int percent, CancellationToken ct)
    {
        try
        {
            // Use StringContent (not PostAsJsonAsync) so the request carries a
            // Content-Length header — the stdlib shim reads Content-Length, and
            // JsonContent's chunked encoding would arrive as an empty body.
            var json = JsonSerializer.Serialize(new { position = percent });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync("/position", content, ct);
            resp.EnsureSuccessStatusCode();
            var s = await resp.Content.ReadFromJsonAsync<ValveStatus>(cancellationToken: ct);
            return s ?? new ValveStatus(false, percent, null, "no response");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Valve set position {Percent}% failed.", percent);
            return null;
        }
    }

    /// <summary>
    /// Best-effort drive valve to 0% on any gas-off event. Fire-and-forget so it never
    /// blocks or fails the relay safety path (the relays are what actually stop gas).
    /// </summary>
    public void RequestClose(string reason)
    {
        Interlocked.Increment(ref _gen);
        _fullyOpen = false;
        if (!_opts.Enabled) return;
        _ = Task.Run(async () =>
        {
            _log.LogInformation("Requesting valve → 0% ({Reason}).", reason);
            var s = await SetPositionAsync(0);
            if (!s.Online) _log.LogWarning("Valve close ({Reason}) did not confirm: {Error}", reason, s.Error);
        });
    }

    /// <summary>
    /// Make sure the valve is at 100%, then call <paramref name="onOpen"/>. Fire-and-forget. If the valve
    /// already reports 100% no move is sent. <paramref name="onOpen"/> runs only if the valve CONFIRMS 100%
    /// and no close/new request happened meanwhile; otherwise <paramref name="onFail"/> runs (when not superseded).
    /// If superseded while moving, the valve is sent back to 0%.
    /// </summary>
    public void RequestFullOpen(string reason, Action onOpen, Action<string> onFail)
    {
        int gen = Interlocked.Increment(ref _gen);
        _fullyOpen = false;
        if (!_opts.Enabled) { _fullyOpen = true; onOpen(); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var cur = await GetStatusAsync();
                ValveStatus s = cur;
                if (!(cur.Online && cur.Position >= 100))
                {
                    _log.LogInformation("Requesting valve -> 100% ({Reason}).", reason);
                    s = await SetPositionAsync(100);
                }
                if (gen != Volatile.Read(ref _gen))
                {
                    await SetPositionAsync(0);   // superseded (gas closed) while moving
                    return;
                }
                if (s.Online && s.Position >= 100)
                {
                    _fullyOpen = true;
                    onOpen();
                }
                else
                {
                    _log.LogWarning("Valve full-open ({Reason}) did not confirm: {Error}", reason, s.Error);
                    onFail(s.Error ?? "valve did not confirm 100%");
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Valve full-open failed.");
                if (gen == Volatile.Read(ref _gen)) onFail(ex.Message);
            }
        });
    }
}
