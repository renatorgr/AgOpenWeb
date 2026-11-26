# AgOpenWeb with a sprayer readout

This is a fork of [AgOpenGPS-Official/AgOpenWeb](https://github.com/AgOpenGPS-Official/AgOpenWeb).

- It adds a **sprayer readout**: boom pressure, flow and alarms from the sprayer's machine module, shown in a strip above the section buttons.
- It also carries [some changes that the official version doesn't have yet](#other-changes-in-this-fork).
- Everything else is the official AgOpenWeb `develop`, brought in by a pull request each day.

The official README follows [below](#agopenweb).

## Sprayer readout

What it shows:

- The pressure (bar), the flow (L/min), the actual rate (L/ha), and one small square per section in the section buttons' colours.
- An alarm, with a sound, for low or high pressure, for a flow that doesn't match the open nozzles, and for flow with every section closed.
- A warning when the module doesn't open or close a section that AgOpenWeb asked for.
- With a target rate set, the pressure that gives that rate at the current speed. It only advises: nothing is regulated.
- The strip only appears while a sprayer module is sending data.

Where the settings are:

- The settings (target rate, nozzles, pressure limits, flow tolerance) are on the module's own web page ([Sprayer_ESP32](https://github.com/renatorgr/Boards/tree/main/Sprayer/Sprayer_ESP32) 0.10 and newer, Settings tab), not in AgOpenWeb.
- The module works out the rate, the pressure for the target and the alarms; AgOpenWeb shows and sounds what it sends, as the official AgOpenWeb leaves the rate settings to the rate controllers.
- The section widths stay in AgOpenWeb (Tool tab).

What the module sends:

- PGN 160 to port 9999 once a second, then (Sprayer_ESP32 0.10 and newer) PGN 161 with ISOBUS process data (DDIs), see [Docs/PGN.md](Docs/PGN.md#sprayer-readout).
- An older program sends PGN 160 only: the strip shows its pressure, flow and sections, without a rate, a target or alarms.
- Firmware is in [renatorgr/Boards](https://github.com/renatorgr/Boards/tree/main/Sprayer): Sprayer_ESP32 for an ESP32 relay board, and the older Sprayer_Nano for an Arduino Nano with a relay board.

The readout was proposed to the official project in [AgOpenGPS-Official/AgOpenWeb#390](https://github.com/AgOpenGPS-Official/AgOpenWeb/issues/390). The maintainers would rather handle sprayer data the ISOBUS way, so it lives in this fork for now.

## Other changes in this fork

Most of these have been offered to the official project too. When one is merged there, it comes off this list.

### Obstacles and U-turns

**Obstacles.** In the boundary list, each obstacle gets its own choice: a U-turn before it, Go around (the pass bends round it), or Drive through.

- Trees can be added as circles with "+ Circles".
- Other shapes are drawn with "+ Polygon": tap the corners on the map, double-tap the last one to close it. Drawing goes on with the next obstacle until Finish with no corners tapped, or Cancel.
- Small obstacles can be made round: "Round all" rounds every obstacle up to 10 m across; a bigger one keeps its shape.
- "Size all" gives every circle up to 10 m across the diameter in the box.
- Obstacles ticked in the list (the box in front of each, or the one in the title for all) are outlined red on the map and deleted together with one confirmation. The outer boundary is never deleted that way.
- The Go around curve radius and safety margin are on the U-turn tab.
- Go around is still being tested: don't use it with AutoSteer on a real tractor yet.

**Go around beside the fence.** A pass that clipped one obstacle and, a few metres before the fence, the tip of another was steered straight through both, while the U-turn was planned as if the pass went round them. So there was no turn before the first obstacle, and AutoSteer went off with the alarm in front of it.

- Now the line steered and the U-turn get the same bend for a pass.
- A bend that is back on the pass at the fence but for its last millimetres is made.
- An obstacle with no room on either side no longer takes away the bend round the obstacle before it.
- Simulator only, not tried on a tractor yet.

**Headland gaps.** With "Drive across a headland gap up to" set on the U-turn tab, a pass that crosses a narrow tip of the field is driven on with the sections off, and the turn is made at the far end.

**U-turns with no room.**

- When no proper turn fits, the simple turn is moved back into the field instead of crossing the boundary.
- Where the headland clips a corner off the pass, AutoSteer drives on across it and says so (a thin spike of the headland line doesn't count).

**U-turn beside a fence that comes in.** With a hard boundary, a turn sometimes had to be moved back to keep the implement off the fence at the end of the pass.

- It could then be made far too early instead, where the fence comes in beside the pass: it landed on the next pass inside its crop and left a strip of it unsprayed.
- Now it is moved back from the end of the pass only.

**Skip worked passes.**

- A pass more than half done but left short of the end the tractor is at (its 20 m next to that end mostly not covered) counts as still to do there, so the turn can go onto it.
- When the order's next pass is more than four passes over (the ones in between were done before), the turn goes onto the nearest pass still to do instead of along the headland to it, and the order starts again from there.
- A pass counts as done by what is painted on the map, not by a section having been on somewhere on it.
  - Where a bay cuts the passes, doing the short piece no longer marks the whole pass done, and the long piece gets its turn from the other end.
- With the headland on and its sections off, a pass lying only in the headland band is left to the headland laps. It no longer hides the passes still to do (that gave "End of field reached" with crop left).
- A manual U-turn and AutoSteer joining a pass from the headland count done passes the same way.
- The number of rows skipped now counts in this mode too: with 3 rows the turn goes four passes over, and the passes left are done on the way back. Before, it always skipped 1 row, so changing the number did nothing.
- (The turn onto the nearest pass with room is official since #807.)

**Manual U-turn beside a hard boundary.** With the implement already nearer a hard boundary than the U-turn distance (a pass along the fence), the manual U-turn button did nothing, even to turn away from the fence.

- Now the turn is made as long as the implement stays inside the boundary.
- (The message saying why a U-turn was refused or missed is official since #804.)

**U-turn skip kept per field.** The skip button's mode (Normal, Alternative, Ignore worked tracks) and the number of rows are saved with each field.

- They come back when it opens, also after AgOpenWeb was closed.
- A field never changed opens in Normal.

### AutoSteer and hard boundaries

**AutoSteer and a hard boundary.**

- At the end of a pass with no U-turn there (no pass with room at this end, no turn that fits, U-turn off), AutoSteer goes off with the alarm a metre into the headland.
  - So there is the headland to turn by hand. Before, it steered on through the fence.
- This also works on a pass bent round trees in the headland (Go around), which leaves the crop at an angle.
- With no U-turn planned, when an end of the implement is heading out across a hard boundary (AutoSteer joining a line from a corner, for example), AutoSteer goes off with the alarm.
- It looks along the curve the tractor is steering, so on the first lap of a boundary curve it no longer goes off at a bend with the tractor on the line.
- A U-turn planned for one end is dropped when the tractor turns round on the pass, so these still work at the other end.

**AutoSteer flickering on and off.** With the steer switch / button set to Button, the app sometimes took AutoSteer off just as it went on (a hard boundary warning, for example).

- The steer module's answer to the engage turned it on again, and it went on and off for seconds with the alarm.
- The app now ignores the module's "steering" for 1 s after it takes AutoSteer off itself.

**No line outside a hard boundary.**

- A pass that lies wholly outside a hard boundary (a lap of a boundary curve outside the fence, say) is no longer shown when the tractor is outside the field with AutoSteer off: the nearest pass inside is shown.
- Moving the line onto such a pass is refused with a message.

**Moving onto a line beside a hard boundary.** This is for AutoSteer turned on, or the line moved or changed, with the tractor off the new line and the implement near a hard boundary.

- AutoSteer now drives a smooth S onto the line that keeps the implement's ends inside the fence.

- A front implement swings out ahead of the tractor as it straightens, so its S is longer: an 8 m front boom 4.5 m ahead, moving one pass at 8 km/h, about 60 m; a rear implement about 20 m.
- A tractor already heading steeply towards the line is first turned along it.
- The S is drawn on the map.
- When no S fits (the end of the field is too near), AutoSteer goes off with the alarm and says to move over by hand.
- Checked by replaying a simulator drive, then used in the field on a tractor with an 8 m front boom.

**Joining a new line from the headland.** U-turn tab › "Joining a new line in the headland": Nearest point (as before), Ask, or Start of the pass. It acts when AutoSteer is turned on, or the line is moved or changed, with the tractor in the headland facing the field.

- **Start of the pass** drives along the headland to where the pass begins and puts the tractor straight on it a set distance before the crop (3 m by default).
- **Ask** draws that way, sounds and shows two buttons, and keeps the nearest point until one is pressed.
- The way stays in the headland at the U-turn radius, keeps the implement off a hard boundary and out of obstacles. When it doesn't fit, a message says so and the nearest point is used.
- With the tractor facing out of the field, Start of the pass and Ask turn it round through the headland when there is room.
- In any mode, AutoSteer turned on in the headland facing a hard boundary, with the line ahead running out of the field, goes off at once and says to turn the tractor to face the field.
- Tested on the simulator, not on a tractor yet.

### Boundary curves and front implements

**Boundary laps through a narrow part.** On a closed boundary curve, an inner lap that can't keep its distance from the fence where the field narrows (a neck narrower than twice the lap's distance) was joined straight across, along the fence.

- Now that stretch isn't drawn, and AutoSteer goes off with the alarm 10 m plus 2 s before it, so an outer lap can be taken there or the tractor steered by hand.
- A passage only a little too narrow, where the lap stays nearer its own place than the next lap out's, is driven as before.
- Not tried on a tractor yet.

**Boundary laps at the seam.** Where a closed boundary curve starts and ends, an inner lap ran 20 cm past the seam and back, so a tractor a little off the lap there could be turned round by AutoSteer.

- The lap now goes straight on through the seam.
- Where the seam is at the tip of a narrow strip cut into the field, the lap also no longer goes out across the fence round the tip and crosses itself.

**A front implement round bends.** AutoSteer steers the tractor's rear axle on the line, so an implement fixed ahead of the tractor ran outside the line on every bend.

- 4.5 m ahead: 0.2 m on a 50 m bend, 0.5 m on a 20 m bend, 1.3 m on an 8 m bend. That left gaps on the inside and overlaps on the outside.
- Now, with a front-fixed implement, the tractor steers a little inside each bend so the implement runs on the line. Never tighter than the tractor can turn.
- Checked by replaying a simulator drive, then used in the field on a tractor with an 8 m front boom. It can still be improved.

**A front implement where a bend starts and ends.** With the tractor steering inside each bend, the implement still ran 10 to 25 cm off the line where a bend starts and where it ends. AutoSteer aims at a point ahead, so it starts each bend a little early and comes out of it early, and an implement ahead of the tractor swings out by that.

- Now, once the tractor is on the line (within 1 m and 20°), AutoSteer sets the wheels for the bend where the tractor is, plus a small correction back to the line.
- In the simulator: an 8 m front boom 4.9 m ahead went from 9 to 2 cm off at the start and end of a 20 m bend; a 35 ft combine header round a 10 m bend from 25 to 9 cm.
- Joining a line from the side is steered as before. A rear or trailed implement is not changed.
- Simulator only, not tried on a tractor yet.

**The green path with a front implement.** The green path of a U-turn, and of the way onto a pass, was drawn where the tractor's rear axle goes. An implement fixed ahead of the tractor swings wider in the turn, so the path on the map was not where the implement went.

- Now, with a front-fixed implement (a front boom, a combine's header), the green path is drawn where the middle of the implement goes. An 8 m boom 4.7 m ahead on a 5.4 m turn: 1.8 m further out.
- Only the drawing changed. The turn is still worked out for the rear axle, unless the setting below is on.
- A rear or trailed implement is drawn as before.
- Simulator only, not tried on a tractor yet.

**U-turns worked out for a front implement.** A U-turn is worked out for the tractor's rear axle. An implement fixed ahead of the tractor then swings outside the turn: past the turn line, past the next pass, and back onto it with a sudden change of direction the steering can't follow.

- New setting on the U-turn tab, shown with a front implement: *U-turns planned for* The tractor (as before, and the default) or The front implement.
- With The front implement, the turn is the path of the middle of the implement. It stays inside the turn line and goes from one pass onto the next without crossing it. The tractor follows behind and never turns tighter than the U-turn radius.
- The manual U-turn and the way to the start of a pass are worked out the same way.
- What it costs: the tractor starts turning sooner, about the implement's distance ahead of the rear axle earlier. Rows closer together than the implement's turning circle get a wider turn. The implement comes square to the next pass over about three times that distance.
- Where such a turn does not fit (too close to a fence or an obstacle), the turn is worked out for the tractor as before, so there is never no turn where there was one. A K-turn is always worked out for the tractor.
- In the simulator, an 8 m boom 4.7 m ahead on a 5.4 m turn: worked out for the tractor, the middle of the boom went 1.75 m past the turn line and 1.7 m past the next pass; worked out for the boom, it stays inside both.
- Simulator only, not tried on a tractor yet. Off unless you turn it on.

### Fields

**Closing a field.**

- It also stops a recorded path that is being driven or recorded.
- With no field open, the section buttons, their hotkeys and the work switch do nothing, so no section can come on with no buttons on the screen to turn it off.

**Moving a field.** A field drawn on a satellite photo is often a metre or two off the ground.

1. Line the tractor up with the drawing with GPS Offset Fix.
2. Press "Move the field instead".
3. The boundary, obstacles (a tree stays a circle), headland, photo and, if chosen, the tracks move by the opposite of the offset. The offset goes back to 0 and the field is saved that way.

Flags, recorded paths and the worked area stay where the GPS put them.

### Simulator and tuning

**Bench button.** For a bench test (a steering motor on the desk) the simulator bar has a **Bench** button, shown while a steer module answers.

- It asks first, turns red, and lets AutoSteer on the simulator move the steering.
- It is never saved and goes off with the simulator or when the module stops answering.
- The official project keeps the simulator from steering, without the button.

**Simulator 10x is time, not speed.** The simulator bar's 10x now runs time ten times faster at the speed shown.

- So a field is worked in a tenth of the time with the same U-turns, guidance and sections as at 1x.
- Before, it drove the tractor ten times faster (100-250 km/h), and the U-turns were not made.
- Proposed to the official project in [#726](https://github.com/AgOpenGPS-Official/AgOpenWeb/issues/726).

**Holding the line when RTK is lost.** Without corrections (no mobile network in a valley) the receiver's position jumps half a metre to two metres sideways, and AutoSteer pulled the tractor across.

- Now the jump is measured and taken out, so the tractor stays on its pass for up to 3 minutes.
- When RTK is back, the line goes back to the true position slowly, with no pull on the steering.
- The fix label shows how much is held ("GPS Fix, held 0.9 m").
- Vehicle › GPS › "Hold the line when RTK is lost", on by default.
- Tried on a tractor with RTK cut on purpose (18 Nov 2026): the tractor stayed on its pass.

**Steer Wizard: tuning the wheels.** A new step after the Ackermann circle finds Minimum To Move, Max Limit and Proportional Gain by itself.

- It first checks that the stop works, then moves the wheels a few degrees while the tractor stands still.
- Nothing changes until "Use these values" is pressed.
- Run on a tractor on 17 and 18 Nov 2026, standing still and at walking pace. The Max Limit rules came from those runs.

**Tuning on the line.** AutoSteer page › Full › "Tune on Line".

- With AutoSteer on a long straight line at the speed you work at, it moves the line 30 cm to the right and back for each value it tries, and times how fast the tractor gets onto it without swinging past.
- Start can be pressed early: the first value starts by itself once AutoSteer holds the line.
- It proposes Steer Response (Pure Pursuit) or Aggressiveness and Overshoot Reduction (Stanley), and says the speed they were found at: measure again when you work faster.
- The values tried steer only while it measures; the settings change only on "Use these values".
- Run on two tractors at 3 to 6 km/h; what was changed after those runs is tested on the simulator only.

### Screen

**Long curves on an older PC.** With a boundary curve and its extra guidelines, the map drew 7 frames a second on an older PC (Intel HD 4000): the screen froze, and once AutoSteer went off ("presence lost").

- Long lines are now drawn with only the points that show, and the guidelines all in one go.
- A slow map always leaves time for the messages from AgOpenWeb.
- An extra guideline that doesn't enter the field (round a boundary curve, the rings outside the fence) is not drawn.

**Web cards.** ⚙ › "Search the network" finds the modules' web pages.

- First the ones that announce themselves by mDNS, then by asking each address on the local network.
- A module found that way is then asked for its own name, so it is listed by name even where the network loses mDNS on its way to the Wi-Fi.
- Each page has a zoom button, and the card's header buttons are big enough to hit in a moving cab.

**Field data card beside the right buttons.** The card that opens from the field line in the top bar (Total, Worked, Remaining... Reset trip):

- opens next to the buttons on the right, just under the U-turn arrow, not over the map ahead of the tractor;
- the U-turn arrow and its distance stay where they are and in sight;
- with a bigger UI size or a short screen, where it would cover the roll gauge and the zoom buttons, those go under it while it is open (side by side if one above the other does not fit).

**Rate on the field data card counts the sections that are on.** The card's Rate (and the Rate in the field line) is the speed times the width of the sections that are on.

- So it reads 0 while every section is off (red, or Auto and off in the headland) and half with half the boom on.
- Before, it was always the whole implement, as in AgOpenGPS, even with nothing being worked.
- Trip area already counted only the ground painted by sections that are on.
- Time left still uses the whole implement.

**Speeds in km/h.** Speeds, and the messages that mention one, read "km/h" as they did before the official project changed them to "kph" (#275). Their translations still apply.

### Bug reports and the log

**Who a bug report goes to.** "Send Report" goes to the official AgOpenWeb maintainers (an issue on AgOpenGPS-Official/AgOpenWeb), not to Renato, and only they can download the report.

- So after "Create Bug Report" the panel asks who it is for: **Renato (this version)**, the first choice, or **Official maintainers**.
- For Renato: "1. Save the Report" writes the title and description typed in the panel into the zip and opens it in the browser to be saved on the device (the browser may ask where). "2. Open a Telegram Chat with Renato" (@renato_rgr) opens the chat to send it in.
- For the official maintainers, the panel says that is not Renato, then works as before.
- Screenshots and files added in the panel go into the zip either way.

**Screenshot in a bug report.** After "Create Bug Report", "Take Screenshot" adds a picture of the whole AgOpenWeb screen as it is at that moment to the report as a JPEG, without the Bug Report panel over it.

**Smaller bug reports.** The Bug Report panel leaves the field's background photo out of the report unless "Include the field's background image" is ticked.

- It can be 10 MB or more and is rarely what a bug is about.
- A note in the report says it was left out.

**What the operator was told, in the log.** Each of these is written to the log as a "[Screen]" line, so a bug report says what was on the screen and what sounded:

- each message shown on the screen ("[Screen] Toast: RTK lost: the 0.3 m jump...");
- each alarm sound: RTK lost, U-turn, boundary, headland, sprayer ("[Screen] Sound: RtkLost");
- each module message;
- each dialog, with the button pressed.

The same line again within 30 s is written once.

**Steer wizard results in the log.** The steer wizard's motor test, maximum steering angle test and Tune steering write what they found to the log as "[Steer wizard]" lines:

- the PWM that first moves each side;
- the wheel speed at each PWM;
- each gain tried;
- the locks reached.

So a bug report holds the numbers without a screenshot of each result.

**Network links in the log.** The log names the computer's network links and their addresses when AgOpenWeb starts and each time they change ("[Network] links now: Ethernet 192.168.5.10/24 (were: ...)"). So a bug report says which link dropped when the modules' data stopped and came back.

## Download

Get the [Beta build of this fork](https://github.com/renatorgr/AgOpenWeb/releases/tag/beta).

- It has the same files as the official Beta (see "Download & run" below).
- Its title shows when it was built and from which commit.
- Links below that point to AgOpenGPS-Official give you the official build, without the changes above.

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

This is done by a scheduled Claude Code session, not by a GitHub Action:

- an Action can't resolve a conflict;
- it may not change workflow files;
- and a pull request it opens starts no CI.

Don't use "Sync fork" or "Discard commits" on `pulverizador`.

A GitHub Action ([`sync-develop.yml`](.github/workflows/sync-develop.yml)) only keeps `develop` a copy of the official one. Its run fails when `pulverizador` has fallen more than three days behind, which means the daily update has stopped.

Tags:

- The `beta` tag belongs to the beta release: leave it alone.
- The `v…` tags are copies of the official ones.
- Don't push tags to this fork from a computer: a `v…` tag pushed here starts a release build.

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
