# WarpDrop: development handoff

This is a snapshot of where the project stands, what has been built and measured, and what's open. Read it before continuing in another tool (for example Codex). Last updated 2026-10-21.

---

## 1. What WarpDrop is

A Windows desktop app (WPF, .NET 8) that sends huge files between a Windows PC and an iPhone at the highest speed the hardware allows:

- The PC creates its own Wi-Fi network: Windows Mobile Hotspot with a dedicated radio.
- The iPhone joins by scanning QR code 1, then opens the transfer page in Safari by scanning QR code 2.
- Files move both ways over HTTP. There's no app on the phone.

The owner's priorities, in order:
1. **Maximum speed, and consistent speed.** No drops mid-transfer.
2. **One ON/OFF switch.** OFF must leave the laptop's normal Wi-Fi, internet and hotspot completely untouched. ON may change anything for speed, but OFF must restore everything.
3. Fully wireless is preferred.

## 2. Current state

| Item | State |
|---|---|
| Repo | https://github.com/AkshayLanghani/warpdrop (public, MIT) |
| Branch `main` on GitHub | `9d66d3b`: 55 commits dated Oct 7–20 |
| Local `main` | **1 commit ahead, not pushed**: `8ded2b8` "Host on 6 GHz when the Wi-Fi driver supports it" |
| Release | `v1.0.0` with `WarpDrop.exe` attached (points at `9d66d3b`) |
| Local backup branches | `backup-before-history`, `backup-before-strip` (old histories; local only, never pushed) |
| Built exe | `dist\WarpDrop.exe` (self-contained single file, ~75 MB). Desktop shortcut `C:\Users\aksha\OneDrive\Desktop\WarpDrop.lnk` points to it |
| CI | `.github/workflows/build.yml` builds on push; on `v*` tags it attaches the exe to the release |

### Owner preferences (must follow)
- **No AI attribution in commits or PRs.** No `Co-Authored-By: Claude …` line, and no "Generated with …" line.
- Commit author is `AkshayLanghani <akshaylanghani1@gmail.com>` (git config).

## 3. Environment and commands

- **.NET 8 SDK** is installed per-user at `%USERPROFILE%\.dotnet\dotnet.exe` and is **not on PATH**. Call it by full path.
- Target framework: `net8.0-windows10.0.26100.0`. The 24H2 SDK is needed for the WPA3 hotspot API, which is checked at runtime, so older Windows still works.
- Git for Windows plus Git Credential Manager is signed in to GitHub as `AkshayLanghani`. The `gh` CLI is **not** installed.
- Laptop: HP OmniBook X Flip 16, Core Ultra 7 256V, **Intel Wi-Fi 6E AX211** (driver 24.50.0.4), USB4 ports, no Ethernet port.

