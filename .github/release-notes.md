## PingBoost 0.2.0

PingBoost Network Lab: a Windows tool for diagnosing and tuning a mobile (4G/5G) game connection.

**New in 0.2.0**
- Network monitor tab: a Task Manager-style list of every TCP connection and UDP flow with process, server name, download/upload speed, totals and TCP retransmits. Sort by any column, filter, group by process.
- A single self-contained `PingBoost.exe` instead of a folder of libraries.
- Settings and the restore backup are stored next to the executable (portable); files from 0.1.0 are copied over on first start.

**Included**
- Latency, jitter and loss tests, route (traceroute) check, idle and keepalive experiments, before/after comparison, ready-made public DNS targets.
- Optimization tab: adapter and TCP tweaks, MTU probe, QoS marking, upload limits for background programs, game mode. Every change is backed up; "Restore previous" and "Reset to Windows defaults" undo it.
- UDP monitor: per-endpoint UDP statistics for the game with receive gaps, jitter and local loss counters.
- Minimize to tray, Russian/English interface (follows Windows by default).

**Requirements**
- Windows 10/11 x64. The build is self-contained, no .NET installation is needed.
- Run as administrator for the Optimization and UDP monitor tabs and for the speed columns of the Network monitor tab.

**Notes**
- This is an early release. Some features (UDP monitor, network monitor speeds, game mode, QoS marking) have not been verified on a wide range of hardware. Apply one tweak at a time and keep the restore buttons in mind.
- Results depend on the carrier and the radio link and are not guaranteed.

Download `PingBoost-0.2.0-win-x64.zip`, unpack it and run `PingBoost.exe`.
