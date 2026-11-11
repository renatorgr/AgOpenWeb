# AgOpenWeb with a sprayer readout

This is a fork of [AgOpenGPS-Official/AgOpenWeb](https://github.com/AgOpenGPS-Official/AgOpenWeb) that adds a **sprayer readout**: boom pressure, flow and alarms from the sprayer's machine module, shown in a strip above the section buttons. It also carries [some changes that the official version doesn't have yet](#other-changes-in-this-fork). Everything else is the official AgOpenWeb `develop`, brought in by a pull request each day.

The official README follows [below](#agopenweb).

## Sprayer readout

- Shows the pressure (bar), the flow (L/min), the actual rate (L/ha), and one small square per section in the section buttons' colours.
- Raises an alarm, with a sound, for low or high pressure, for a flow that doesn't match the open nozzles, and for flow with every section closed.
- Warns when the module doesn't open or close a section that AgOpenWeb asked for.
- With a target rate set, shows the pressure that gives that rate at the current speed. It only advises: nothing is regulated.
- The settings (target rate, nozzles, pressure limits, flow tolerance) are on the module's own web page ([Sprayer_ESP32](https://github.com/renatorgr/Boards/tree/main/Sprayer/Sprayer_ESP32) 0.10 and newer, Settings tab), not in AgOpenWeb. The module works out the rate, the pressure for the target and the alarms; AgOpenWeb shows and sounds what it sends, as the official AgOpenWeb leaves the rate settings to the rate controllers. The section widths stay in AgOpenWeb (Tool tab). With an older module program (PGN 160 only) the strip shows the pressure, flow and sections, without a rate or alarms.
- The strip only appears while a sprayer module is sending data.

The module sends PGN 160 to port 9999 once a second, then PGN 161 with ISOBUS process data (DDIs), see [Docs/PGN.md](Docs/PGN.md#sprayer-readout). Firmware is in [renatorgr/Boards](https://github.com/renatorgr/Boards/tree/main/Sprayer): Sprayer_ESP32 for an ESP32 relay board, and the older Sprayer_Nano for an Arduino Nano with a relay board.

The readout was proposed to the official project in [AgOpenGPS-Official/AgOpenWeb#390](https://github.com/AgOpenGPS-Official/AgOpenWeb/issues/390). The maintainers would rather handle sprayer data the ISOBUS way, so it lives in this fork for now.

## Other changes in this fork

Most of these have been offered to the official project too. When one is merged there, it comes off this list.

- **Obstacles.** In the boundary list, each obstacle gets its own choice: a U-turn before it, Go around (the pass bends round it), or Drive through. Trees can be added as circles with "+ Circles", and small obstacles made round. The Go around curve radius and safety margin are on the U-turn tab. Go around is still being tested: don't use it with AutoSteer on a real tractor yet.
- **Headland gaps.** With "Drive across a headland gap up to" set on the U-turn tab, a pass that crosses a narrow tip of the field is driven on with the sections off, and the turn is made at the far end.
- **U-turns with no room.** When no proper turn fits, the simple turn is moved back into the field instead of crossing the boundary. Where the headland clips a corner off the pass, AutoSteer drives on across it and says so.
- **U-turn line.** When a U-turn starts, the highlighted line, the tool's band and the pass number move to the pass the turn ends on, as in AgOpenGPS. They used to change only when the turn was finished, with the tractor already driving along the new pass.
- **Manual U-turn beside a hard boundary.** With the implement already nearer a hard boundary than the U-turn distance (a pass along the fence), the manual U-turn button did nothing, even to turn away from the fence. Now the turn is made as long as the implement stays inside the boundary. A manual U-turn that is refused, and an automatic one that can't be made or is missed, says why in a message on the screen; before there was only a sound, or nothing.
- **Closing a field.** It also stops a recorded path that is being driven or recorded. With no field open, the section buttons, their hotkeys and the work switch do nothing, so no section can come on with no buttons on the screen to turn it off.
- **When the operator's screen goes away.** If the screen in control stops answering (a tablet out of Wi-Fi range, a closed tab), it shows Disconnected after 1.5 s. Free Drive and any section switched On by hand are turned off.
- **Simulator.** The simulator does not drive the real steering. With a steer module on the network, AutoSteer on the simulator used to make it open the lock valve and drive the steering valve or motor. Now the module is told "don't steer" for as long as the simulator runs, and AutoSteer goes off when the simulator is turned on or off. For a bench test (a steering motor on the desk) the simulator bar has a **Bench** button, shown while a steer module answers: it asks first, turns red, and lets AutoSteer on the simulator move the steering; it is never saved and goes off with the simulator or when the module stops answering. Free Drive and the Steer Wizard's tests move the steering without it. The sections still go to the machine module, for bench tests.
- **Dual antenna check.** AutoSteer won't turn on, and turns off, when the dual heading points across the direction of travel. That means the antennas are set up the wrong way round.
- **AgOpenGPS settings.** Tractors and implements from AgOpenGPS 6.8.2 or newer, and AgIO's NTRIP caster, are converted when AgOpenWeb starts, once they are copied into its data folder.
- **Steer Wizard: tuning the wheels.** A new step after the Ackermann circle finds Minimum To Move, Max Limit and Proportional Gain by itself. It first checks that the stop works, then moves the wheels a few degrees while the tractor stands still. Nothing changes until "Use these values" is pressed. So far it has only been tested on a simulated steering: not on a tractor yet.
- **Modules dot.** A machine module counts as present while it sends its own messages (PGN 237, and a sprayer's 160/161), not only while it answers the hello, so a module on Wi-Fi no longer makes the dot flash amber while it works.
- **Web cards.** ⚙ › "Search the network" finds the modules' web pages: first the ones that announce themselves by mDNS, then by asking each address on the local network; a module found that way is then asked for its own name, so it is listed by name even where the network loses mDNS on its way to the Wi-Fi. Each page has a zoom button, and the card's header buttons are big enough to hit in a moving cab.

## Download

Get the [Beta build of this fork](https://github.com/renatorgr/AgOpenWeb/releases/tag/beta). It has the same files as the official Beta (see "Download & run" below), and its title shows when it was built and from which commit. Links below that point to AgOpenGPS-Official give you the official build, without the changes above.

The beta is not rebuilt on a schedule. To build a new one from `pulverizador`, go to Actions › Build Deployment Bundles › Run workflow, and keep "publish" on `beta`.

## Branches and tags

| Branch | What it holds |
|---|---|
| `pulverizador` (default) | The official `develop` plus the changes above. All work on this fork goes here. |
| `develop` | An exact copy of the official `develop`. Don't commit to it. |
| `sync-pulverizador` | The daily update on its way into `pulverizador`. Don't commit to it. |

The official changes come in once a day, with nothing to press:

1. The newest official `develop` commit is merged into `sync-pulverizador`, and any conflict with this fork's changes is resolved there.
2. A pull request, "Sync pulverizador with official develop …", runs CI on it.
3. It is merged into `pulverizador` only when every check has passed on that same commit. With a red check it stays open until it is fixed.

This is done by a scheduled Claude Code session, not by a GitHub Action: an Action can't resolve a conflict, may not change workflow files, and a pull request it opens starts no CI. Don't use "Sync fork" or "Discard commits" on `pulverizador`. A GitHub Action ([`sync-develop.yml`](.github/workflows/sync-develop.yml)) only keeps `develop` a copy of the official one; its run fails when `pulverizador` has fallen more than three days behind, which means the daily update has stopped.

The `beta` tag belongs to the beta release: leave it alone. The `v…` tags are copies of the official ones. Don't push tags to this fork from a computer: a `v…` tag pushed here starts a release build.

---

# AgOpenWeb

[![CI](https://github.com/AgOpenGPS-Official/AgOpenWeb/actions/workflows/build-and-release.yml/badge.svg?branch=develop)](https://github.com/AgOpenGPS-Official/AgOpenWeb/actions/workflows/build-and-release.yml)
[![Translation status](https://hosted.weblate.org/widget/agopengps/agopenweb/svg-badge.svg)](https://hosted.weblate.org/projects/agopengps/agopenweb/)

[![Beta build](https://img.shields.io/github/release-date-pre/AgOpenGPS-Official/AgOpenWeb?label=beta%20build&style=for-the-badge&color=brightgreen)](https://github.com/AgOpenGPS-Official/AgOpenWeb/releases/tag/beta)

> **⬇️ Download the [Beta build](https://github.com/AgOpenGPS-Official/AgOpenWeb/releases/tag/beta). It is rebuilt from `develop` as fixes land and has all the latest fixes. Please install it, and check the problem still happens on it, before you open an issue.**

Cross-platform agricultural GPS guidance — a rewrite of [AgOpenGPS](https://github.com/farmerbriantee/AgOpenGPS) using HTML/JS, .NET 10, and C#. The UI is a web page served up by the backend host and shown in each platform's own web view.

## Download & run

Get the [**Beta**](../../releases/tag/beta) build. It is rebuilt from `develop` on demand and
daily when there are changes, and it is the build to use and report issues against. Everything is a self-contained app (no .NET install needed). Pick the set that matches how you'll run it — in
every mode the host also serves `http://<host>:5174` on the LAN, so cab phones/tablets connect
in a browser.

**🖥️ Desktop** — a window with the guidance UI (host runs in-process):

| OS | Download | Run |
|---|---|---|
| Windows x64 | `agopenweb-win-x64.zip` | run `AgOpenWeb.Desktop.exe` |
| macOS (Apple silicon) | `agopenweb-macos-arm64.dmg` | drag to Applications, launch |
| Linux x64 / arm64 | `agopenweb-launcher-linux-<arch>.tar.gz` | `./run.sh` |

**🛰️ Headless** (Linux + Windows) — no window; UI in a browser at `http://<host>:5174`, auto-starts
on boot (for an in-cab mini-PC / SBC):

| OS | Download | Install |
|---|---|---|
| Linux x64 / arm64 | `agopenweb-linux-<arch>.tar.gz` | `sudo ./install.sh --from app` (systemd) |
| Windows x64 | `agopenweb-win-x64.zip` | `install-service.cmd` → Run as administrator (Windows Service) |

**📱 Mobile** — all-in-one (host + UI on the device):

| Device | Download | Install |
|---|---|---|
| Android | `agopenweb-android.apk` | sideload |

Each download's bundled `README` lists the prerequisites (e.g. the WebView2 Runtime on Windows,
WebKitGTK on Linux).

## Platforms

- Windows x64
- macOS x64 / ARM64
- Linux x64 / ARM64
- Android
- iOS (sideload)

## Tech stack

- **UI:** HTML/JS web client with CanvasKit (Skia WebAssembly); shown in WebView2 / WKWebView / WebKitGTK on desktop (Photino.NET), WKWebView on iOS, WebView on Android
- **Control brain:** `MainViewModel` with CommunityToolkit.Mvvm, driven by the web client
- **Runtime:** .NET 10
- **Architecture:** shared backend on every platform; the platform projects are thin web-view shells
- **Testing:** NUnit, NSubstitute

## Building from source

Prerequisites: .NET 10 SDK

```bash
# Desktop (Windows/macOS/Linux)
dotnet build Platforms/AgOpenWeb.Desktop
dotnet run --project Platforms/AgOpenWeb.Desktop

# Android
dotnet build Platforms/AgOpenWeb.Android

# Run tests
dotnet test
```

See [BUILD.md](BUILD.md) for platform-specific setup, and `deploy/{linux,windows,macos}/` for the
packaging scripts that produce the release bundles above.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the architecture overview, feature list, and how to get
started.

To help translate the UI, no code is needed: join in on [Weblate](https://hosted.weblate.org/projects/agopengps/agopenweb/). See [Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md).

## License

[Apache License 2.0](LICENSE.md)
