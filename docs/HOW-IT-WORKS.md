# How WarpDrop works

## Overview

```
iPhone (Safari)                                   Windows PC (WarpDrop.exe)
──────────────                                    ──────────────────────────────────────────
 web page  ◄── HTTP/1.1, keep-alive ──────────►  FastServer (raw sockets, port 80/8080/…)
   │  speed test (memory only)                     │  /f/{id}/{name}   downloads (Range)
   │  upload engines (lanes / read-ahead / safe)   │  /api/up/*        resumable uploads
   │                                               │  /api/bench/*     speed test endpoints
   ▼                                               ▼
 joins WarpDrop-XXXX (WPA2, 5 GHz)  ◄────────►  Windows Mobile Hotspot (WinRT tethering API)
                                                  SessionGuardian (elevated PowerShell)
```

## The network

- **Hotspot.** Started through `NetworkOperatorTetheringManager`, the same API as Settings → Mobile hotspot. The PC's address on the hotspot is normally `192.168.137.1`.
- **Band ladder.** WarpDrop tries each band in order until one starts:
  1. **6 GHz with WPA3 (SAE).** 6 GHz requires WPA3, so this needs both `IsAuthenticationKindSupportedAsync(Wpa3)` and `IsBandSupportedAsync(SixGigahertz)`.
  2. **5 GHz with WPA2.**
  3. **Auto**, letting Windows choose.

  Each skipped step and why is logged. The 6 GHz step can be turned off from the UI for iPhones without 6 GHz. `--probe-bands` prints the same capability checks without starting anything.
- **Why some drivers can't do 6 GHz.** The WPA3/6 GHz hotspot capability comes from the Wi-Fi driver itself. On Intel AX211 there's no registry setting for it: its INF exposes `Is6GhzBandSupported` (the 6 GHz client radio), but nothing for a WPA3 or 6 GHz hotspot. 6 GHz rules only let certified access points start transmissions on the band, which is why vendors gate it in the driver.
- **Dedicated radio.** Laptops have one Wi-Fi radio, shared between the laptop's own connection (the "station") and the hotspot. While the station is connected, the hotspot has to use its channel or time-share with it. WarpDrop therefore:
  1. pauses Windows Wi-Fi auto-config through the guardian, which stops auto-joining and background scans;
  2. disconnects the station and waits until it's really down;
  3. starts the hotspot, so the driver picks a free channel;
  4. runs a watchdog that disconnects the station again if anything reconnects it.
- **Fallbacks.** If Windows won't host in that state, WarpDrop first turns auto-config back on. If that still fails, it shares the radio.
- **QR codes.** QR 1 is a standard `WIFI:T:WPA;S:…;P:…;;` payload, which the iOS Camera understands. QR 2 is `http://<ip>/?k=<token>`.

## The session guardian

`Tuning.cs` holds an embedded PowerShell script that runs elevated, one UAC prompt per ON session:

1. **Snapshot.** It records the original values of everything it will touch: adapter advanced properties, the Wi-Fi power plan value, TCP template settings, the hotspot timeout registry value and the auto-config state. They're saved to `%APPDATA%\WarpDrop\session\original.json`. A leftover snapshot from a crash always wins, so true originals are never lost.
2. **Apply.** It applies the tuning and restarts the adapter once if driver settings changed.
3. **Hold.** It waits until the app writes a stop file, or until the app's process disappears.
4. **Revert.** It restores everything, reconnects the station to its saved profile, and deletes the snapshot. If the app died, it also stops a running `WarpDrop-*` hotspot.

All steps are logged to `%APPDATA%\WarpDrop\session\session.log`.

## The HTTP server (`FastServer.cs`)

The server is a minimal HTTP/1.1 implementation directly on `Socket`, with no web framework:

