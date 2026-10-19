# PingBoost Network Lab

A Windows application for diagnosing network quality. The interface uses a clean, light visual style inspired by macOS utilities and is built with WPF for Windows.

## Prototype features

- Lists active network adapters, gateways, and reported link speeds.
- Sequentially checks up to eight user-provided IP addresses or DNS names with ICMP.
- Shows the latest and average ping, mean absolute jitter between adjacent successful probes, and the share of probes with no response.
- Charts latency history and breaks the line where probes time out.
- Runs an ICMP traceroute with one probe per TTL step, up to 24 hops.
- Compares ping immediately after network activity with ping after a selected idle period (5, 15, 30, or 60 seconds; five cycles).
- Runs a paired A/B experiment with and without HTTPS keepalive at the same idle interval, reporting average and median RTT changes.
- **Optimization tab** (requires administrator rights; no driver is installed, everything uses documented PowerShell cmdlets and registry values):
  - adapter tweaks: disable adapter power saving, Interrupt Moderation, Energy-Efficient/Green Ethernet, Large Send Offload and Receive Segment Coalescing, lowest Wi-Fi roaming aggressiveness (only settings the driver actually exposes are touched);
  - TCP Optimizer-style system tweaks: TCP timestamps, global RSC, per-adapter `TcpAckFrequency`/`TCPNoDelay`, MMCSS network throttling and Games task priority;
  - MTU probe (largest IPv4 packet that passes without fragmentation) with an optional apply step;
  - upload limit for background programs (Windows QoS throttle policy) to keep the uplink queue short and reduce bufferbloat;
  - Windows QoS policy that marks the selected game's outgoing UDP traffic with a DSCP value;
  - game mode: High process priority, High performance power plan and temporary pause of Windows Update/BITS/Delivery Optimization;
  - **restore buttons**: "Restore state from before PingBoost" (uses a backup saved before the first change) and "Reset to Windows defaults" (sets documented defaults for every managed setting, even without a backup).
- **UDP monitor tab** (administrator rights): passive per-remote-endpoint UDP statistics for the game process from the Windows kernel ETW network provider - packet counts, receive rate, receive jitter and the longest gap between received packets over 10 seconds. It also shows system-wide local loss counters (UDP datagrams dropped by Windows, adapter discards and errors) to tell loss on this PC from loss outside it. Only addresses, ports, sizes and timing are read.
- Keepalive interval choices from 2 to 60 seconds.
- Stores the Russian/English UI preference, the current-user Windows startup setting, the last game process name and the test target list (ready-made public DNS servers plus your own addresses) as JSON under the user's LocalAppData folder.
- Provides an opt-in HTTPS keepalive with a configurable interval and request/response-body counters.
- Opens the official Waveform Bufferbloat Test after a separate confirmation. Waveform displays idle/download/upload latency, jitter, and its A+–F grade.
- Saves a baseline snapshot, lets you rerun measurements after changing one setting, and compares only the new samples.
- Exports probe history to CSV using the standard Windows save dialog.

## Limitations and safety

- Measurements never change the system. The Optimization tab changes settings only after an explicit confirmation, stores the original values in `%LOCALAPPDATA%\PingBoost\tuning-backup.json`, and does not promise a lower ping.
- Without a relay server PingBoost cannot reroute or duplicate game packets (what ExitLag/GearUP do); it only tunes the local part of the path: adapter, Windows network stack, Wi-Fi and cellular behavior.
- Some adapter settings restart the adapter and drop the connection for a few seconds. DSCP marks may be cleared by a mobile carrier or a phone hotspot. Adapter keywords differ between drivers; unsupported ones are skipped.
- The UDP monitor attributes packets to a process through the UDP owner table (local port -> PID). It does not prove which packets the game lost; a long receive gap shows that nothing arrived from the server for that time.
- Measurements use the Windows system route. Adapter details are informational; probes are not currently bound to a specific adapter.
- ICMP may be filtered by a server or network and does not always reflect game UDP latency. A traceroute hop may not reply even while forwarding traffic.
- Reported “loss” is the share of ICMP probes with no reply, not confirmed loss of game packets.
- The game process name is used by the Optimization and UDP monitor tabs. Startup registration applies to the current Windows user.
- The idle test measures RTT changes after a pause but cannot determine the LTE/5G radio state by itself. The sequential A/B comparison is short and can be affected by changing cellular conditions.
- Keepalive sends HTTPS GET requests to the user-selected endpoint (default: `https://www.google.com/generate_204`). It may not keep a particular operator's radio connection active. The response-body counter excludes TLS, IP, and radio-protocol overhead. Stop keepalive when the experiment is complete.
- The bufferbloat test runs on the official Waveform site in a browser, not inside PingBoost. It may nearly saturate the link, temporarily slow other devices, and use mobile data; the app always asks for confirmation first.
- The “before” snapshot is held in memory and is lost when the app closes. CSV exports individual probes, not an automatically generated comparison report.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK

## Build and run

```powershell
dotnet build .\PingBoost.csproj
dotnet run --project .\PingBoost.csproj
```

Restoring NuGet packages requires internet access once (`Microsoft.Diagnostics.Tracing.TraceEvent` for the UDP monitor). Run the app as administrator to use the Optimization and UDP monitor tabs; the app offers a restart button.

The latest changes were compile-checked against the WPF reference assemblies on Linux; they have not yet been run on Windows, so test them on a non-critical session first and keep the restore buttons in mind.

## Next steps

1. Test the Optimization tab on the target machine, one tweak at a time, comparing results with the before/after tools; verify both restore buttons.
2. Verify the UDP monitor against a real game session and compare receive gaps with felt lag.
3. Add comparison-report export.
4. Consider a private relay only if measurements show that changing routes may help.