```powershell
# Build
& "$env:USERPROFILE\.dotnet\dotnet.exe" build WarpDrop -c Release

# Publish the single-file exe to dist\
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish WarpDrop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

A running `dist\WarpDrop.exe` locks the file. Close it before publishing, but **only if its switch is OFF** (see §8).

### Command-line flags
| Flag | Purpose |
|---|---|
| `--lan` | No switch. Serve on the current network. Used for local testing. |
| `--nofw` / `--notune` | Skip the elevated guardian (no UAC, no tuning, no firewall rule). |
| `--token=xyz` | Fixed access token. Used for scripted tests. |
| `--restore-system` | Emergency undo of all guardian changes. |
| `--probe-bands` | Read-only report of the hotspot bands and security modes the driver supports. |
| paths | Files or folders to share at startup. |

### Runtime files (outside the repo)
- `%APPDATA%\WarpDrop\settings.json`: hotspot SSID, password, receive folder, `PreferSixGhz`.
- `%APPDATA%\WarpDrop\diag.log`: **the diagnostics log**. Read this first when the owner reports a problem.
- `%APPDATA%\WarpDrop\session\`: guardian script, `session.log`, `original.json` (snapshot of original settings while ON).
- Received files go to `%USERPROFILE%\Downloads\WarpDrop`.

## 4. Architecture (file map)

| File | Responsibility |
|---|---|
| `WarpDrop/FastServer.cs` | HTTP/1.1 server on raw sockets. Double-buffered 2 MB reads overlapping sends, 8 MB socket buffers. Range downloads. Resumable uploads: `PUT` at any offset, merged written-range list (`AddRange`), `Contig` gap-free prefix, `409` refusal of incomplete finishes, 60 s body-stall timeout. Speed-test endpoints. `/api/log` for phone-side events. |
| `WarpDrop/Hotspot.cs` | WinRT `NetworkOperatorTetheringManager`. **Band ladder**: 6 GHz/WPA3, then 5 GHz/WPA2, then auto. Captures and restores the user's own hotspot config. `ProbeAsync`. Hotspot IP and link-rate detection, wired-link detection. `WifiStation`: query, disconnect and connect the laptop's own Wi-Fi via `netsh`. |
| `WarpDrop/Tuning.cs` | `SessionGuardian`: an elevated PowerShell script (embedded) that applies tuning on ON, holds, and reverts on OFF or if the app dies. `PerfBoost`: per-session Best-performance power overlay, no-sleep, High priority. |
| `WarpDrop/MainWindow.xaml(.cs)` | UI and the ON/OFF switch flow (`TurnOnAsync`/`TurnOffAsync`), dedicated-radio watchdog, band button, activity list, live speed, diagnostics ticks. |
| `WarpDrop/Support.cs` | `Diag` log, `Fmt`, `Settings`, `Qr` (QRCoder), `TransferRow`. |
| `WarpDrop/web/index.html` | The iPhone page, embedded in the exe: file list, downloads, speed test, **upload engines**. |
| `docs/HOW-IT-WORKS.md` | Architecture, HTTP API table, known limits (public doc). |

### What the switch does
**ON:**
1. The guardian is started with one UAC prompt. It applies the tuning and logs to `session.log`. The tuning:
   - Intel adapter: Throughput Booster on, MIMO power save off (No SMPS), Transmit Power highest, Channel Width 5/6 GHz Auto, 6 GHz radio on, U-APSD off;
   - Wi-Fi power plan: max performance;
   - TCP template `internet`: CUBIC → **BBR2**, minimum retransmit timeout 300 → **60 ms**;
   - `icssvc PeerlessTimeoutEnabled=0`, so the hotspot doesn't auto-off;
   - **Windows Wi-Fi auto-config paused** (`netsh wlan set autoconfig enabled=no`): no auto-join, no background scans;
   - a firewall allow rule for the exe.
2. The laptop's own Wi-Fi is disconnected. This is the **dedicated radio**.
3. The hotspot is started through the band ladder.
4. The **fallback chain**: if hosting fails with auto-config paused, the app asks the guardian to re-enable it (`autoconfig.on` file) and retries. If it still fails, the app reconnects the laptop's Wi-Fi and runs a shared radio.

**OFF:** stop the hotspot, guardian revert, reconnect the laptop's Wi-Fi, restore the user's own hotspot config.

If the app dies, the guardian notices the missing PID and reverts by itself.

### Upload engines (`web/index.html`)
1. **Lanes** (files ≥ 64 MB): up to 4 lanes, each one long `PUT` of its share of the file. Per-lane resume uses `/status` ranges.
2. **Read-ahead** (automatic switch): if the 5-second speed stays below 65% of its best for 6 s, the page reads 8 MB blocks into memory (6 ahead) and 4 senders upload them. It backs off if it isn't faster than the lanes were.
3. **Safe mode**: one continuous stream from `Contig`, with a 20 s stall abort and resume. It also finishes any leftover gaps.

An earlier design that sent many 16 MB file slices from a Photos video **stalled on iOS after the first 4 pieces (64 MB)**. That's why the current engines avoid many slice requests.

## 5. Measured results (real iPhone, 5 GHz hotspot, 80 MHz, link 1201 Mbps)

| Date/time | Test | Result |
|---|---|---|
| 14:24 | Speed test (6 s phases) | down 1 stream 392 Mbps, down 4 streams 346, up 203 |
| 14:35 | Upload 1.75 GB, single stream | 26.7 MB/s (≈50 for 5 s, then ≈25) |
| 14:45 | Speed test, BBR2 on | down1 399, down4 505, up 361 Mbps |
| 14:47 | Upload 2.09 GB, 4 lanes | 27.7 MB/s (same sag after ~8 s) |
| 14:48 | Download 2.09 GB (PC → phone) | 42.5 MB/s |
| 15:00 | **Speed test, 20 s memory-only upload** | up **223 Mbps ≈ 28 MB/s**, per second 22–37 MB/s |
| 15:01 | Download 2.09 GB | **50.4 MB/s** (best so far) |
| 17:03 | Upload 1.12 GB | 34.3 MB/s |
| local | Server on PC, 4 GB, 4 lanes | 962 MB/s (the PC side is not a bottleneck) |

**Conclusions so far:**
- **PC → iPhone:** ~42–50 MB/s, steady with BBR2. That's near the practical ceiling for an 80 MHz channel shared with ~68 nearby networks. Both 5 GHz 80 MHz blocks a Windows hotspot can use (36–48 and 149–161) are crowded at this location.
- **iPhone → PC:** ~25–35 MB/s sustained. The 20 s memory-only test shows the same level, so the limit is the **iPhone's transmit / uplink**, not file reading and not WarpDrop. The read-ahead engine therefore can't fix it, though it doesn't hurt.
- Multiple streams help downloads (4 streams: ~60 MB/s raw in the speed test), but Safari's downloader is single-stream.

## 6. Hardware and band findings

- `--probe-bands` on this AX211 (driver 24.50) gives: **WPA2 only** (WPA3 and WPA3-transition not supported); bands **2.4 and 5 GHz only**; **6 GHz not supported**. 6 GHz requires WPA3, so this driver can't host 6 GHz. The code falls back to 5 GHz and shows a note.
- The driver INF has 724 registry parameters. The only 6 GHz one is `Is6GhzBandSupported` ("Ultra High Band (6GHz)", the client radio), which is already Enabled. There's no INF parameter for WPA3 or 6 GHz hotspot. Modifying the signed driver was considered and is **out of scope**; this project doesn't do it.
- Any later driver that reports WPA3 plus 6 GHz hotspot support is picked up automatically by the band ladder. Re-run `--probe-bands` after driver updates.
- The hotspot link rate is shown in the UI and logged (`tick link=…Mbps`). 1201 Mbps means 5 GHz, 80 MHz, 2×2.

## 7. Research summary (details and sources in the chat history; key points)

- Every iPhone, including the iPhone 17 with Apple's N1 chip, maxes out at **2×2, 160 MHz**. There's no 320 MHz. Apple's Wi-Fi 7 MLO is single-radio (eMLSR), with **no 5+6 GHz aggregation**. The best published iPhone Wi-Fi result is about **1.6–1.7 Gbps (~200 MB/s)** on 6 GHz at 160 MHz, 1.5 m from a high-end router.
- The Windows Mobile Hotspot is effectively limited to 80 MHz on non-DFS 5 GHz. 6 GHz hotspot needs driver support (Windows 11 24H2+).
- **The fastest fully wireless path:** a tri-band Wi-Fi 7 router or travel router, with the laptop and iPhone on separate radios at 160 MHz (iPhone on 6 GHz) and WarpDrop in `--lan` mode. Estimated 150–200 MB/s. Not yet tested.
- **Wired:** iPhone 15/16/17 **Pro** (USB 3) with a USB-C Ethernet adapter. 1 GbE confirmed working on iPhone (~110 MB/s expected). 2.5 GbE adapter compatibility on iPhone is hit-or-miss. WarpDrop detects a wired link (169.254.x self-assigned addresses) and offers its QR code.
- **Dead ends:** pymobiledevice3/AFC over USB (~25 MB/s up / 85 down); open-source AirDrop (OWL, <2 MB/s); Wi-Fi Aware (needs a native iOS app, and Windows has no NAN API); 60 GHz (no iPhone support); Linux AP on Intel (Intel blocks 5/6 GHz AP mode, and the card is soldered on this laptop).
- **WebKit bug 319985** (iOS 26.5.x): `XHR.send(file)` may send a 0-byte body. The read-ahead engine (reads into memory first) is the workaround if it ever shows up.

## 8. Testing notes (important for any agent)

- **Never switch the network ON from a remote or agent session.** ON disconnects the laptop's Wi-Fi, which kills Remote Control and the agent's own internet. Test ON/OFF only with the owner at the laptop.
- Local server tests:
  1. Start `WarpDrop.exe --lan --nofw --token=t [files]`. It uses port 80, or 8080 if another instance holds 80.
  2. Drive it with curl, Node scripts, or a browser at `http://127.0.0.1[:8080]/?k=t`.
  3. In the browser, test page functions (`upload`, `put`, …) by calling them from the console. You can monkeypatch `put` to simulate drops or stalls.
  4. Verify files with SHA-256.