- **Downloads** are double-buffered. 2 MB `RandomAccess.ReadAsync` reads overlap with socket sends, and 8 MB kernel socket buffers stay above the bandwidth-delay product even when Wi-Fi latency spikes.
- **Uploads** are written with `RandomAccess.WriteAsync` at their offsets into a preallocated `*.warpdrop-part` file. Each written `[start, end)` range is merged into a sorted list. `Contig`, the gap-free prefix, is the resume point.
- **Finishing** is refused with `409` unless the whole file is present, so a truncated file is never saved.
- **Dead connections:** a body read that sees no data for 60 s aborts, so a phone that drops off Wi-Fi without closing the connection doesn't hold an upload forever.
- **Access:** every request except `/` needs `?k=<token>`.

### API

| Method | Path | Purpose |
|---|---|---|
| GET | `/` | The phone page (embedded `web/index.html`) |
| GET | `/api/state` | Shared files, live MB/s, PC name |
| GET/HEAD | `/f/{id}/{name}` | Download a shared file (supports `Range`) |
| POST | `/api/up/init?name=&size=` | Start an upload → `{id}` |
| PUT | `/api/up/{id}?off=` | Write the request body at byte offset `off` (any length, any order) |
| GET | `/api/up/{id}/status` | `{received, ranges:[[s,e],…]}`: gap-free prefix and all written ranges |
| POST | `/api/up/{id}/done` | Finish (409 with `{received}` if anything is missing) |
| POST | `/api/up/{id}/cancel` | Abort and delete the partial file |
| GET | `/api/bench/down?bytes=` | Speed test: stream bytes from memory |
| PUT | `/api/bench/up` | Speed test: discard the body |
| POST | `/api/bench/result?down1=&down4=&up=` | Speed test results (shown on the PC) |
| POST | `/api/log?msg=` | Phone-side events into the diagnostics log |

## Upload engines (`web/index.html`)

A single TCP stream on a busy Wi-Fi channel halves its speed after every loss. Parallel streams hold the speed up. iOS Safari also has its own quirks when uploading large Photos videos. WarpDrop handles both with three engines:

1. **Lanes (default for files ≥ 64 MB).** The file is split into up to 4 equal lanes, and each lane is one long PUT of its share. When a lane drops, it resumes exactly where the PC's data for that lane ends, read from `/status` ranges.
2. **Read-ahead (automatic switch).** The lane speed is watched over a 5-second window. If it stays below 65% of its best for 6 seconds, the lanes stop. A reader then loads the remaining gaps into memory in 8 MB blocks, up to 6 blocks ahead, while 4 senders upload ready blocks. If this engine isn't faster than the lanes were, it gives up after 8 seconds.
3. **Safe mode.** One continuous resumable stream from the gap-free prefix. It also finishes any leftover gap after the other engines.

A stalled XHR (no progress for 20 s) is aborted and resumed. Every engine switch and failure is reported to the PC's log.

## Diagnostics

`%APPDATA%\WarpDrop\diag.log` records:

- each ON/OFF step and the guardian's changes;
- the hotspot result and link rate;
- a 2-second tick with devices, MB/s in each direction and hotspot state;
- speed test results with per-second series;
- upload starts, drops, resumes and engine switches;
- each finished transfer with its average speed.

## Known limits

- **Channel width.** Windows Mobile Hotspot uses at most 80 MHz on non-DFS 5 GHz channels, so the link rate is 1201 Mbps with a 2×2 adapter. Many adapters can't host on 6 GHz at all, and apps can't choose the channel.
- **Shared airtime.** In buildings with many networks, both usable 80 MHz blocks (36–48 and 149–161) are usually busy. Real throughput is then roughly 35–50% of the link rate.
- **iPhone → PC on large videos.** In testing, sustained uploads of large Photos videos sometimes dropped to about half speed after 5–10 seconds, independent of the number of streams. The PC side was verified at about 1 GB/s, so the cause is on the phone: the file-read path, or the Wi-Fi transmitter throttling. The read-ahead engine targets the first. The 20-second upload speed test tells the two apart.
- **Downloads.** Safari's downloader uses one connection, so download speed depends on that stream. BBR2 helps it hold steady.
