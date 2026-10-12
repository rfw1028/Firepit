using FirepitController.Hardware;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Integrate with systemd: journald logging, Type=notify lifetime (READY/STOPPING).
builder.Host.UseSystemd();

builder.Services.AddRazorPages();

// ---- Gas relay hardware ----
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relays"));

// Real GPIO on Linux (the Pi); no-op simulation everywhere else or when forced.
builder.Services.AddSingleton<IRelayArray>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<RelayOptions>>().Value;
    // Canonical default when unconfigured; dedupe defensively so a stray duplicate
    // pin can never cause a double open/close.
    var pins = (opts.BcmPins is { Length: > 0 } configured ? configured : [26, 20, 21])
        .Distinct().ToArray();
    bool useRealGpio = OperatingSystem.IsLinux() && !opts.ForceSimulation;
    return useRealGpio
        ? new GpioRelayArray(pins, sp.GetRequiredService<ILogger<GpioRelayArray>>())
        : new SimulatedRelayArray(pins.Length, sp.GetRequiredService<ILogger<SimulatedRelayArray>>());
});
// ---- Tuya ball valve (via local Python shim) ----
builder.Services.Configure<ValveOptions>(builder.Configuration.GetSection("Valve"));
builder.Services.AddHttpClient<ValveController>();

builder.Services.AddSingleton<GasValveController>();

// ---- Igniter relay (Eaton continuous-ignition module, manual dead-man) ----
// Relay A only (BCM 5) — Relay B (BCM 6) belongs exclusively to the independent
// firepit-igniter-watchdog service, same split as CH1/CH2 (app) + CH3 (watchdog).
builder.Services.Configure<IgniterOptions>(builder.Configuration.GetSection("Igniter"));
builder.Services.AddKeyedSingleton<IRelayArray>("igniter", (sp, _) =>
{
    var opts = sp.GetRequiredService<IOptions<IgniterOptions>>().Value;
    var pins = (opts.BcmPins is { Length: > 0 } configured ? configured : [5]).Distinct().ToArray();
    bool useRealGpio = OperatingSystem.IsLinux() && !opts.ForceSimulation;
    return useRealGpio
        ? new GpioRelayArray(pins, sp.GetRequiredService<ILogger<GpioRelayArray>>())
        : new SimulatedRelayArray(pins.Length, sp.GetRequiredService<ILogger<SimulatedRelayArray>>());
});
builder.Services.Configure<IgniterHeartbeatOptions>(builder.Configuration.GetSection("IgniterHeartbeat"));
builder.Services.AddSingleton<IgniterController>();
builder.Services.AddHostedService<IgniterHoldTimeoutService>();

// ---- Camera flame detector client + auto-ignition (advisory detector, bounded trial) ----
builder.Services.Configure<FlameOptions>(builder.Configuration.GetSection("Flame"));
builder.Services.AddHttpClient("flame");
builder.Services.AddSingleton<FlameMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FlameMonitor>());

// systemd watchdog keep-alive (inert if not running under WatchdogSec).
builder.Services.AddHostedService<SystemdWatchdogPinger>();

// ---- External-watchdog dead-man heartbeat ----
// The main app owns only CH1/CH2 (BCM 26, 20). The independent firepit-watchdog
// service owns CH3 (BCM 21) and drops it (gas off) + reboots if this heartbeat goes stale.
builder.Services.Configure<HeartbeatOptions>(builder.Configuration.GetSection("Heartbeat"));
builder.Services.AddSingleton<HealthState>();
builder.Services.AddHostedService<HeartbeatService>();

var app = builder.Build();

// ---- Force gas OFF the instant the app is up, and again as it shuts down. ----
var gas = app.Services.GetRequiredService<GasValveController>();
gas.ForceSafe();
var igniter = app.Services.GetRequiredService<IgniterController>();
igniter.ForceSafe();
// Startup: drive the ball valve to 0% (known safe baseline).
app.Services.GetRequiredService<ValveController>().RequestClose("startup");
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    app.Logger.LogWarning("Application stopping — forcing gas and igniter OFF.");
    gas.ForceSafe();
    igniter.ForceSafe();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

// ---- Minimal gas control API ----
app.MapGet("/api/gas/status", (GasValveController g) => Results.Json(g.GetStatus()));
app.MapPost("/api/gas/open", (GasValveController g) =>
{
    g.OpenGas();
    return Results.Json(g.GetStatus());
});
app.MapPost("/api/gas/close", (GasValveController g) =>
{
    g.CloseGas();
    return Results.Json(g.GetStatus());
});

// ---- Igniter API (manual dead-man; caller must re-hold roughly every 500ms) ----
app.MapGet("/api/igniter/status", (IgniterController i) => Results.Json(i.GetStatus()));
app.MapPost("/api/igniter/hold", async (IgniterController i) =>
{
    await i.HoldAsync();
    return Results.Json(i.GetStatus());
});
app.MapPost("/api/igniter/release", (IgniterController i) =>
{
    i.Release();
    return Results.Json(i.GetStatus());
});

// ---- Flame detector status (read-only) and the auto-ignite on/off switch (the only setting here) ----
app.MapGet("/api/flame", (FlameMonitor f) => Results.Json(f.GetStatus()));
app.MapPost("/api/flame/auto-ignite", (FlameMonitor f, AutoIgniteRequest req) =>
{
    f.SetAutoIgnite(req.Enabled);
    return Results.Json(f.GetStatus());
});

// ---- Ball-valve API ----
app.MapGet("/api/valve/status", async (ValveController v) => Results.Json(await v.GetStatusAsync()));
app.MapPost("/api/valve/position", async (ValveController v, ValvePositionRequest req) =>
{
    if (req.Position is < 0 or > 100)
        return Results.BadRequest(new { error = "position must be 0–100" });
    return Results.Json(await v.SetPositionAsync(req.Position));
});

app.Run();

record ValvePositionRequest(int Position);
record AutoIgniteRequest(bool Enabled);
