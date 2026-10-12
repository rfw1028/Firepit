namespace FirepitController.Hardware;

/// <summary>Config for the camera flame detector client and auto-ignition ("Flame" section).</summary>
public sealed class FlameOptions
{
    public double ValveWaitSeconds { get; set; } = 60;   // max wait for the valve to confirm 100% before closing gas

    /// <summary>Master switch for polling the detector (and therefore auto-ignition).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The firepit-flame service's HTTP endpoint (localhost on the Pi).</summary>
    public string Url { get; set; } = "http://127.0.0.1:5007/flame";

    /// <summary>How often to poll the detector normally (1 s while sparking).</summary>
    public double PollSeconds { get; set; } = 2;

    public double TimeoutSeconds { get; set; } = 1.5;

    /// <summary>A detector reading older than this (or a dead camera) counts as "no flame".</summary>
    public double MaxAgeSeconds { get; set; } = 5;

    /// <summary>Initial value of the auto-ignite switch; the in-app switch (persisted in the user's data folder) overrides it.</summary>
    public bool AutoIgnite { get; set; } = true;

    /// <summary>
    /// Max continuous time to spark with gas on and no flame before the controller
    /// stops the spark, closes the gas and locks out (until Open gas is pressed again).
    /// Must cover pilot lighting + thermocouple warm-up before the valve passes gas.
    /// </summary>
    public double TrialSeconds { get; set; } = 30;
}
