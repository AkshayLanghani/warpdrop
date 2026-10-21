# WarpDrop

**Send huge files between a Windows PC and an iPhone over Wi-Fi, as fast as the hardware allows. No app on the phone, no cable, no cloud, no router.**

[![Build](https://github.com/AkshayLanghani/warpdrop/actions/workflows/build.yml/badge.svg)](https://github.com/AkshayLanghani/warpdrop/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)

WarpDrop turns your laptop into its own private 5 GHz Wi-Fi network. Scan one QR code on the iPhone to join it, scan another to open the transfer page in Safari, and move multi-gigabyte files both ways at full speed.

```
 ┌──────────────┐   own 5 GHz network, direct, no router   ┌──────────────┐
 │  Windows PC  │ ◄──────────────────────────────────────► │    iPhone    │
 │  WarpDrop    │      HTTP on 192.168.137.1 (Safari)       │   (Safari)   │
 └──────────────┘                                           └──────────────┘
```

## Features

- **Its own network.** Starts Windows Mobile Hotspot with a random password and dedicates the Wi-Fi radio to it.
  - **6 GHz first:** if the Wi-Fi driver can host 6 GHz (WPA3), WarpDrop uses it. 6 GHz is far less crowded than 5 GHz.
  - Otherwise it uses 5 GHz.
  - One tap switches back to 5 GHz for iPhones without 6 GHz.
- **QR codes:** one to join the Wi-Fi, one to open the page. The iPhone remembers the network for next time.
- **Both directions:**
  - PC → iPhone: drag files onto the window, tap Download on the phone. Files land in Files › Downloads.
  - iPhone → PC: pick photos, videos or files on the phone. They're saved to `Downloads\WarpDrop`.
- **Fast:**
  - custom zero-overhead HTTP server (~1 GB/s locally);
  - parallel uploads over up to 4 lanes, with an engine that switches automatically if speed drops;
  - BBR2 congestion control while running;
  - driver and power tuning while running.
- **Reliable:**
  - interrupted uploads resume from the exact byte the PC has on disk;
  - the PC never saves a truncated file;
  - downloads support HTTP Range.
- **One ON/OFF switch.** OFF leaves your Wi-Fi, internet and hotspot completely alone. ON boosts everything. OFF restores every setting exactly, even if the app crashes.
- **Built-in speed test and diagnostics log**, so you see measured numbers instead of guesses.
- **Single portable `.exe`.** Nothing to install, and nothing to install on the phone.

## Requirements

| | |
|---|---|
| PC | Windows 10 (2004+) or Windows 11, with a Wi-Fi adapter that supports Mobile Hotspot. 5 GHz capable is strongly recommended. |
| Phone | iPhone with iOS 15 or later, using Safari. Other phones' browsers work too. |
| Admin rights | One UAC prompt each time you switch the network ON (for the temporary speed tuning). You can decline it; WarpDrop still works without the tuning. |

## Setup

### Option A: download the app (easiest)

1. Go to [**Releases**](https://github.com/AkshayLanghani/warpdrop/releases/latest) and download `WarpDrop.exe`.
2. Put it anywhere, for example `C:\Tools\WarpDrop.exe`, and double-click it.
   - Windows SmartScreen may warn about an unsigned app: click **More info → Run anyway**.
3. Optional: right-click the exe → **Send to → Desktop (create shortcut)**.

The exe is self-contained (.NET runtime included), so there's nothing else to install.

### Option B: build from source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone and build:

   ```bash
   git clone https://github.com/AkshayLanghani/warpdrop.git
   cd warpdrop
   dotnet publish WarpDrop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
   ```

3. Run `dist\WarpDrop.exe`.

For development, `dotnet run --project WarpDrop` works too.

## How to use it

1. Open WarpDrop. It starts with the switch **OFF**, so nothing on your PC has changed yet.
2. Flip **WarpDrop network** (top right) to **ON** and approve the admin prompt. Within a few seconds:
   - your laptop disconnects from its normal Wi-Fi, so the radio serves only WarpDrop (laptop internet is off while ON);
   - the private `WarpDrop-XXXX` 5 GHz network starts;
   - two QR codes appear.
3. On the iPhone, open the **Camera**:
   1. scan **QR 1** and tap **Join**;
   2. scan **QR 2** and tap the link. It opens in Safari.
4. Transfer:
   - **PC → iPhone:** drag files or folders onto the WarpDrop window, then tap **Download** on the phone.
   - **iPhone → PC:** tap **Choose photos & files** on the phone.
   - Keep Safari open with the screen on during big transfers.
5. Flip the switch **OFF** when you're done. Everything is restored and your laptop reconnects to its Wi-Fi.

Tap **Run speed test** on the phone page any time to measure the link. The result also shows on the PC.

## What the switch changes (and puts back)

While the switch is ON, WarpDrop makes these changes so the radio and network stack run flat out:

| Setting | While ON | Why |
|---|---|---|
| Laptop's own Wi-Fi connection | Disconnected | The radio serves only the WarpDrop network and picks its own channel. Otherwise it's pinned to the internet network's channel or time-sliced against it (≈ half speed). |
| Windows Wi-Fi auto-config (auto-join + background scans) | Paused | Background scans pull the radio off-channel; auto-join would reconnect to another saved network. |
| TCP congestion control | CUBIC → **BBR2**, min retransmit timeout 300 → 60 ms | A single stream holds its speed on a noisy channel instead of halving on every lost packet. |
| Intel Wi-Fi driver | Throughput Booster on, MIMO power save off, max transmit power, widest 5 and 6 GHz channels, 6 GHz radio on | Both antennas always active, aggressive transmit bursting. Skipped on adapters that don't have these settings. |
| Wi-Fi adapter power plan | Maximum performance (battery too) | No power-save throttling mid-transfer. |
| Windows power mode | Best performance, no sleep, high process priority | No CPU throttling. |
| Hotspot auto-off | Disabled | Windows otherwise turns an idle hotspot off. |

**Switch OFF restores every original value.** It also turns Wi-Fi auto-config back on, reconnects your laptop to its Wi-Fi, and restores your own Mobile Hotspot name, password and on/off state.

The restore is handled by a small elevated guardian process. If WarpDrop crashes or is killed while ON, the guardian notices and restores everything by itself. The original values are also saved to disk, so even a power cut can't leave settings changed: the next ON/OFF, or `WarpDrop.exe --restore-system`, puts them back.

The only lasting change is a Windows Firewall rule that allows WarpDrop itself. `--restore-system` removes it.

## How fast is it?

**The Wi-Fi link is the limit, not WarpDrop.** The server moves about 1 GB/s on the PC itself. What you get over the air depends on hardware and on how crowded your 5 GHz channels are.

| Example (Intel AX211 laptop, recent iPhone, apartment building with ~70 visible Wi-Fi networks) | Measured |
|---|---|
| Hotspot link rate | 1201 Mbps (5 GHz, 80 MHz, 2×2) |
| PC → iPhone, 2 GB video | **42.5 MB/s** sustained (≈ 340 Mbps) |
| iPhone → PC, 4-lane speed test | 45–51 MB/s |
| iPhone → PC, large video | 25–50 MB/s; varies with how the iPhone reads and transmits (see [docs](docs/HOW-IT-WORKS.md#known-limits)) |

Some honest physics:

- **Windows hotspots are limited to 80 MHz channels** on 5 GHz (no radar-checked DFS channels). That caps the link rate at 1201 Mbps, which is roughly 600–950 Mbps of real data in a quiet location.
- **6 GHz hosting depends on the Wi-Fi driver.** It needs a driver that supports WPA3 hotspots (Windows 11 24H2+). Many don't yet, including Intel AX211 on driver 24.50. Run `WarpDrop.exe --probe-bands` to see what yours supports. When 6 GHz is available, WarpDrop uses it automatically. It works with iPhone 15 Pro, iPhone 16 and iPhone 17 models (not 16e/17e).
- **Crowded channels cut that further.** Every nearby network on the same channel shares the airtime, and Windows doesn't let apps choose the hotspot channel.
- **Faster than Wi-Fi:** on iPhone 15 Pro and later (USB 3), connect a 2.5 GbE USB-C Ethernet adapter to the phone and one to the PC with a cable. WarpDrop detects the wired link and shows a QR code for it.

Getting the most out of Wi-Fi:
- Keep the phone close to the laptop.
- Keep Safari in the foreground with the screen on.
- Plug the laptop in.

## Troubleshooting

| Problem | Fix |
|---|---|
| "Couldn't create the network" | Turn Wi-Fi on in Windows. Some adapters or drivers don't support Mobile Hotspot. Everything is restored automatically. |
| Status says "shared radio" | Windows refused to host with the laptop's Wi-Fi disconnected, so WarpDrop fell back to sharing the radio. It works, but slower. |
| Phone page won't load | The firewall prompt may have been declined. Switch OFF and ON again and approve the admin prompt. |
| Laptop has no internet | Expected while ON (the radio is dedicated). Switch OFF. Ethernet keeps working while ON. |
| Transfer slowed down | Look at `%APPDATA%\WarpDrop\diag.log`. It records link rate, per-second speeds, speed tests, engine switches and errors. |
| Settings feel off after a crash | Run `WarpDrop.exe --restore-system`. |

## Command-line options

| Option | Effect |
|---|---|
| `--restore-system` | Emergency undo: restores all original Wi-Fi, TCP, power and hotspot settings and removes the firewall rule. |
| `--probe-bands` | Read-only: shows which hotspot bands (2.4/5/6 GHz) and security modes (WPA2/WPA3) this PC's Wi-Fi driver can host. Changes nothing. |
| `--notune` | Switch ON without the admin prompt: no tuning and no firewall rule (Windows shows its own firewall prompt instead). Slower, but no system changes. |
| `--lan` | No switch. Serve on the network the PC is already on (phone must be on the same Wi-Fi). |
| `<file or folder paths>` | Share these at startup. |

## Privacy and security

- Everything stays between your PC and your phone. Nothing is uploaded anywhere, and there's no telemetry.
- The network is WPA2-protected with a random password stored only on your PC (`%APPDATA%\WarpDrop\settings.json`).
- Every file download and API call needs a random access token that's embedded in the QR code and changes every time the app starts. Without it, the page loads but can't list, download or upload anything.
- Received files go to `Downloads\WarpDrop`. Shared files are read-only to the phone.

## Project layout

```
WarpDrop/
├── FastServer.cs        HTTP/1.1 server on raw sockets: downloads, resumable parallel uploads, speed test
├── Hotspot.cs           WinRT Mobile Hotspot control, dedicated radio, wired-link detection
├── Tuning.cs            Elevated session guardian (apply + restore system tuning), power boost
├── MainWindow.xaml(.cs) The desktop UI and the ON/OFF switch
├── Support.cs           Settings, QR rendering, diagnostics log, helpers
└── web/index.html       The iPhone page (embedded in the exe): upload engines, speed test
```

See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) for the architecture, HTTP API, upload engines and measurements.

## Contributing

Issues and pull requests are welcome. The most useful thing you can share is a speed test result plus the relevant lines from `diag.log`, along with your Wi-Fi adapter and phone model. Please check a change end to end with a real phone before opening a PR. The upload engines especially behave differently on iOS than on desktop browsers.

## License

[MIT](LICENSE)
