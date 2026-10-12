using Microsoft.Extensions.Options;

namespace FirepitController.Hardware;

/// <summary>
/// Writes a monotonically-updated heartbeat file that the independent external watchdog
/// polls. If this stops (process death, thread-pool starvation, or a deliberate
/// <see cref="HealthState.MarkUnhealthy"/>), the watchdog cuts the CH3 relay (gas off)
/// and reboots. This is the app side of the software dead-man.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private readonly HeartbeatOptions _opts;
    private readonly HealthState _health;
    private readonly ILogger<HeartbeatService> _log;

    public HeartbeatService(IOptions<HeartbeatOptions> opts, HealthState health, ILogger<HeartbeatService> log)
    {
        _opts = opts.Value;
        _health = health;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled || !OperatingSystem.IsLinux())
        {
            _log.LogInformation("Heartbeat disabled (Enabled={Enabled}, Linux={Linux}).",
                _opts.Enabled, OperatingSystem.IsLinux());
            return;
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_opts.Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create heartbeat directory for {Path}.", _opts.Path);
        }

        _log.LogInformation("Heartbeat → {Path} every {Ms} ms (external-watchdog dead-man).",
            _opts.Path, _opts.IntervalMs);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opts.IntervalMs));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            // While unhealthy, deliberately stop refreshing → watchdog trips.
            if (!_health.Healthy) continue;
            try
            {
                await File.WriteAllTextAsync(
                    _opts.Path,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                    stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to write heartbeat to {Path}.", _opts.Path);
            }
        }
    }
}
