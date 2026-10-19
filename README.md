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
- Records a gameplay session with sequential ICMP probes to the local gateway and an external host, manual gameplay/loss markers, and a separate CSV export.
- Adds session snapshots for system CPU/memory and Windows network-interface error/discard counters, plus automatic event markers.
- Can list IPv4 UDP local endpoints owned by a selected game process; it does not inspect payloads or identify remote UDP peers.
- Captures repeated traceroute snapshots and compares responding hops to show route changes.
- Stores Russian/English UI preference, current-user Windows startup setting, and per-game diagnostic profiles as JSON under the user's LocalAppData folder.
- Provides an opt-in HTTPS keepalive with a configurable interval and request/response-body counters.
- Opens the official Waveform Bufferbloat Test after a separate confirmation. Waveform displays idle/download/upload latency, jitter, and its A+–F grade.
- Saves a baseline snapshot, lets you rerun measurements after changing one setting, and compares only the new samples.
- Exports probe history to CSV using the standard Windows save dialog.
- Displays cautious recommendations based on observed latency, jitter, and missing ICMP replies.

## Limitations and safety

- This is a diagnostic tool. It does not promise to reduce ping and does not change routes, DNS, application priorities, or adapter settings.
- Measurements use the Windows system route. Adapter details are informational; probes are not currently bound to a specific adapter.
- ICMP may be filtered by a server or network and does not always reflect game UDP latency. A traceroute hop may not reply even while forwarding traffic.
- Reported “loss” is the share of ICMP probes with no reply, not confirmed loss of game packets.
- Gameplay session markers are manually timestamped and ICMP probes do not capture or identify the game's UDP packets. A missing gateway reply and a missing external reply can help narrow down where to investigate, but are not definitive proof of in-game packet loss.
- Process UDP inspection uses the Windows IPv4 owner-PID table and reports local endpoints only. UDP does not have a connection table containing the remote peer; IPv6 endpoints and exact game-server identification are not currently included.
- Route snapshots use ICMP traceroute and are manual. A snapshot can take up to about 22 seconds, during which the session's regular probes pause.
- System snapshots use built-in Windows CPU, memory, and adapter counters. Adapter error/discard counters are cumulative and may not indicate game-specific loss.
- Per-game profiles store diagnostic parameters only. They do not alter the game's routes or traffic. Startup registration applies to the current Windows user.
- The idle test measures RTT changes after a pause but cannot determine the LTE/5G radio state by itself. The sequential A/B comparison is short and can be affected by changing cellular conditions.
- Keepalive sends HTTPS GET requests to the user-selected endpoint (default: `https://www.google.com/generate_204`). It may not keep a particular operator's radio connection active. The response-body counter excludes TLS, IP, and radio-protocol overhead. Stop keepalive when the experiment is complete.
- The bufferbloat test runs on the official Waveform site in a browser, not inside PingBoost. It may nearly saturate the link, temporarily slow other devices, and use mobile data; the app always asks for confirmation first.
- The “before” snapshot is held in memory and is lost when the app closes. CSV exports individual probes, not an automatically generated comparison report.
- Recommendations are heuristic guidance, not automatic optimization.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK

## Build and run

```powershell
dotnet build .\PingBoost.csproj
dotnet run --project .\PingBoost.csproj
```

The project was verified with .NET SDK 8.0.426: build completed with no warnings or errors.

## Next steps

1. Manually test multiple targets, stopping, timeouts, traceroute, baseline comparison, and CSV export.
2. Improve saving/loading test profiles and add comparison-report export.
3. Design a bufferbloat measurement separately. Any traffic-generating test should be opt-in, clearly warned, and bounded.
4. Consider a private relay only if measurements show that changing routes may help.
