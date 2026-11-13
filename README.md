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
- **U-turns with no room.** When no proper turn fits, the simple turn is moved back into the field instead of crossing the boundary. Where the headland clips a corner off the pass, AutoSteer drives on across it and says so (a thin spike of the headland line doesn't count).
- **Skip worked passes.** A pass more than half done but left short of the end the tractor is at (its 20 m next to that end mostly not covered) counts as still to do there, so the turn can go onto it. (The turn onto the nearest pass with room is official since #807.)
- **AutoSteer and a hard boundary.** At the end of a pass with no U-turn there (no pass with room at this end, no turn that fits, U-turn off), AutoSteer goes off with the alarm a metre into the headland, so there is the headland to turn by hand; before, it steered on through the fence. And with no U-turn planned, when an end of the implement is heading out across a hard boundary (AutoSteer joining a line from a corner, for example), AutoSteer goes off with the alarm. It looks along the curve the tractor is steering, so on the first lap of a boundary curve it no longer goes off at a bend with the tractor on the line. A U-turn planned for one end is dropped when the tractor turns round on the pass, so these still work at the other end.
- **AutoSteer flickering on and off.** With the steer switch / button set to Button, when the app took AutoSteer off just as it went on (a hard boundary warning, for example), the steer module's answer to the engage turned it on again, and it went on and off for seconds with the alarm. The app now ignores the module's "steering" for 1 s after it takes AutoSteer off itself.
- **No line outside a hard boundary.** A pass that lies wholly outside a hard boundary (a lap of a boundary curve outside the fence, say) is no longer shown when the tractor is outside the field with AutoSteer off: the nearest pass inside is shown. Moving the line onto such a pass is refused with a message.
- **Moving onto a line beside a hard boundary.** When AutoSteer is turned on, or the line is moved or changed, with the tractor off the new line and the implement near a hard boundary, AutoSteer now drives a smooth S onto the line that keeps the implement's ends inside the fence. A front implement swings out ahead of the tractor as it straightens, so its S is longer (an 8 m front boom 4.5 m ahead, moving one pass at 8 km/h: about 60 m; a rear implement: about 20 m). A tractor already heading steeply towards the line is first turned along it. The S is drawn on the map. When no S fits (the end of the field is too near), AutoSteer goes off with the alarm and says to move over by hand. Checked by replaying a simulator drive, not tried on a tractor yet.
- **Boundary laps through a narrow part.** On a closed boundary curve, an inner lap that can't keep its distance from the fence where the field narrows (a neck narrower than twice the lap's distance) was joined straight across, along the fence. Now that stretch isn't drawn, and AutoSteer goes off with the alarm 10 m plus 2 s before it, so an outer lap can be taken there or the tractor steered by hand. A passage only a little too narrow, where the lap stays nearer its own place than the next lap out's, is driven as before. Not tried on a tractor yet.
- **Boundary laps at the seam.** Where a closed boundary curve starts and ends, an inner lap ran 20 cm past the seam and back, so a tractor a little off the lap there could be turned round by AutoSteer. The lap now goes straight on through the seam. Where the seam is at the tip of a narrow strip cut into the field, the lap also no longer goes out across the fence round the tip and crosses itself.
- **Manual U-turn beside a hard boundary.** With the implement already nearer a hard boundary than the U-turn distance (a pass along the fence), the manual U-turn button did nothing, even to turn away from the fence. Now the turn is made as long as the implement stays inside the boundary. (The message saying why a U-turn was refused or missed is official since #804.)
- **Joining a new line from the headland.** U-turn tab › "Joining a new line in the headland": Nearest point (as before), Ask, or Start of the pass. When AutoSteer is turned on, or the line is moved or changed, with the tractor in the headland facing the field, Start of the pass drives along the headland to where the pass begins and puts the tractor straight on it a set distance before the crop (3 m by default); Ask draws that way, sounds and shows two buttons, and keeps the nearest point until one is pressed. The way stays in the headland at the U-turn radius, keeps the implement off a hard boundary and out of obstacles; when it doesn't fit, a message says so and the nearest point is used. With the tractor facing out of the field, Start of the pass and Ask turn it round through the headland when there is room. In any mode, AutoSteer turned on in the headland facing a hard boundary, with the line ahead running out of the field, goes off at once and says to turn the tractor to face the field. Tested on the simulator, not on a tractor yet.
- **A front implement round bends.** AutoSteer steers the tractor's rear axle on the line, so an implement fixed ahead of the tractor ran outside the line on every bend (4.5 m ahead: 0.2 m on a 50 m bend, 0.5 m on a 20 m bend, 1.3 m on an 8 m bend), leaving gaps on the inside and overlaps on the outside. Now, with a front-fixed implement, the tractor steers a little inside each bend so the implement runs on the line. Never tighter than the tractor can turn. Checked by replaying a simulator drive, not tried on a tractor yet.
- **Closing a field.** It also stops a recorded path that is being driven or recorded. With no field open, the section buttons, their hotkeys and the work switch do nothing, so no section can come on with no buttons on the screen to turn it off.
- **U-turn skip kept per field.** The skip button's mode (Normal, Alternative, Ignore worked tracks) and the number of rows are saved with each field and come back when it opens, also after AgOpenWeb was closed. A field never changed opens in Normal.
- **Bench button.** For a bench test (a steering motor on the desk) the simulator bar has a **Bench** button, shown while a steer module answers: it asks first, turns red, and lets AutoSteer on the simulator move the steering. It is never saved and goes off with the simulator or when the module stops answering. The official project keeps the simulator from steering, without the button.
- **Simulator 10x is time, not speed.** The simulator bar's 10x now runs time ten times faster at the speed shown, so a field is worked in a tenth of the time with the same U-turns, guidance and sections as at 1x. Before, it drove the tractor ten times faster (100-250 km/h), and the U-turns were not made. Proposed to the official project in [#726](https://github.com/AgOpenGPS-Official/AgOpenWeb/issues/726).
- **Holding the line when RTK is lost.** Without corrections (no mobile network in a valley) the receiver's position jumps half a metre to two metres sideways, and AutoSteer pulled the tractor across. Now the jump is measured and taken out, so the tractor stays on its pass for up to 3 minutes; when RTK is back, the line goes back to the true position slowly, with no pull on the steering. The fix label shows how much is held ("GPS Fix, held 0.9 m"). Vehicle › GPS › "Hold the line when RTK is lost", on by default. Not tried on a tractor yet.
- **Steer Wizard: tuning the wheels.** A new step after the Ackermann circle finds Minimum To Move, Max Limit and Proportional Gain by itself. It first checks that the stop works, then moves the wheels a few degrees while the tractor stands still. Nothing changes until "Use these values" is pressed. So far it has only been tested on a simulated steering: not on a tractor yet.
- **Tuning on the line.** AutoSteer page › Full › "Tune on Line": with AutoSteer on a long straight line at working speed, it moves the line 30 cm to the right and back for each value it tries and times how fast the tractor gets onto it, without swinging past. It proposes Steer Response (Pure Pursuit) or Aggressiveness and Overshoot Reduction (Stanley). The values tried steer only while it measures; the settings change only on "Use these values". Tested on the simulator, not on a tractor yet.
- **Moving a field.** A field drawn on a satellite photo is often a metre or two off the ground. Line the tractor up with the drawing with GPS Offset Fix, then press "Move the field instead": the boundary, obstacles (a tree stays a circle), headland, photo and, if chosen, the tracks move by the opposite of the offset, the offset goes back to 0 and the field is saved that way. Flags, recorded paths and the worked area stay where the GPS put them.
- **Long curves on an older PC.** With a boundary curve and its extra guidelines, the map drew 7 frames a second on an older PC (Intel HD 4000): the screen froze, and once AutoSteer went off ("presence lost"). Long lines are now drawn with only the points that show and the guidelines all in one go, and a slow map always leaves time for the messages from AgOpenWeb. An extra guideline that doesn't enter the field (round a boundary curve, the rings outside the fence) is not drawn.
- **Web cards.** ⚙ › "Search the network" finds the modules' web pages: first the ones that announce themselves by mDNS, then by asking each address on the local network; a module found that way is then asked for its own name, so it is listed by name even where the network loses mDNS on its way to the Wi-Fi. Each page has a zoom button, and the card's header buttons are big enough to hit in a moving cab.
- **Screenshot in a bug report.** After "Create Bug Report", "Take Screenshot" adds a picture of the whole AgOpenWeb screen as it is at that moment to the report as a JPEG, without the Bug Report panel over it.
- **Field data card beside the right buttons.** The card that opens from the field line in the top bar (Total, Worked, Remaining... Reset trip) opens next to the buttons on the right, just under the U-turn arrow, not over the map ahead of the tractor. The U-turn arrow and its distance stay where they are and in sight.
- **Speeds in km/h.** Speeds, and the messages that mention one, read "km/h" as they did before the official project changed them to "kph" (#275). Their translations still apply.
- **Smaller bug reports.** The Bug Report panel leaves the field's background photo out of the report unless "Include the field's background image" is ticked: it can be 10 MB or more and is rarely what a bug is about. A note in the report says it was left out.

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
