namespace FirepitController.Hardware;

/// <summary>
/// In-app half of the igniter dead-man: proactively releases Relay A if hold pings go
/// stale, rather than waiting for the next ping that will never come. This is a
/// convenience/UX layer — the safety-critical cutoff is Relay B, owned by the fully
/// independent firepit-igniter-watchdog process, which still cuts power even if this
/// app (and this service along with it) hangs.
/// </summary>
public sealed class IgniterHoldTimeoutService(IgniterController igniter, ILogger<IgniterHoldTimeoutService> log)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (igniter.IsStale(igniter.HoldTimeout))
            {
                log.LogInformation("Igniter hold pings stale past {Timeout} — releasing Relay A.", igniter.HoldTimeout);
                igniter.Release();
            }
        }
    }
}
