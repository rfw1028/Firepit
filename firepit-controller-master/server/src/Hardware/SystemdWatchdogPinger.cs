using System.Net.Sockets;
using System.Text;

namespace FirepitController.Hardware;

/// <summary>
/// Sends periodic <c>WATCHDOG=1</c> keep-alive datagrams to systemd's notify socket.
/// If the process hangs and stops pinging, systemd (WatchdogSec=) kills and restarts
/// the unit — and the restarted app force-safes the relays on startup.
///
/// Inert when not launched under systemd with WatchdogSec (NOTIFY_SOCKET / WATCHDOG_USEC
/// unset), so it is harmless during local/dev runs.
///
/// TODO(monitor): once the relay/valve-power monitor exists, gate each ping on a fresh
/// health heartbeat so a deadlocked control loop (thread still alive) also trips the watchdog.
/// </summary>
public sealed class SystemdWatchdogPinger : BackgroundService
{
    private readonly ILogger<SystemdWatchdogPinger> _log;

    public SystemdWatchdogPinger(ILogger<SystemdWatchdogPinger> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        var watchdogUsecRaw = Environment.GetEnvironmentVariable("WATCHDOG_USEC");

        if (string.IsNullOrEmpty(notifySocket) || !long.TryParse(watchdogUsecRaw, out var watchdogUsec) || watchdogUsec <= 0)
        {
            _log.LogInformation("systemd watchdog not configured (NOTIFY_SOCKET/WATCHDOG_USEC unset) — pinger idle.");
            return;
        }

        // Ping at half the watchdog interval, per systemd guidance.
        var interval = TimeSpan.FromMilliseconds(watchdogUsec / 1000.0 / 2.0);
        _log.LogInformation("systemd watchdog active: interval {Interval} ms.", interval.TotalMilliseconds);

        var endpoint = CreateEndpoint(notifySocket);
        var payload = Encoding.ASCII.GetBytes("WATCHDOG=1");

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var sock = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                await sock.SendToAsync(payload, SocketFlags.None, endpoint, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to send systemd watchdog ping.");
            }
        }
    }

    private static UnixDomainSocketEndPoint CreateEndpoint(string notifySocket)
    {
        // systemd uses an abstract socket when the path starts with '@'.
        var path = notifySocket.StartsWith('@') ? "\0" + notifySocket[1..] : notifySocket;
        return new UnixDomainSocketEndPoint(path);
    }
}
