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

- **Its own network.** Starts Windows Mobile Hotspot on 5 GHz with a random password, and dedicates the Wi-Fi radio to it.
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
| Intel Wi-Fi driver | Throughput Booster on, MIMO power save off, max transmit power, widest channel | Both antennas always active, aggressive transmit bursting. Skipped on adapters that don't have these settings. |
| Wi-Fi adapter power plan | Maximum performance (battery too) | No power-save throttling mid-transfer. |
| Windows power mode | Best performance, no sleep, high process priority | No CPU throttling. |
| Hotspot auto-off | Disabled | Windows otherwise turns an idle hotspot off. |

**Switch OFF restores every original value.** It also turns Wi-Fi auto-config back on, reconnects your laptop to its Wi-Fi, and restores your own Mobile Hotspot name, password and on/off state.

The restore is handled by a small elevated guardian process. If WarpDrop crashes or is killed while ON, the guardian notices and restores everything by itself. The original values are also saved to disk, so even a power cut can't leave settings changed: the next ON/OFF, or `WarpDrop.exe --restore-system`, puts them back.

The only lasting change is a Windows Firewall rule that allows WarpDrop itself. `--restore-system` removes it.