- Before closing a running `WarpDrop.exe`, check that it's OFF: `%APPDATA%\WarpDrop\session\session.log` ends with `REVERTED`, and no `powershell.exe` is running `guardian.ps1`.
- Delete test files from `Downloads\WarpDrop`, and test entries from `diag.log`, after testing.
- Chromium uploads `ArrayBuffer` bodies slowly on loopback (~14 MB/s). Don't judge the read-ahead engine's speed from the desktop browser.

## 9. Open items / backlog

1. **Push `8ded2b8`** (6 GHz band ladder, `--probe-bands`, band button). Optionally tag `v1.1.0` and let CI attach the exe.
2. **Wi-Fi + wired bonding**: spread the upload lanes and download streams across two server IPs (hotspot and wired 169.254.x). Needs CORS on the server, and checking how iOS routes when both interfaces are up. Download bonding would need a multi-stream download path. Safari's native downloader is single-stream, and a parallel in-page download needs OPFS, which requires a secure context (HTTPS), so that's not available on plain HTTP.
3. **Router/LAN mode UX**: a first-class "use an external Wi-Fi 7 router" mode instead of the `--lan` flag. Show the router link and keep the switch semantics.
4. **17:00 session note:** the first hotspot start with auto-config paused failed ("Wi-Fi is off"). The fallback (resume auto-config) worked. Consider starting the hotspot before pausing auto-config, or always resuming it, if this repeats.
5. **iPhone model is still unknown.** It decides which wired/6 GHz options apply.
6. Possible: add CORS plus an HTTPS option for a secure context (would enable OPFS-based parallel downloads, but needs certificate trust on the iPhone).
