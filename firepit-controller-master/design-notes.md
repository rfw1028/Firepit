

Assessed runtime installation and strategized framework-dependent deployment approach
Assessed runtime installation and strategized framework-dependent deployment approach
Runtime's in and reporting linux-arm64 — that's what you needed. "No SDKs were found" is correct; you asked for --runtime aspnetcore, so builds happen on the dev PC.

Two things to fix now that it's user-local at ~/.dotnet.

DOTNET_ROOT isn't set (note the Environment variables: Not set line). Interactive shells find dotnet via PATH, but a systemd service won't. Either add it to the unit, or move the install somewhere system-wide — I'd do the latter since a service reading its runtime out of a home directory is asking for trouble later:

bash
sudo mv ~/.dotnet /usr/share/dotnet
sudo ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet
sed -i '/\.dotnet/d' ~/.bashrc
dotnet --info
Publish framework-dependent now, since the runtime is present — output drops from ~90 MB to a few MB, which matters for SD card wear and copy time. On the dev PC:

dotnet publish -c Release -r linux-arm64 --self-contained false -o publish
Copy to /opt/firepit, then the unit changes slightly for framework-dependent — you invoke dotnet with the DLL rather than the apphost:

ini
[Service]
WorkingDirectory=/opt/firepit
ExecStart=/usr/local/bin/dotnet /opt/firepit/FirepitController.dll
Environment=DOTNET_ROOT=/usr/share/dotnet
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Restart=always
RestartSec=10
User=pi
SupplementaryGroups=gpio spi i2c dialout
ASPNETCORE_URLS in the unit is cleaner than hardcoding UseUrls — keeps the binding out of your source so debug runs on the dev PC stay on localhost.

One gotcha with framework-dependent: your app's target framework has to be ≤ 10.0.10. If you build against a newer patch than what's on the Pi, it fails at startup with a rollforward error. Worth pinning a global.json or just re-running the install script when you bump the dev PC.

pi@firepit:~ $ sudo mv ~/.dotnet /usr/share/dotnet
sudo ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet
sed -i '/\.dotnet/d' ~/.bashrc
dotnet --info
bash: /home/pi/.dotnet/dotnet: No such file or directory


Diagnosed shell cache issue and verified dotnet relocation
Diagnosed shell cache issue and verified dotnet relocation
That's just bash's command hash — it cached dotnet at the old path when you ran it earlier, and it's still trying there even though the file moved. Nothing actually broke.

bash
hash -r
dotnet --info
hash -r clears the cache, and the lookup then finds /usr/local/bin/dotnet via the symlink. (Opening a fresh shell would do the same thing.)

Your current shell's PATH still has ~/.dotnet on it — harmless, and it'll be gone next login since you stripped it from .bashrc.

Quick confirmation the move landed cleanly:

bash
ls -l /usr/local/bin/dotnet
dotnet --list-runtimes
You should see the symlink pointing at /usr/share/dotnet/dotnet and both runtimes now reporting under /usr/share/dotnet/shared/.

pi@firepit:~ $ dotnet --info
Host:
  Version:      10.0.10
  Architecture: arm64
  Commit:       f7d90799ce
  RID:          linux-arm64
.NET SDKs installed:
  No SDKs were found.
.NET runtimes installed:
  Microsoft.AspNetCore.App 10.0.10 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
  Microsoft.NETCore.App 10.0.10 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
Other architectures found:
  None
Environment variables:
  Not set
global.json file:
  Not found
Learn more:
  https://aka.ms/dotnet/info
Download .NET:
  https://aka.ms/dotnet/download

Show more

Orchestrated deployment pipeline with verification testing
Orchestrated deployment pipeline with verification testing
Runtimes are under /usr/share/dotnet now — done with the Pi side. (Environment variables: Not set is fine here; the muxer resolves the symlink to find its own root, so DOTNET_ROOT isn't strictly needed. Leave it in the unit anyway as insurance.)

Prep the deploy target:

bash
sudo mkdir -p /opt/firepit
sudo chown pi:pi /opt/firepit
On the dev PC:

dotnet new webapp -o FirepitController
cd FirepitController
dotnet publish -c Release -r linux-arm64 --self-contained false -o publish
scp -r publish/* pi@firepit:/opt/firepit/
Then on the Pi, run it in the foreground first — you want startup errors on your screen, not buried in journalctl while you're also debugging a unit file:

bash
cd /opt/firepit
ASPNETCORE_URLS=http://0.0.0.0:5000 dotnet FirepitController.dll
Hit http://firepit:5000 from the dev PC. If the default Razor page renders, Ctrl-C and install the unit:

ini
[Unit]
Description=Firepit Controller
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/firepit
ExecStart=/usr/local/bin/dotnet /opt/firepit/FirepitController.dll
Environment=DOTNET_ROOT=/usr/share/dotnet
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always
RestartSec=10
User=pi
SupplementaryGroups=gpio spi i2c dialout
KillSignal=SIGINT

[Install]
WantedBy=multi-user.target
bash
sudo systemctl daemon-reload
sudo systemctl enable --now firepit
journalctl -fu firepit
KillSignal=SIGINT matters more than it looks — it lets Kestrel shut down gracefully, which is how you get a chance to drive the valve closed and de-energize the pilot relay in a IHostApplicationLifetime.ApplicationStopping handler instead of having the process shot dead mid-cycle.






Claude is AI and can make mistakes. Please double-check responses.
