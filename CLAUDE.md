# CLAUDE.md - AgOpenWeb3

This file provides guidance to Claude Code when working with this repository.

**Key documentation:**
- **[Docs/ARCHITECTURE.md](Docs/ARCHITECTURE.md)** - Full architecture: services, state management, data flow
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Contributor guide with cross-platform parity rules
- **[PGN.md](PGN.md)** - UDP packet protocol for hardware communication
- **[Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md)** - UI strings and Weblate
- **[Docs/GPS_RECEIVERS.md](Docs/GPS_RECEIVERS.md)** - Receivers, sentence families, the bridge contract, per-receiver setup
- **[Docs/WEB_CLIENT.md](Docs/WEB_CLIENT.md)** - The web client's files (`js/`, `css/`), how the scripts load, adding a panel
- **[Docs/ADD_ON_CARDS.md](Docs/ADD_ON_CARDS.md)** - For add-on developers: web cards and VT cards, gateways, discovery
- **[Docs/FIELD_FILE_FORMATS.md](Docs/FIELD_FILE_FORMATS.md)** - `field.geojson`, sidecars and coverage tiles: the spec for tools that read or write fields

## Project Overview

AgOpenWeb3 is a cross-platform agricultural GPS guidance application: a .NET guidance host
with a web UI. It's a clean rewrite of AgOpenGPS with the backend shared across every
platform and the UI a single web client.

**What it does:**
- Real-time GPS guidance for agricultural equipment
- Field boundary management and recording
- Unified track guidance (AB lines and curves use same system)
- U-turn path generation and following
- Section control for sprayers/planters
- NTRIP RTK corrections support
- Configurable keyboard hotkeys
- Integration with AgOpenGPS ecosystem via UDP

## Architecture

**The web client is the only UI.** `AgOpenWeb.RemoteServer` embeds a web server and serves
the `wwwroot` CanvasKit PWA (`index.html`, the client in `js/`, its styles in `css/`, `transport.js`,
`i18n.js`; map in `Docs/WEB_CLIENT.md`). Every head
boots the same guidance backend (`AgOpenWeb.RemoteWiring.WebBackend`: the `MainViewModel`
"brain" on a `HostLoopDispatcher`, plus the embedded `RemoteServerHost`) and shows that page
in the platform's own web view; any browser on the LAN can open `http://<host>:5174` too.
There is no native UI framework in the heads (Avalonia was removed in October 2026; the
`VehicleSimulator` dev tool is the one Avalonia app left).

```
AgOpenWeb3/
├── Shared/                              # Platform-agnostic code
│   ├── AgOpenWeb.Models/            # Data models, geometry, configuration, DTOs
│   ├── AgOpenWeb.Services/          # Business logic, GPS, NTRIP, UDP, audio (.wav embedded)
│   ├── AgOpenWeb.ViewModels/        # MainViewModel: the headless control brain; no native UI
│   ├── AgOpenWeb.RemoteServer/      # Embedded web server + wwwroot web client (the UI)
│   └── AgOpenWeb.RemoteWiring/      # WebBackend host core + RemoteServer↔VM command/projector wiring
│
├── Platforms/                           # Thin web-view shells + platform services
│   ├── AgOpenWeb.Desktop/           # Windows/macOS/Linux: Photino.NET launcher window, or headless daemon
│   ├── AgOpenWeb.iOS/              # iOS/iPadOS: UIApplicationDelegate + WKWebView
│   └── AgOpenWeb.Android/          # Android: Activity + android.webkit.WebView, foreground BackendService
│
├── Simulators/
│   ├── AgOpenWeb.VirtualModules/    # Virtual GPS/steer/machine UDP modules (tests + bench)
│   └── AgOpenWeb.VehicleSimulator/  # Standalone Avalonia dev tool (not shipped)
│
├── Tests/                              # NUnit test projects
│   ├── AgOpenWeb.Models.Tests/     # Geometry, coordinate conversion
│   ├── AgOpenWeb.Services.Tests/   # NMEA parsing, guidance, coverage, pipeline
│   ├── AgOpenWeb.ViewModels.Tests/ # ViewModel/control-brain logic
│   ├── AgOpenWeb.RemoteServer.Tests/ # Web host: satellite tiles, imagery capture
│   └── AgOpenWeb.IntegrationTests/ # Test-support lib: fake settings etc. (used by Services.Tests)
│
├── Tools/                              # Dev scripts (i18n-extract.py, unpack-dump.sh, ...)
├── deploy/                             # Packaging per OS (package.sh, installers, READMEs)
├── TestRunner/                         # Legacy test harness for guidance algorithms
└── AgOpenWeb.sln                    # Solution file
```

### Platform Support

| Platform | Project | Notes |
|----------|---------|-------|
| Windows | AgOpenWeb.Desktop | Launcher window (WebView2) by default, or Windows Service (headless) |
| macOS | AgOpenWeb.Desktop | Launcher window (WKWebView) by default |
| Linux | AgOpenWeb.Desktop | Headless systemd daemon by default; `--launcher` window (WebKitGTK) on real hardware |
| iOS/iPadOS | AgOpenWeb.iOS | Requires Xcode 26.3+; TestFlight |
| Android | AgOpenWeb.Android | APK build, sideload install |

### Desktop modes

`Program.cs` picks the mode: `--headless` (display-less daemon: `HeadlessHost`), `--launcher`
(the Photino.NET window: `Launcher/LauncherEntry`), no flag = launcher on Windows and macOS,
headless on Linux. `--launcher --console` shows the supervisor page (start/stop, LAN URL)
instead of the UI, for a box that only serves other devices. The backend is identical in
every mode (`BackendHost`).

## Build Commands

```bash
# Build and run Desktop (works on Windows, macOS, Linux)
dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj            # launcher window
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -- --headless   # daemon; UI at http://localhost:5174

# Build iOS (requires macOS with Xcode 26.3+)
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj -c Debug -f net10.0-ios -r iossimulator-arm64

# iOS on a device: Release signs for the App Store, so for a directly installable optimized build use
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj -c Release -f net10.0-ios -r ios-arm64 \
  -p:MtouchUseLlvm=false "-p:CodesignKey=Apple Development" -p:CodesignProvision=Automatic
# (LLVM off builds in ~3 min instead of ~15; delete bin/obj for ios-arm64 after switching branches)
xcrun devicectl device install app --device <udid> Platforms/AgOpenWeb.iOS/bin/Release/net10.0-ios/ios-arm64/AgOpenWeb.iOS.app

# Build Android APK
dotnet build Platforms/AgOpenWeb.Android/AgOpenWeb.Android.csproj

# Build entire solution
dotnet build AgOpenWeb.sln

# Run tests
dotnet test Tests/AgOpenWeb.Models.Tests/ ; dotnet test Tests/AgOpenWeb.Services.Tests/ ; dotnet test Tests/AgOpenWeb.ViewModels.Tests/ ; dotnet test Tests/AgOpenWeb.RemoteServer.Tests/
```

## Key Design Decisions

### Rendering: the web client
The map is drawn by the web client with CanvasKit (Skia compiled to WebAssembly, bundled in
`wwwroot/vendor` for offline cab use). The host sends state over a WebSocket as compact binary
frames (`WireCodec` on the host, `transport.js` on the client): a 10 Hz `Tick` (pose, cross-track,
sections), the `Scene` (tracks, boundary, imagery extent), `Config`, `Status`, coverage as an
init + incremental cell deltas, and smaller frames for dialogs, prompts, sounds and toasts.
Position between ticks is extrapolated client-side (`RENDER_DELAY`); the pose estimator on the
host does the same for the 100 Hz control loop.
Long lines cost the most to draw: Skia strokes a bent, anti-aliased path through a CPU mask the
size of the path on the screen, so a curve that goes round the field costs a screen-sized mask.
Lines from the `Scene` are drawn with `strokeFixedPtsSk` (`js/map-draw.js`: a copy without the
points that add nothing, made once per array; the host replaces its arrays, the page never edits
them); a line the page draws or edits uses `strokePtsSk`. Lines in one style share one path
(`strokeCmdsSk`), as the extra guidelines do.

### Control authority
Several browsers may be connected; only one holds control (`ControlAuthority`, deadman model).
Every command is dropped unless the sender holds fresh control: the others are observers, who
see everything and change nothing (#397). `ObserverIds` lists the few requests an observer may
send. A client can't take control from a live operator, only fill an empty seat.
The deadman (1.5 s without the holder's presence) runs on a timer of its own. The page sends
presence on a timer and on every frame from the host (`sendPresence` in `js/control.js`): a
minimized window or a background tab gets its timers throttled but still hears the host (#766). When control
ends, `MainViewModel.OnBrowserControlEnded` turns off AutoSteer, Free Drive, the sections (every
one whose own button is not Off: On by hand, or left in Auto with neither master on) and the steer wizard, and the
next screen to take control is told why.
`WebSocketHub` gives each browser its own outgoing queue: a broadcast never waits on a socket,
and a browser that stops reading for 15 s is cut off (it reconnects and gets a fresh seed).
The page drops a link that has carried no frame for 2.5 s and reconnects (`transport.js`).
The app's log (a dump's `logs.txt`) says why a screen went: each browser arriving and leaving with the reason
(closed by the page, cut off, link failed), a deadman with the state of its link, and what the page saw of itself
(`diag.page`: hidden, frozen, its timers stopped by the system, offline, a link given up), sent on its next link.
What a page writes there (its note, the reason it gave for closing) goes in on one line (`WebSocketHub.OneLine`): any
browser may send it, an observer too, and a line break in it wrote lines of its own in the log. Fork only. The logger
does the same for every message (`InMemoryLogger`: CR and LF written as `\r` and `\n`, an exception on its own lines
below), so a track or NTRIP profile named on a screen, or a gateway's name on the network, can't add lines either
(official PR #912, reworked).

### Section control timing
`SectionControlService` runs every section by its own button state (Off / Auto / On); the master
buttons only set them all, as AgOpenGPS. In Auto the look-ahead asks for on/off early: the
**module** gets that request at once (`IsCommandedOn` → PGN 239/229 bits), while `IsOn` and the
coverage map wait the look-ahead time, so the valve acts at the edge and the map paints where
the product lands (#707). With look-ahead off at 0 the turn-off delay is a plain overrun of both. Look-ahead
installs at AgOpenGPS's 1.0 s on / 0.5 s off (`ToolConfig.DefaultLookAheadOn/Off`): the time it
assumes a sprayer's valves take; saved profiles keep their own.
The control loop sends the PGNs at 100 Hz, PGN 229 every tick whatever the section count (the
ISOBUS task controller reads sections from 229 only).
A section away from the fence is "all inside" without the edge tests: `PolygonClearGrid` (10 m cells,
built with the polygon's index) holds, for each cell inside the polygon, how many whole cells lie
between it and the nearest cell an edge runs through, and the quick answer of
`BoundaryPolygon.GetSegmentBoundaryStatus` is given when that is at least the section's half width.
The bounding box moved in 50 m was used before: a notch in the fence, or the missing corner of an
L-shaped field, is deep inside the box, so with *Off outside boundary* off and no headland the
sections stayed on outside the field (a tester's field, the simulator steered by hand across a
60 m notch: 464 m² sprayed outside). Tests: `PolygonClearGridTests`, `SectionInFenceNotchTests`
(Models), `SectionsOffInFenceNotchTests`. Fork only.

### A boundary edit keeps the job's worked area
`MainViewModel.SetCurrentBoundary` gives the coverage map the outer boundary's box (plus 50 m) at every boundary
edit (an obstacle's choice, one added or deleted, another outer boundary). `CoverageMapService.SetFieldBounds`
made a new, empty grid whenever that box was not the grid's, and the grid is not the box once it has grown
(painting within 50 m of its edge adds 250 m, a job's tiles reaching further do the same on loading): the map
went blank and the next autosave deleted every tile of the job. A tester's sprayer job lost the day's passes to
one obstacle deleted (Couto da Ribeira, 20 Nov 2026). With worked area on the grid, `SetFieldBounds` now grows
the grid to hold the box too and keeps what is painted (`GrowBoundsToHold`, `ResizeKeepingCoverage`, the copy
painting near the edge uses); an empty grid takes the box as before, and a field closing clears the bounds first
(`ClearFieldBounds`), so another field starts empty. The last boundary deleted with worked area on the map keeps
the grid as well. Tests: `CoverageBoundaryEditTests`, `DeleteFieldAndBoundaryTests`.

### Data quality: one monitor, many sensors
AgOpenWeb runs on many set-ups (a tablet on a cable, a board with the app on it, a phone as a second
screen), so it judges the data it is given instead of trusting the set-up: the data is fit to use or it is
not, whatever the cause. Everything is in `Services/DataQuality`. A **sensor** (`IDataSensor`) watches one
thing and says what it found (`DataReading`, `DataFinding`); it never repairs, drops or smooths data. The
**monitor** (`DataQualityMonitor`, built in `MainViewModel.DataQuality.cs`) reads every sensor once a second
and does the rest the same for all of them: the operator is told once when a sensor goes faulty (a toast,
and once more when it is good again), the log says `[Data] ...`, the status frame carries every sensor (the
status bar's fix rate goes red, System Data lists them under Data quality) and the bug report's
`system_info.txt` has a `Data quality` block. **A new check is a new sensor**: implement `IDataSensor`, add
it where its data passes (`GpsSentenceMonitor.DataSensors`, `IUdpCommunicationService.DataSensors`), and give
its name and finding a text in `js/network.js`; no new alarm, frame or dump code.

The sensors:
- `StreamQualityMonitor`: does a periodic stream arrive as it is sent? The GPS fixes, PGN 253, 211 and 240
  (PGN 237 is measured only). Over 10 s the usual wait is the median and the mean is the window over the
  count: equal when nothing is lost, the median staying at the sender's interval when packets are lost (the
  share missing is `1 - median / mean`), and falling to nothing when they come in bursts (Wi-Fi). 10 %
  missing for 15 s is a fault, under 5 % for 15 s ends it (#922).
- `FixTimeSensor`: is every position newer than the one before? It reads the receiver's own time on each
  position sentence (PANDA, PAOGI, KSXT, GGA, GNS, HPR; each kind against its own last one). A position
  that comes a second time or late reads as a reverse and takes AutoSteer off; it is what a module heard on
  two links looks like (cable and Wi-Fi, #900), and it does not show in how the fixes arrive. A good link
  gives none, so 5 in a minute is a fault and a minute without any ends it. A sender that emulates a
  receiver must put an advancing time on its sentences (the scenario rigs do).
- `FixRateSensor`: does the receiver send as often as the app is set up for (`Connections.GpsUpdateRate`,
  10 Hz)? It takes the sender's own rate from the arrival sensor (the usual wait, so not what a link
  loses): a stream at half the rate arrives "evenly". Under 80 % of the expected rate for 15 s is a fault.
- `RefusedSentenceSensor`: do the sentences arrive whole? A sentence the app decodes that fails its checksum
  or whose fields do not read was damaged between receiver and module. 5 in a minute is a fault (the rule
  the counting sensors share, `RecentEvents`). A kind the app does not decode is not counted.

One message per cause: a sensor may name another that sees the cause of what it sees (`ExplainedBy`), and
is not reported while that one is faulty. A damaged sentence is also a position that did not arrive, so the
GPS arrival sensor yields to `RefusedSentenceSensor`.

**A warning only for now**: nothing is switched off, and nothing is repaired. Faulty data is put right where
it comes from (the cable, the set-up), never filtered or smoothed in the app.

### The simulator never steers the module
The steer module stays on the network while the simulator runs. While the simulator is the chosen GPS
source (`GpsPipelineService.SetSimulatorEnabled`, from the moment it is chosen) `VehicleState.SimulatorHoldsSteering`
is set (`IAutoSteerService.SetSimulatorHoldsSteering`) and `PgnBuilder.BuildAutoSteerPgn` sends status 0:
the module would open the lock valve and steer a parked tractor to a simulated line. AutoSteer stays
engaged in the app, so the simulator follows the line, and the red "module not steering" button is not
shown. Turning the simulator on or off (the receiver's fix arriving does it too) takes AutoSteer off
first. Free Drive is the operator's own test of the steering and still sends status 1; PGN 239/229
(sections, lift) still go to the machine module, as a bench test of a sprayer needs. The hold is the
app's simulator, not the label on a fix: the scenario rigs feed their own fixes as
`GpsSentenceType.Simulator` with the simulator off, and are steered.

**Bench choice** (a steering motor on the desk): the sim bar's *Bench* button (`sim.driveSteering|1|0`,
`MainViewModel.IsSimulatorDrivingSteering`, `GpsPipelineService.SetSimulatorSteersModule`) lets AutoSteer
on the simulator send status 1. Shown only with the simulator on and a steer module answering, asked
before it goes on, red while on (`statusBar.simDrivesSteering`). From a screen it is taken only with a steer module
answering then (`MainViewModel.RequestSimulatorDrivesSteering`). Never saved; dropped when the simulator
is turned off and when the steer module goes silent; a change takes AutoSteer off first. The web client
harness reads PGN 254 on the simulator, so its rig makes this choice (`WebClientRig`). Fork only.

### The steer module's switch
With the steer switch / button set (`AutoSteer.ExternalEnable`), PGN 253's steer bit turns AutoSteer on and off
(`ModuleCommunicationService.CheckSwitches`, as AgOpenGPS). The module's bit also follows the engage the app sends
it, so for `MainViewModel.ModuleAnswerSeconds` (1 s) after the app took AutoSteer off for a reason of its own (a
guard, the screen button), the module reporting "steering" is its answer to that engage, not the operator
(`IsModuleAnsweringTheApp`): a guard going off as AutoSteer went on was followed by 13 s of on and off, two sounds
each time (Button, a bench module with the simulator driving it). After the module's own stop (its button, a
kickout) the next press counts at once. Fork only.

### A module heard on two network links
An installation fault (a tablet on cable and on Wi-Fi to the modules' router): every broadcast comes once per link,
the Wi-Fi copy late (1.49-1.73 s, 8 every 205 ms, half lost, measured on a tester's router 22 Nov 2026), and on a
tester's Puma (20 Nov 2026) late fixes read as reverses took AutoSteer off over and over. The app refuses to work
with it, as Chris asked on official #900: `UdpCommunicationService` asks each socket but the loopback one for the
link a datagram came in on (IP_PKTINFO, `BeginReceiveMessageFrom`), and `TwoLinkDetector` (`Services/Network`)
finds a module on two links (10 datagrams in a row on each, no gap over 1 s; never one stray datagram). While it
is, nothing from that module is used and `MainViewModel.UpdateSecondLinks` holds the control-ended safe state
(`OnBrowserControlEnded`); the page shows a box over the map (`#twolinks`) naming both links, and on cable and
Wi-Fi only "turn the Wi-Fi off and keep the cable" (`SecondLink.CableAndWiFi`: module data needs the cable). It
clears 1 s after one link goes quiet. Where the system does not give the link, everything is used, as before.
Docs/ARCHITECTURE.md has the rule, the measurements and the Linux checks. Tests: `TwoLinkDetectorTests`,
`TwoLinksTests`. Same code as official draft PR #900 (branch `fix/udp-second-link-copies`); it replaced the fork's
`LinkCopyFilter` (#296, #316), which dropped the copies and kept working. Fork status frame: the two-link fields
come after `LineTune` (official: after the NTRIP session); at a sync after #900 merges, keep one copy, in the order
the merged codec has.

### The simulator's 10x is a time-lapse
The sim bar's 10x (`IsSimulatorSpeed10x`) runs time ten times faster at the speed shown, not the tractor
ten times faster: at 100-250 km/h the look-ahead, the U-turn trigger and the simulator's steering failed,
and the simulator drove on through the fence (official issue #726). `MotionClock` (`Models/Timing`) is
`Clock.Current` run `SimulatorTimeLapse.Scale` times faster while the simulator and 10x are both on
(`MainViewModel.ApplySimulatorTimeLapse`), and only the vehicle's motion reads it: the estimator's stamp
on each fix (`GpsData.MotionTimestamp`, else `MotionClock.GetTimestamp()`), the 100 Hz control loop's
tick and the render pull's pose (its `RenderPoseMs` stays real: the web client's timeline). NTRIP, RTCM,
module silence and logs stay on real time. `SimulatorTimeLapse.Advance` drives the simulator up to the
motion clock's now in its own 1/30 s steps (`IGpsSimulationService.Tick(steer, dt)`), every third step a
fix stamped with its instant; the pipeline processes those fixes inline, none skipped
(`IGpsPipelineService.SetSimulatorTimeLapse`), and the VM applies each result inline
(`OnGpsCycleCompleted`), so the steps after a fix are steered by it, as at 1x. The section timers count
control ticks, so `SectionControlService.TickHz` is the loop's rate / 10. More than 2 s behind (a slow
PC) the tractor waits instead of jumping. A new backend and `StopBackgroundLoops` put the clock back to
real time (the process outlives the backend on Android and iOS). Tests: `SimulatorTimeLapseUTurnTests`
(the same U-turn at 1x and 10x; the old 10x drove 1.7 km on past the fence), `SimulatorTimeLapseTests`,
`MotionClockTests`. Fork only: at a sync keep the fork side in the simulator files.

### The ground left to paint and the time left
The status bar's Stats page ("Done ... Left ...%") and the field data card's Remaining and Time left come from the
host (fork only). `CoverageMapService.MeasureCoverage` counts the coverage map's 0.1 m cells inside the field's
boundary less its obstacles, and how many are painted (`CoverageAreaCount`: a row of cells at a time, the bits a word
at a time; a 15 ha field in a few milliseconds), every 3 s on a worker thread (`MainViewModel.FieldProgress`). Done
is the ground inside painted, Left what is still to paint: the page took every pass painted (headland passes, overlap,
paint outside the fence) from the headland's area and read "Left -14%" (Tapada da Casa, simulator, 23 Nov 2026). The
time left is that ground at the job's pace (`JobPace`): the ground painted for each second of work, work being the
tractor moving faster than 1 km/h within 30 m of the field, turns included, stops and trips off the field not;
kept in `job.json` (`workSeconds`, `workPaintedSqM`), so a job opened again goes on at its own pace. Its first 2
minutes the speed times the implement stands in, as AgOpenGPS. The instant speed times the width swung by 13 minutes
at 8 km/h ± 1 with 10 ha to do; the job's pace by seconds (`JobPaceTests`). Tests: `CoverageAreaCountTests`,
`JobPaceTests`, `FieldProgressTests`.

**Unpainted in an area** (the field card's button; fork only): the operator taps the corners of an area on the map
(`startAreaDraw` in `js/overlays.js`, as an obstacle drawn on the map: a double tap or Finish closes it), and from
the third corner each tap sends it (`area.select|e,n;...`, `MainViewModel.SetAreaSelection`, kept in
`FieldToolsState.AreaSelection` and drawn from the Scene's `AreaSelection` on every screen). It is counted with the
field, kept to the field (`MeasureCoverage`'s clip: the boundary less its obstacles): its ground in the field, how
much of that is still to paint and the time to paint it at the job's pace, in a panel in the field card's place,
counted again every 3 s as the work goes on, until Close (`area.clear`) or the field closes. Not saved.

### Host ↔ client commands
The client sends `id|arg` strings (`transport.send('track.select|0')`). `RemoteServerWiring`
(in `RemoteWiring`) switches on the id and calls the VM; `RemoteServerWiring.Helpers.cs` holds
the `config.set|key:value` table. Projectors (`SceneProjector`, `CoverageProjector`) build the
DTOs in `Contracts.cs`, `WireCodec` encodes them, `MapBroadcaster` schedules the sends.
A command may be longer than the socket's read buffer (a boundary or an obstacle drawn on the map,
a curve's points saved from the map editor, a bug report's text): `WebSocketHub` joins the pieces of
one message before it is dispatched, up to `MaxCommandBytes` (1 MB; a longer one is dropped whole and
logged). It read into 1024 bytes and took each piece as a command of its own, so a command over
1024 bytes was cut there (the last number too, in the middle) and its other pieces were unknown
commands: an outer boundary of 325 points sent as *Draw on map* sends it became 68 points, 1.36 ha
for 13.24 (Couto da Ribeira, 21 Nov 2026). Tests: `WebSocketHubTests`.

### MainViewModel is the control brain
`MainViewModel` has no view. It runs on the single `HostLoopDispatcher` thread (the "UI
thread" stand-in, `IUiDispatcher`), coordinates services, and is driven only through
`RemoteServerWiring`. It is a large partial class split by domain:

| File | Domain |
|------|--------|
| `MainViewModel.cs` | Core state, constructor, DI, properties |
| `MainViewModel.Commands.Track.cs` | Track/AB line commands |
| `MainViewModel.Commands.Boundary.cs` | Boundary, headland, AgShare commands |
| `MainViewModel.Commands.Fields.cs` | Field open/close/create, jobs |
| `MainViewModel.Commands.Ntrip.cs` | NTRIP profile management |
| `MainViewModel.Commands.Navigation.cs` | View settings, camera, day/night |
| `MainViewModel.Commands.Hotkeys.cs` | Hotkey configuration and dispatch |
| `MainViewModel.Commands.Settings.cs` | App directories, language, reset settings |
| `MainViewModel.Commands.Simulator.cs` | Simulator controls |
| `MainViewModel.Commands.Configuration.cs` | Vehicle/tool configuration |
| `MainViewModel.Commands.Wizards.cs` | Setup wizards |
| `MainViewModel.YouTurn.cs` | U-turn path generation and following |
| `MainViewModel.Guidance.cs` | Guidance algorithm orchestration |
| `MainViewModel.GpsHandling.cs` | GPS data processing |
| `MainViewModel.SectionControl.cs` | Section on/off logic |
| `MainViewModel.BoundaryRecording.cs` | Boundary recording state |
| `MainViewModel.Ntrip.cs` | NTRIP connection management |
| `MainViewModel.Simulator.cs` | GPS simulator state |
| `MainViewModel.ViewSettings.cs` | Display/view settings |
| `MainViewModel.FieldData.cs` | Field data card: trip distance, boundary area |
| `MainViewModel.*.Remote.cs` | Web-client-only flows (field builder edits, headland, KML, tram) |

### ConfigurationStore
`ConfigurationStore` is a reactive singleton holding all runtime configuration (vehicle, tool, guidance, hotkeys, etc.). It syncs to/from `AppSettings` JSON via `ConfigurationService`.

```csharp
ConfigStore.Vehicle.AntennaHeight    // Vehicle config
ConfigStore.Tool.ToolWidth           // Tool/implement config
ConfigStore.Hotkeys.GetActionForKey("A")  // Hotkey lookup
```

### Unified Track Architecture
**Key insight from AgOpenGPS creator Brian:** "An AB line is just a curve with 2 points."

All guidance track types use a single `Track` model (`Shared/AgOpenWeb.Models/Track/Track.cs`):

```csharp
public class Track
{
    public string Name { get; set; }
    public List<Vec3> Points { get; set; }  // AB lines have 2 points, curves have N
    public TrackMode Mode { get; set; }
    public bool IsVisible { get; set; }
    public double NudgeDistance { get; set; }

    // Computed properties
    public bool IsABLine => Points.Count == 2;
    public bool IsCurve => Points.Count > 2;
}
```

**Single guidance service** (`TrackGuidanceService`) handles both Pure Pursuit and Stanley algorithms for all track types. This replaced 4 separate guidance services and reduced ~2,100 lines of duplicated code.

**Shared utilities** in `GeometryMath.cs`:
- `Distance()`, `DistanceSquared()` - various overloads for Vec2/Vec3
- `ToDegrees()`, `ToRadians()` - angle conversion
- `IsPointInPolygon()` - boundary checks
- `PIBy2`, `twoPI` - common constants

### File Format Philosophy
AgOpenWeb may use different/improved formats from AgOpenGPS when it benefits code simplicity or features. Provide **one-way import** from AgOpenGPS formats rather than maintaining full backwards compatibility.

- **Formats**: a field is `field.geojson` (origin, boundaries, headland, tracks, flags, headland lines, background image placement) beside `background.png`, `contours.geojson`, `recorded-paths.geojson` and `elevation.csv`; a job's coverage is world-anchored tiles under `jobs/<task>/coverage/`. Profiles are JSON; the spec for other programs is `Docs/FIELD_FILE_FORMATS.md` (keep it in step with the code).
- **Migration**: AgOpenGPS files found in a field folder are imported once (on open) and **deleted**; AgOpenWeb never writes AgOpenGPS formats. AgOpenGPS 6.8.2+ profiles copied into the data folder (`VehicleProfiles/`, `ToolProfiles/`, `Environment/environment.xml` or, on a fresh 6.8.2+ install, `DefaultEnvironment.xml`, as AgOpenGPS reads them) and AgIO's NTRIP caster (`AgIO/*.xml`; not the one AgIO ships with, and the default only if NTRIP was on there) are converted at start the same way (`AgOpenGpsProfileImport`, one mapping in `AgOpenGpsSettings`, which turns AgOpenGPS's signs and units into AgOpenWeb's; `NtripProfileService.ImportAgIoCastersAsync`), a profile of the same name left alone. An AB line under 10 m (AgOpenGPS's A+ line puts B 1 m from A) is read with B 100 m out along its heading (`Track.LengthenShortABLine`, from `field.geojson`, `TrackLines.txt` and `ABLines.txt`): an offset pass leaves out every point within 1 m of the one before, so it had no pass but the line itself. No compatibility code for files from older AgOpenWeb builds (no installed base yet). See `Plans/Completed/FILE_FORMAT_MODERNIZATION_PLAN.md`.

### Moving a field's drawing
Offset Fix › *Move the field instead* (`field.move|dE,dN,tracks`, `MainViewModel.RemoteMoveFieldAsync`,
`GeoJsonFieldService.MoveField`; fork only) keeps the offset in the field, for a field drawn on a photo
that is off the ground: everything drawn (boundary, obstacles, a tree's circle centre, headland, headland
lines and segments, the background photo with its Mercator box, the tracks unless left out) moves by the
opposite of the offset on the field's plane, the origin stays, and the offset goes back to 0, so the map
looks the same. What the GPS recorded (flags, recorded paths, contours, coverage) stays. The field is
closed (saved), moved on disk and opened again with its job, the simulator put back where it was; refused
with AutoSteer on, a boundary being recorded, or more than 50 m. The Offset Fix itself is not saved: a
restart puts it back to 0.

### A headland along the passes
Field Builder › Headland › Add › *Along the Passes* (`headland.alongPasses|ends,sides`,
`MainViewModel.RemoteCreateHeadlandAlongPasses`, `HeadlandAlongPasses` in `Models/Headland`; fork only): a headland
wide only where the passes end. The crop is every point with at least *Ends* m of field ahead and behind along the
active line's passes and at least *Sides* m from the boundary any way, so an end square to the passes gets the ends'
width, a side along them the sides', a slanted edge the ends' times the sine of its angle to the passes, and a spike
too narrow for a turn is all headland. Worked out on a grid in the passes' frame (0.25 m, coarser past 8 million
cells; marching squares, the largest ring, thinned to 5 cm; under 0.1 s on a tester's 2 km boundary). A segment of type
`AlongPasses`: `Offset` is the ends, `SideOffset` the sides, `PassHeading` the line's when it was made (an AB line, or
an open curve from its first point to its last; with none active, the field's only open line; refused with none). Saved
in `HeadlandSegments.json`, recomputed when the field opens and rebuilt when the boundary changes, as Whole Boundary; it
takes the place of a whole-boundary or along-the-passes segment, and lines drawn per edge stay. The list shows both
widths and changes them (`headland.setOffset` the ends, `headland.setSides`); not point-edited. The pane starts at 3
tool widths for the ends and 1 for the sides. Made because one width all round lost crop at the sides of a tester's
irregular fields, and lines drawn per edge (run straight on past their ends) cut half a field off at a step in its
boundary. Tests: `HeadlandAlongPassesTests` (Models), `HeadlandAlongPassesVmTests`.

### Replacing a field's outer boundary
The boundary menu's *Drive around field*, *Draw on map* and *Build from tracks* each give the field its outer
boundary, and with one there already they took its place without a word (only the KML import asked). On a tester's
field (Couto da Ribeira, 20 Nov 2026) two obstacles driven round with the field's button, which sits beside the
obstacle's, each became the outer boundary: an 11.6 ha field 0.09 ha on the screen, the headland and the turn line
rebuilt on it. With an outer boundary in the field the page now asks first (`askReplaceOuter` in
`js/field-builder.js`: "Replace the outer boundary?", before a recording or a drawing starts) and names the
obstacle buttons; with none it goes on as before. AgOpenGPS asks before its one deliberate replacement, Build from
tracks ("A boundary already exists. Do you want to remove it?", `FormBoundary.cs:438-443`), then clears every
boundary, the inner ones too (`FormBuildBoundaryFromTracks.cs:711`), and its boundary tool clears them all without
asking (`FormBndTool.cs:585`); its drive around, draw on map and KML add to the list, the first entry the outer
boundary and any later one an inner (`FormBoundaryPlayer.cs:140`, `FormMap.cs:193`, `FormBoundary.cs:372`). Asking
before a drive around or a drawing, and keeping the obstacles, are ours. Every new outer boundary is in the log
with what the field had (`MainViewModel.SetOuterBoundary`: `[Boundary] Outer boundary replaced by a drive-around
recording: was 213 points, 11.60 ha; now 39 points, 0.05 ha`), so a bug report says it. The host does not refuse the
command: the scenario harnesses redraw boundaries with it. Tests: `OuterBoundaryReplacedLogTests`. Official since #903.
The menu's buttons and the recorder's have their words under the icon (*Drive round field*, *Drive round obstacle*,
...): they were icons whose words were a title, which a tablet never shows. The recorder's title is *Record Field
Boundary* or *Record Obstacle*, from the host once its recording has started (`MainViewModel.IsRecordingObstacle`,
the Boundary frame's last byte; before that, the button that opened it), where both said "Stop Record Pause
Boundary". Tests: `BoundaryRecorderKindTests`, `BoundaryRecorderPageTests` (Docs/WEB_CLIENT.md, "The screen it is
made for").

### The implement's picture on the map
Tool › Type › *Picture on the map* (`ToolConfig.Picture`, `ToolPicture`, `tool.picture`, saved with the tool profile;
fork only) draws a top view of the implement instead of the 2 m section bars and the hitch line, to scale and at the
tool's real hitch points: *Seed drill* (a trailing tool: the headstock on the tractor from the lower links to the
drawbar pin, Vehicle › Hitch; the drill turning about that pin with the tool's heading, so its working line is where
the map paints) and *Front sprayer* (a front-fixed tool: the boom at the front hitch plus the working point, its tank
on the rear lower links, Vehicle › Rear 3-point hitch). A picture is drawn only with the tool type it is made for;
otherwise, and with *Sections only* (the default), the map is as before. The setting is a list of the pictures made for
the tool type chosen (`IMPLEMENT_PICTURES` in `js/map-draw.js`, one row per picture: its type, draw function, name and
note), so a type with several implements lists them all; a new picture is a `ToolPicture` value, its icons, its draw
function and a row there. Over a picture the sections are a thin strip
where it works (`implementStrip`). Drawing: `implementPictureSk` in `js/map-draw.js`, the strip in `toolFootprintSk`
(`js/map-layers.js`); the pictures are `icons/DrillBody.png`, `DrillHeadstock.png`, `SprayerBoom.png` and
`SprayerTank.png` (200 px per metre, each one's extent in metres in its draw call). Another user's build drew its
implements over the rear tyres; these two are a tester's own machines, drawn from their measures and photos
(24 Nov 2026). A picture is also made for vehicle types (`vehicles` in its row): the drill and the sprayer for a
tractor; a combine carries only its header, *Grain header* or *Maize header* (`grainHeaderPictureSk`,
`maizeHeaderPictureSk`: drawn, not pictures, as wide as the tool and in the vehicle's colour, a row unit every 75 cm,
a feeder house to the combine's body); an articulated tractor pulls big implements, the trailed disc harrow, the
ploughs and the four-rotor rake only so far.
Tests: `ToolPictureProfileTests`, `ImplementPictureListTests` (one row per `ToolPicture` value, RemoteServer).

**Implements drawn from shapes** (fork only, 25 Nov 2026): a manure spreader, a centre-pivot mower conditioner, a quick
disc harrow, a semi-mounted roller, a reversible plough, a trailed and a 3-point disc harrow, a rear side disc mower, a
twin-disc fertiliser spreader, a semi-mounted reversible plough, a reversible disc plough and five rakes (one, two side by side,
two one behind the other, four rotors, and finger wheels in a V; `iRakeRotor`) (`ToolPicture` 5 to 20), drawn in `js/map-draw.js` from rectangles, bars and paths like the
headers, sized from the tool's width and hitch lengths, in metres with y backwards (`implTractorFrame`: what rides on the
tractor, from the rear axle; `implTrailFrame`: a trailing tool from its pin). The part that works (bodies, discs, cutter
bar, beaters) is always on the line the map paints, frames, arms and wheels fitting round it: clamped to look right, the
plough and the mower conditioner were drawn 0.5 m behind the strip at a tester's settings. Colours from the tester's own machines where
he sent photos, else generic; never a maker's name (the mower conditioner's above all). The rear side mower is drawn where
the tool offset puts it (`implSectionsMiddle`), as are the rakes' rotors and wheels. A plough has as many bodies as its width takes at about 40 cm each (a disc
plough a disc every 30 cm), within what is made: 2 to 7 bodies mounted, 5 to 12 semi-mounted, 2 to 6 discs; the front one
behind the rear tyre in the open furrow (`iPloughBodyPlaces`), so the work is 15 to 20 cm off the tractor's middle towards
the ground still to plough on a four-body plough, as on the ground. **The plough and the mower change side by themselves** (`implWorkLeft`): the
side of the ground still to work, from the pass label (↑ A to B, ↓ B to A) and the way the passes last changed (a pass
counts after 10 m on it without leaving a quarter of the tool's width of its line, so a turn by hand that loops over a pass does not,
or at once when a U-turn lands on it; before any change on the track, the U-turn arrow's side; followed on every Tick, a
picture shown or not, and started again on another track or field).
During a U-turn the label names the pass it lands on, driven the other way, so the ploughs turn over (the disc plough's
beam swings across) and the mower swings across as the turn starts (`implSideNow`, eased over about 3 s). Only the picture changes side: the painted strip stays
where the tool settings put it.

**The vehicle's colour on the map** (Vehicle › *Machine brand*, `VehicleConfig.Color` 0xRRGGBB, `vehicle.color` as hex,
saved with the vehicle profile, the Config frame's i32 after the IMU offset; fork only): the body picture of every vehicle
type has its paint drawn in that colour (`vehicleTinted` in `js/map-draw.js`: the picture recoloured once per colour on a
canvas, each pixel scaled by the picture's own paint grey, `VEH_TINT`, so the colour picked is the colour the map shows;
AgOpenGPS multiplies its body texture by `setDisplay_colorVehicle`, which darkens it). The tyres stay black: a pixel darker
than `VEH_TYRE_MAX` inside one of the picture's tyre boxes is left as drawn, as is any coloured pixel (lights, reflectors);
the colour matrix used first painted the articulated tractor's tyres too (the tester, 24 Nov 2026). White, the default,
leaves the picture as it is. The setting is a list of the makers' colours for the vehicle type chosen (`VEHICLE_COLOURS` in
`js/settings-panels.js`, each with where its value comes from) and a picker for any other. A tester's three tractors are
red and he asked for the makers' own colours (24 Nov 2026). Tests: `VehicleColorProfileTests`.

## Technology Stack

- **.NET 10.0** - Target framework
- **Web client** - HTML/JS, CanvasKit (Skia WASM); no framework
- **Photino.NET 4** - Desktop launcher window (WebView2 / WKWebView / WebKitGTK)
- **WKWebView / android.webkit.WebView** - the iOS and Android shells
- **CommunityToolkit.Mvvm** - ObservableObject/RelayCommand in the control brain
- **Microsoft.Extensions.DependencyInjection / Hosting** - DI, daemon lifetime (systemd, Windows Service)
- **SkiaSharp 3.119.4** - server-side imagery work (`BoundaryImageryCapture`, `/backpic.png`); each head references its native package
- **NUnit 4 + NSubstitute** - Testing

## Key Files Reference

| File | Purpose |
|------|---------|
| `Shared/AgOpenWeb.ViewModels/MainViewModel.cs` | Control brain, constructor, DI |
| `Shared/AgOpenWeb.RemoteWiring/WebBackend.cs` | Boots the backend every head uses |
| `Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring.cs` | Web command → VM routing |
| `Shared/AgOpenWeb.RemoteServer/RemoteServerHost.cs` | Embedded web server, routes, static assets |
| `Shared/AgOpenWeb.RemoteServer/SceneProjector.cs` | Host state → DTOs |
| `Shared/AgOpenWeb.RemoteServer/WireCodec.cs` | Binary wire encoding (pair with `wwwroot/transport.js`) |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/js/` | The web client, one classic script per feature (`Docs/WEB_CLIENT.md`) |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n.js` | Client-side translation |
| `Shared/AgOpenWeb.Models/Track/Track.cs` | Unified track model (AB lines + curves) |
| `Shared/AgOpenWeb.Models/Base/GeometryMath.cs` | Shared geometry utilities |
| `Shared/AgOpenWeb.Models/Configuration/ConfigurationStore.cs` | Reactive config singleton |
| `Shared/AgOpenWeb.Services/Track/TrackGuidanceService.cs` | Pure Pursuit + Stanley guidance |
| `Shared/AgOpenWeb.Services/YouTurn/YouTurnStateMachine.cs` | U-turn planning and following |
| `Shared/AgOpenWeb.Services/Coverage/CoverageMapService.cs` | Coverage detection + display grids, tiles |
| `Shared/AgOpenWeb.Services/Ntrip/NtripSession.cs` | NTRIP connection: request, reply, retry, timeouts, status |
| `Shared/AgOpenWeb.Services/NtripClientService.cs` | NTRIP RTK corrections: frames the stream, forwards it to the GPS module |
| `Shared/AgOpenWeb.Services/ConfigurationService.cs` | AppSettings ↔ ConfigurationStore sync |
| `Platforms/AgOpenWeb.Desktop/Program.cs` | Desktop mode selection |
| `Platforms/AgOpenWeb.Desktop/Launcher/LauncherEntry.cs` | Photino window: all-in-one app or `--console` supervisor |
| `Platforms/AgOpenWeb.iOS/LauncherViewController.cs` | iOS head: WKWebView over WebBackend |
| `Platforms/AgOpenWeb.Android/MainActivity.cs` | Android head: WebView, Back/Exit, startup probe |

## Service Interfaces

Services use interface-based design in `Shared/AgOpenWeb.Services/Interfaces/`:
- `ITrackGuidanceService` - Unified guidance (Pure Pursuit + Stanley) for all track types
- `IGpsService` - GPS data processing and position updates
- `IUdpCommunicationService` - UDP communication with AgOpenGPS modules
- `INtripClientService` - NTRIP caster connections for RTK
- `INetworkWatcher` - the one place that notices network changes (plus a 30 s heartbeat); UDP discovery, NTRIP retry and the bug report outbox subscribe in that order
- `IFieldService` - Field loading/saving/management
- `IBoundaryRecordingService` - Recording field boundaries
- `IMapService` - Historical map hook; every head registers `NullMapService` (the web client renders)
- `IConfigurationService` - AppSettings ↔ ConfigurationStore sync, vehicle profiles
- `IVehicleProfileService` - Vehicle profile CRUD
- `INtripProfileService` - NTRIP profile CRUD
- `IAutoSteerService` - Zero-copy GPS→steering pipeline
- `ISettingsService` - AppSettings JSON persistence
- `ICoverageMapService` - Worked area tracking
- `ISectionControlService` - Automatic section on/off based on coverage/boundaries
- `IUiDispatcher` / `IUiTimerFactory` - the host-loop thread the VM runs on (`HostLoopDispatcher`)

## Platform-Specific Code

Platform projects contain only what **must** differ per platform: the process entry, the web
view shell, DI setup, and platform services (battery, data root, imagery capture process).
Everything else lives in `Shared/`.

### Desktop
- `Program.cs` - mode selection (see above); the imagery-capture child process entry
- `HeadlessHost.cs` / `BackendHost.cs` - daemon lifetime; the backend used by both modes
- `Launcher/` - `LauncherEntry` (Photino window), `WebViewLauncher` (all-in-one), `ConsolePage` (`--console` HTML supervisor), `ScreenAwake`
- `ImageryCaptureProcess.cs` - Skia imagery compositing in a throwaway child process (crash isolation)
- `DependencyInjection/ServiceCollectionExtensions.cs` - DI setup

### iOS
- `AppDelegate.cs` - window, landscape lock, saves on background/terminate
- `LauncherViewController.cs` - WKWebView, startup + retry, external links to Safari, keep screen on
- `IosImageryCapture.cs` - imagery capture in-process
- `Services/IOSBatteryService.cs`, `DependencyInjection/`, `Info.plist`

### Android
- `AndroidApp.cs` - `Application`; `AndroidDataRoot` init; holds `Services`
- `MainActivity.cs` - WebView, probe-and-retry startup (#73), Back → "Exit AgOpenWeb?", immersive mode, keep screen on
- `LauncherWebClients.cs` - navigation policy, external links, console → logcat
- `BackendService.cs` - foreground service owning the backend (survives backgrounding)
- `WebKeyboardBridge.cs` - `window.agnative.hideKeyboard()`

What each shell must provide to the web UI (JS + WASM, audio without a gesture, `window.open`
to the system browser, keep the screen awake, localhost HTTP) is listed in
`Plans/Completed/AVALONIA_REMOVAL_PLAN.md`.

## Common Tasks

### Adding a web command
1. In the feature's file in `js/`, send it: `transport.send('mything.do|' + arg)`.
2. Route it in `RemoteServerWiring.cs` (or a `config.set` key in `RemoteServerWiring.Helpers.cs`) to a VM method or command.
3. It needs control automatically; add it to `ObserverIds` only if it changes nothing.

### Showing new state in the client
1. Add it to the DTO in `Contracts.cs`, fill it in `SceneProjector` (or the relevant projector).
2. Encode it in `WireCodec.cs` and decode it in the same order in `transport.js`.
3. Render it in the feature's file in `js/`. Frames are versioned by content hash where that matters (`SceneProjector`).

### Adding UI text
Write it in English; `index.html` needs nothing more, and strings in `js/` use `tr('…')`
(`tr('Pass {n}', { n })` for values). Then run `Tools/i18n-extract.py` and commit `en.json`.
See `Docs/TRANSLATIONS.md`.

### Verifying a change in the running app
Run the Desktop head `--headless` with `AGOPENWEB_DATA=<dir>` and drive it from a browser or
Playwright: wait for `iHoldControl === true`, then `transport.send(...)`; read `tick`,
`lastTick`, `scene`, `statusBar`, `config`. Kill any older instance first, or you test the
old build on `:5174`.

## NTRIP Connection Format
`NtripSession` (`Services/Ntrip/`) makes the connection and asks with NTRIP 2.0:
```
GET /mountpoint HTTP/1.1
Host: caster.example.com:2101
Ntrip-Version: Ntrip/2.0
User-Agent: NTRIP AgOpenWeb
Authorization: Basic base64(username:password)
Ntrip-GGA: $GPGGA,...
Connection: close
```
A caster that did not understand it (a reply that is no NTRIP or HTTP reply, or HTTP 400 or 505) gets the
NTRIP 1.0 request (`GET /mountpoint HTTP/1.0`, no Host or version header, the position sent right after
the answer) once; never after a 401, 403, 404, an error text or a source table. The version that delivered
corrections is remembered per service. `NtripResponse.Classify` sorts every reply into an `NtripCategory`
(wrong login and unknown mount point end the session, the rest is retried after 1, 2, 4, 8, 15 s, and a
network change ends the wait, and ends a stream that then stays quiet for 3 s, as on a move from Wi-Fi to the phone's hotspot); `NtripConnectionTester` (the Test button) opens its connection the same way.
Every wait has a timeout (`NtripTimeouts`). Design: `Plans/Completed/NTRIP_MAIN_FALLBACK_PLAN.md`.

**Main and fallback service.** Two exclusive modes (`Connections.NtripProfilePerField`, `config.set|conn.ntripProfilePerField`).
The default is one profile (the default profile) with an optional fallback service (`NtripProfile.Fallback`): a field load
changes nothing. With the setting on, a loaded field switches the profile and the fallback is not used. The client
(`NtripClientService`, rules in `NtripFailover`) switches to the fallback by itself only with the profile's Auto-switch on:
after `SwitchAfterFailures` failed attempts, or at once for a wrong login or mount point. The operator switches in both
directions at any time (`ntrip.switch|main|fallback`, `INtripClientService.SwitchToAsync`). It **never goes back by itself**:
while the fallback is in use after a failure, the main service is tried slowly in the background and a `MainBack` notice
says when it answers; not when both are one login on one caster. Every switch raises `Notice` and says that RTK may be lost.
In the web client the Network IO card keeps to the first lines and the switch; NTRIP Settings (`renderNtripSettings` in
`js/network.js`: headline, advice, checks, the mode) shows the rest, from the `NtripSessionDto` that ends the Status frame;
`ntrip.use|id` connects with a profile now (`MainViewModel.UseNtripProfileAsync`; the default too in the one-profile mode); the session frame carries
the profile name and the service in use (`ConnectionState.NtripProfileName/MainText/FallbackText`). A new default profile connects at once; the
*NTRIP active / disabled* button (`conn.ntripEnabled`) connects the default profile even with its auto-connect off. `ntrip.mountpoints` asks the caster for its
source table (`NtripSourcetable`), answered in its own frame; a profile's fallback ends its entry in the profiles frame; `ntrip.save` carries it (`NtripProfileFields`), `ntrip.test` can test it. A switch is a toast, "the main service
is back" is the host's confirmation dialog.

### RTCM forwarding
`NtripClientService` frames the caster's stream into RTCM 3 messages (`RtcmFramer`, after
`ChunkedDecoder` if the reply is chunked) and queues whole messages (`RtcmQueue`): 256-byte
datagrams about 25 ms apart, never closer than 10 ms; on a backlog the newest observation
epoch and newest station data win, and nothing is cut mid-message. Datagrams go to the GPS
module's own address when its position sentences have been heard in the last 10 s, else to
the subnet broadcast (or always, with Network IO → "Broadcast corrections"). Network IO shows
the receiver's correction age, warnings and a message table; bug report dumps carry
`ntrip_rtcm.txt`. Design, bench results and open checks: `Plans/RTCM_FORWARDING_PLAN.md`;
bench scripts: `Tools/rtcm-bench`.

### Holding the line when RTK is lost
`RtkLossHold` (`Services/Gps`; fork only), run by `GpsPipelineService` on the plane position before the
heading fusion: when the fix quality leaves 4 (RTK fixed) the step of the receiver's position against
its own travel (the last fix-to-fix step, counted in the receiver's epochs) is taken out as an offset,
as is each change of quality while held and the step when RTK fixed is back (never on the second fix after
a start or a gap, when the travel is not known yet); held 3 min at most, then
(and after RTK is back) eased to 0 at 5 cm/s. Setting `Connections.RtkHold` (`gps.rtkHold`, on by default,
in the vehicle profile and the Config frame after `rtkLostAction`); never with the simulator; reset on a
new plane. Told by `GpsCycleResult.RtkHoldNotice` (a toast), shown in the fix label from
`GpsCycleResult.RtkHoldM` (`VehicleState.FixQualityText`: "GPS Fix, held 0.9 m"). Docs/GPS_RECEIVERS.md.

## Features switched off
The work is on the foundation and navigation first. The VT cards, web cards (with the card pane) and AgShare
are built but switched off (`FeatureSwitches` in `AgOpenWeb.Models`, `Default` none): the host doesn't register
the VT service (no terminal, no gateway browsing) and refuses `vt.*`, `webcard.*`, `agshare.*` and the
`conn.agShare*` settings; the page starts with them hidden (`fs-no-*` classes on the body, `css/base.css`)
and the Hello frame's feature byte shows the ones the host runs. Settings made for them are kept.
`AGOPENWEB_FEATURES=vt,webcards,agshare` (or `all`) turns them on for development. An AgShare re-download keeps
what the operator chose for a hole that is still where the field has one (`FieldFileWriter.SameHole`; AgShare stores
outlines only, and a hole it brings new is drive-through; fork only). The ISOBUS task
controller's section control (#312) is not hidden.
**In this fork they are on** (`FeatureSwitches.Default` is `All`): the sprayer and seed drill modules' own
pages are web cards. At a sync keep the fork's default and its test.

## ISOBUS virtual terminal (VT card)
The VT cards show ISOBUS implements' own screens. `Shared/AgOpenWeb.VirtualTerminal`
(`IVirtualTerminalService`) runs SharpIsoTerm's terminal stack on the implement bus through
an AOW link gateway (`AppSettings.VtGateway`, port 2323; the HAT's `aow-linkd`) or an slcan USB
adapter on the device itself (`AppSettings.VtAdapter`, a CANable; not on iOS). Gateways announcing
themselves by DNS-SD (`_aowlink._tcp`) are listed in the card's gear by name (`AowLinkBrowser`); one
picked from the list is kept by name too (`VtGatewayName`) and followed to a new address. One to four
terminals (`AppSettings.VtCount`), as displays in one cab, each implement choosing its terminal in
its own menu, one card each. Their messages and pictures go to the browsers as `Vt` (with the
terminal) and `VtImage` frames (`VirtualTerminalRemote`), the painter is in `js/vt.js`, and each
card sends `vt.*` commands naming its terminal. Every head has it (iOS: device builds). SharpIsoTerm
comes as its NuGet package from its GitHub release, pinned by version and SHA-256 in
`Shared/AgOpenWeb.VirtualTerminal/AgOpenWeb.VirtualTerminal.csproj` and fetched by the build into
`nuget-local/` (a package source in `nuget.config`); to take a new SharpIsoTerm, change both to its
release's. To work on SharpIsoTerm itself, build against a checkout instead:
`dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -p:SharpIsoTermDir=$HOME/Code/SharpIsoTerm`
(an absolute path; its native libraries built with its `native/build.sh`). The service
is disposed with the backend: on Android and iOS the process outlives it, and the library allows
one stack per process.

## Web cards and the card pane
**Web cards** show an add-on's own web page (the HAT's pages, a rate controller's) in a card, picked
from a bookmark list kept on the host (`AppSettings.WebCards`, `webcard.save` / `webcard.delete`,
the `WebCards` frame; ⚙ › *Search the network*, `webcard.scan`, has the host first ask the network by
mDNS for the pages announcing themselves (DNS-SD `_http._tcp`, listed at once by the name they give:
`MdnsHttpBrowser`; asked three times, from its own port and from port 5353 shared with the system, as
modules answer one or the other), then ask each address of its own private networks (at most the /24
around it) for a page on port 80, listed with its title (`WebPageScanner`); a module found there but
not by mDNS is asked for its name at its own address (`MdnsHttpBrowser.AskHostsAsync`), and what was
asked and who answered goes to the log (`WebCardSearch` in a bug report's `logs.txt`); a page found
both ways is listed once);
`{host}` in a URL is the AgOpenWeb host as the browser reaches it. The page
loads from the add-on on its own origin, never through AgOpenWeb; observers get a cover over it.
Which cards are open, where, and each page's zoom are the browser's (`localStorage`); the zoom lays
the page out at 1/zoom of the card and scales it to fit, so a page shows more or bigger with nothing
done by the add-on. Where CSS zoom reaches a frame's page (Chromium) the frame is zoomed, not scaled
by a transform: Chromium runs a page from another site in a process of its own and opened a scaled
page's `<select>` lists where the control would be unscaled, off the card. Fork only. `WebCards` in `js/web-cards.js`; plan: `Plans/ADD_ON_CARDS_PLAN.md`.

**Card pane** (#456): Screen & Alerts › VT & Web Cards sets Floating (the default), Left or Right,
per browser. In a split layout the open VT and web cards sit in a pane on that side, two to a
tab, and the map has the rest of the window (`CardPane` in `js/card-pane.js`; cards call `track`, `shown`
and `untrack`). The map's area is set by `setMapArea(l, r)`; what that means for new code:
- An overlay on the map keeps clear of the pane through `--sal` / `--sar` (the map's insets: the
  pane's width on its side, else the safe area), a full-width bar uses `--map-l` / `--map-r`, and
  one centred with `left: 50%` adds `var(--map-dx)`. Something spanning the window (a menu panel,
  a dialog) uses `--win-sal` / `--win-sar`.
- `vw` is the map's width, not the window's, and the canvas starts at `mapL`: a pointer's
  `clientX` is `clientX - mapL` on the map (`s2w`, hit tests), and a DOM label placed from `w2s`
  adds `mapL`.
- A card moved in the page reloads its iframe, so the pane hides and shows cards on a tab switch
  and moves them only when the layout changes.
- The pane is a **drawer**: Closed (the handle on the window's edge, with the count of open
  cards), Split (⅓ to ½) or Full (over the map, which the frame loop then doesn't draw:
  `CardPane.coversMap`; a guidance strip along the top keeps speed, distance from the line,
  AutoSteer and sections, plus the light/steer bar above walking pace). Only the operator moves
  it (a drag of the handle snaps on release, a tap toggles Closed); above 2 km/h it can't be
  pulled to Full; it appears Closed when the first card opens and never closes by itself. Full sets both map insets, so everything on the
  map is off-screen. Plan: `Plans/ADD_ON_CARDS_PLAN.md`.

## Debugging Tips

1. **iOS simulator issues**: Use `xcrun simctl` commands directly if `dotnet build -t:Run` fails
2. **iOS device**: `xcrun devicectl device process launch --console --terminate-existing com.agopenweb.ios` streams the app's console; its web server is reachable from the Mac over the USB link
3. **Android**: Debug builds enable WebView debugging (`chrome://inspect`); `adb forward tcp:5599 tcp:5174` reaches the host
4. **A client shows something a fresh connection doesn't**: compare the incremental stream with the full snapshot (coverage deltas vs `Snapshot()`)
5. **Bug Report Dump** (main menu) zips settings, state, the device (`device_info.txt`: make and model, memory, disk, the network interfaces with an address, USB devices; on Android also the web view, power saving and how each network is carried; `DeviceReport`, a head's part in its `IDeviceInfoProvider`), the field (its background image only with *Include the field's background image* ticked, off by default; `field_background_left_out.txt` says it was left out), the active job's coverage tiles and the last 5 minutes of the GPS log (10 Hz; includes the sentence type, its pre-fusion heading and the receiver's differential age); with NTRIP in use, `ntrip_rtcm.txt` lists the RTCM messages the caster sent by type and `ntrip_session.txt` what the connection did and why it failed (no login in it; `system_info.txt` has an `NTRIP:` line with the same outcome). `system_info.txt` ends with each browser's GPU renderer, texture limit, canvas, frame rate, draw time and where the rest of a frame's time goes, for now and for the slowest stretch since the page loaded (`diag.client`, `ClientDiagnostics`; `js/alerts.js`), flagged `SOFTWARE RENDERING` when the renderer is the CPU (llvmpipe, SwiftShader): such a device will not draw the map smoothly whatever the app does. What the operator was told is in `logs.txt` as `[Screen]` lines (`OperatorMessageLog` in `Services/Logging`, set on `RemoteServerHost.OperatorLog` and `MainViewModel.OperatorLog`): each toast, alarm sound (not the section, lift and AutoSteer on/off clicks), module message (PGN 221) and dialog with the button pressed, the same line again within 30 s written once. The background image and the `[Screen]` lines are fork only
6. **iOS Release builds hang in CI**: Use Debug configuration (Release triggers AOT compilation that hangs on runners). Simulator builds run on the interpreter (`MtouchInterpreter=all` in the csproj), so they skip AOT altogether; device builds keep it
7. **The line moved to another pass**: a lateral move (the blue arrows under the U-turn buttons, *Pass >* in the line menu) is logged (`[Guidance] Lateral move right: pass 19R -> 18R`, also refused or ignored mid-turn) and told as a toast (`GpsCycleResult.PassNotice`); the AB line's debug heading is logged only when it changes, so a dump's 2000 log lines reach further back. Fork only
8. **A steering setting changed by itself**: every change of what goes to the steer module (PGN 251/252) and of Max Steer Angle is logged with the value before and after, whatever made it (`[Steer settings] Ackermann 90 -> 0`, `SteerSettingsLog`; a profile load too), and an AutoSteer card edit from a web screen says so just before (`[Config] From a web screen: autosteer.ackermann = 0`; a line break in it is written as `\n`, so a screen can't add lines of its own). A settings slider moves only by its thumb, the heading fusion slider too: a press on its bar changes nothing, and a release off the slider is heard (`guardSliderTap` in `js/cfg-controls.js`; one tap on the bar had set Ackermann 0 and Max Steer Angle 61 on a tractor), and Ackermann under 50 is asked first, and any lower value once under 50 (`cfgAskFirst`). Fork only

## Code Style

- **Cross-platform parity is mandatory.** All code MUST go in `Shared/` unless it requires platform-specific APIs. Platform projects contain only the shell, DI setup and platform services. See `CONTRIBUTING.md`.
- The web client is plain HTML/JS: no framework, no build step. Keep its idioms (DOM overlays for text, CanvasKit for the map). Its scripts are classic scripts sharing one scope: code that runs while loading uses only earlier files, and anything asynchronous starts in `js/frame.js` (`Docs/WEB_CLIENT.md`).
- Use dependency injection for services
- Use shared `GeometryMath` utilities instead of duplicating distance/angle calculations
- Anything written to disk or a wire uses `CultureInfo.InvariantCulture` (see `CONTRIBUTING.md`)
- New `.cs`/`.js`/`.py`/`.sh` files start with the Apache 2.0 header (`// SPDX-License-Identifier: Apache-2.0`, see `CONTRIBUTING.md`); `Tools/license-headers.py` adds it, CI checks it

## Testing

```bash
dotnet test Tests/AgOpenWeb.Models.Tests/
dotnet test Tests/AgOpenWeb.Services.Tests/
dotnet test Tests/AgOpenWeb.ViewModels.Tests/
dotnet test Tests/AgOpenWeb.RemoteServer.Tests/

# Legacy guidance algorithm test harness
dotnet run --project TestRunner/TestRunner.csproj
```

**Test projects:**
- `AgOpenWeb.Models.Tests` - GeometryMath, GeoConversion, boundary/curve utilities
- `AgOpenWeb.Services.Tests` - NMEA parsing, TrackGuidanceService, coverage, pipeline, U-turn (uses the virtual UDP modules in `Simulators/AgOpenWeb.VirtualModules`)
- `AgOpenWeb.ViewModels.Tests` - ViewModel / control-brain logic
- `AgOpenWeb.RemoteServer.Tests` - web host: Bing tile replies, boundary imagery capture
- `AgOpenWeb.IntegrationTests` - test-support library (fake settings etc.); not a standalone test run
- `AgOpenWeb.VehicleToolScenarios.Tests` - Vehicle & Tool scenarios: a simulated machine (steering, implement linkage, antenna on a slope, the Boards simulator's GPS/IMU/motor) drives the whole backend; a careful operator on five vehicles and their implements, a careless one with wrong measures, signs, units, roll setup and profiles, and a settings sweep (every guidance/AutoSteer/tool/vehicle setting flipped: did the steer angle, a PGN or the sections change?); a findings report; the rig reports no network interfaces, so its PGNs go to 127.0.0.1 only and never reach a module on the LAN; CI only builds it (and runs its host-loop budget), `vehicle-tool-scenarios.yml` runs it nightly (its `README.md`)
- `AgOpenWeb.FieldScenarios.Tests` - end-to-end field scenarios: the whole backend in-process, a careful and a careless operator on five real field shapes, a findings report; about 4 minutes, so CI only builds it and `field-scenarios.yml` runs it nightly (its `README.md`)
- `AgOpenWeb.SteerWizardScenarios.Tests` - end-to-end steer wizard scenarios: the whole backend in-process, an emulated AiO steer module (firmware P controller on its 25 ms loop, WAS formula, locks, either motor wiring) and a moving tractor, a careful and a careless operator, the wheel tuning step on a late PWM valve, a Keya and a wheel motor, a safety watch on the wheels at 50 Hz, a findings report; about 20 minutes, so CI only builds it and `steer-wizard-scenarios.yml` runs it nightly (its `README.md`)
- `AgOpenWeb.MachineModuleScenarios.Tests` - an emulated Sprayer_ESP32 machine module on the real UDP: sections vs valves, settings reaching the module, restarts and outages (`machine-module-scenarios.yml`)
- `AgOpenWeb.GpsNtripScenarios.Tests` - receivers, sentence families and an NTRIP caster on the loopback: corrections, network changes, dual heading (`gps-ntrip-scenarios.yml`; one scenario needs Linux's 127.0.0.2)
- `AgOpenWeb.FieldJobScenarios.Tests` - whole jobs on real fields with AutoSteer, U-turns and auto sections, measuring where product really went on a 0.2 m grid (`FieldJobScenarioTests`, the careful and the careless operator); its `PathJobScenarioTests` do the same on curves, the boundary as a loop, a water pivot, contour and a recorded path; `RigJobScenarioTests` with the testers' rigs: an implement shifted to one side, a curve recorded by driving, a wide implement skipping rows, the snake order (skip worked passes, W1-W2); `TurnInterventionScenarioTests` with an operator who acts during a U-turn, and manual U-turns; `ImperialJobScenarioTests` the job typed in feet and inches through the real web client, headless Chromium. One engine (`JobRun`), so one nightly, `job-scenarios.yml`: the six side by side, then one combined report listing each finding once with every harness and job it was seen in (`Tools/combine-job-reports.py`). **Short runs:** `AGOPENWEB_SCENARIO_TURNS=N` stops each job at its (N+1)th U-turn, `AGOPENWEB_SCENARIO_MINUTES=N` after N simulated minutes, `AGOPENWEB_SCENARIO_FROM_PASS=K` starts on pass K (rework passes 28-32 of a finding: `FROM_PASS=28 TURNS=4`) (a job of a few passes in under a minute, not 5-80), with the whole-field checks skipped; `AGOPENWEB_SCENARIO_ONLY=J1,J8` picks jobs; the workflow's `only`, `turns`, `from_pass` and `minutes` inputs do the same on GitHub. **The harnesses run overnight; they are not a gate for a fix.** A job with a finding leaves the app's own bug report dump beside the report (`evidence/<job>.zip`, `Tests/ScenarioShared/Evidence.cs`: with the whole job's GPS log and every U-turn's planned path), as a tester's report does: make the fix from it, prove it with a unit test built from it (failing on develop, passing on the fix), then merge; the nightly on develop is the check the next morning. A short run is for looking into what the dump does not show (then make the dump show it) and for a change to a harness itself (`FROM_PASS` works on AB lines and curves)
- `AgOpenWeb.IsobusScenarios.Tests` - an emulated AOG-TaskController and its implements (`isobus-scenarios.yml`)
- `AgOpenWeb.WebClientScenarios.Tests` - the real web client in headless Chromium (Playwright) on the screen target, a 10" tablet in landscape (1280×800 CSS px, the Panasonic FZ-G1; other screens are out of scope, `Docs/WEB_CLIENT.md`), a second tablet as observer, and the operator's screen going away while working (tab closed, Wi-Fi silent or cut, tablet asleep, Free Drive, a section on by hand), read at the module port; and the operator's flows done by taps and typing (tracks made every way but the water pivot, boundary and headland, tram lines, fields and jobs, the section bar, the U-turn and lateral buttons) with a careful and a careless operator (`web-client-scenarios.yml`)
- `AgOpenWeb.UpdateScenarios.Tests` - settings, profiles, casters, fields and a job made on older releases (their Linux bundles, SHA-256 checked) then read back by this build, a rollback; on this build a restart, a power cut, damaged and leftover files, a moved data folder, a comma-decimal locale, AgOpenGPS fields and a whole AgOpenGPS 6.8.6 folder; about 15 minutes (`update-scenarios.yml`)
- `AgOpenWeb.WorkingDayScenarios.Tests` - an eight-hour working day in one process (fields, refills, lunch, a tablet and a phone that drop, stall and reconnect, the next morning): memory baselines, heap census, cycle and tick times, the deadman with a second browser; about 45 minutes (`working-day-scenarios.yml`)
- `AgOpenWeb.DumpReplayScenarios.Tests` - the bug report dumps attached to public issues, replayed on this build: the reporter's settings, field, job and track, their GPS log fed back through the pipeline and compared with what their build did, then a machine of their shape taken over with AutoSteer and U-turns; the bug report service's dumps only in a local run (`AGOPENWEB_REPLAY_PRIVATE=1`), never in CI (`dump-replay-scenarios.yml`)

**Running the harnesses:** on the harness host, never on a development machine (`Tools/harness-runner`: a Docker image run on the `runner` context; `runner-compare.sh` runs a fix and its base side by side and lists the findings new, gone and in both for each report, `Tools/compare-reports.py`; it exits 1 when a side did not pass or a report is missing). Name the jobs (`AGOPENWEB_SCENARIO_ONLY`, exact codes) and keep the run short (`TURNS`, `FROM_PASS`): the jobs named run one per container on each side at about 50× real time (`AGOPENWEB_SCENARIO_PACE=2`; a shard that lags the fixes fails the comparison), so the wait is the longest job, and the base side and the build are cached by commit and settings, so a second fix against the same develop runs its own side only (its `README.md`). Every harness stops at the start when another process holds UDP 9999 or 8888 (`Tests/ScenarioShared/PortGuard.cs`, linked into each): an app left running shares the packets and the findings are noise. Before rerunning, read the saved report, pictures and traces; one confirming run per fix.

CI also runs `Tools/i18n-extract.py --check` so `en.json` stays current, `Tools/license-headers.py --check` for the licence headers, and `Tools/i18n-limits.py --check` (job `translations-fit`, only when a change touches `wwwroot/`, the check, its baseline or the workflow; otherwise skipped, which counts as passed) so every translated label fits the English layout.

**Hot-path budgets** (job `hot-path-budgets`, `Tests/AgOpenWeb.Services.Tests/Performance`): a 300 × 500 m field, with a headland and with only the outer boundary, is driven through the real GPS cycle and the 100 Hz control tick, and every cycle is held to a budget of `WorkCounters` (polygon edges, coverage lookups, offset points, bytes allocated). Counts don't depend on the machine, so CI can enforce them. A failure means new work runs every cycle: do it once and keep the result. `HotPathBudgetTests.Report` (explicit) prints the counts. Run locally: `dotnet test Tests/AgOpenWeb.Services.Tests/ -c Release --filter TestCategory=HotPath`. The same job holds the host loop and the 10 Hz broadcast to a budget with a field open (`HostLoopBudgetTests` in the Vehicle & Tool harness: the whole backend, 41 fields on the disk, a pass driven; the test runs each `MapBroadcaster.BroadcastOnceAsync` itself and the rig's dispatcher counts every host-loop item, files read included, #604/#169): `dotnet test Tests/AgOpenWeb.VehicleToolScenarios.Tests/ -c Release --filter TestCategory=HotPath`.

## U-Turn System

U-turns are planned by `YouTurnStateMachine` (automatic turns at the turn line, and manual
turns from the on-screen buttons) with paths built by `YouTurnCreationService`:
- **The turn line, not the headland**, as AgOpenGPS (`CYouTurn` uses only `turnLine`, `CTurn.BuildTurnLines`): the fence moved in by the U-turn distance from boundary. The automatic turn's trigger, zones and landing checks read it (`GpsPipelineService`: `turnLine`, the turn area of the out-of-bounds test too), with a headland or without, on or off. The headland is for the sections, the hydraulic lift and the drawing, and for the "Headland too narrow" warning (`TickContext.WorkHeadlandLine`). A pass inside the headland band turns like any other (#694: a boundary curve's outer passes).
- Automatic: entry leg, arc, exit leg back to the next track; triggered when the tractor reaches the turn start, with an approach alarm 20 m out.
- The first turn on a track goes the way the work is (`WorkSideForFirstTurn`, the normal and the alternate-skip modes): with a headland, the side whose next pass has at least the landing length (20 m) inside the area it encloses; the pass along the edge the operator started beside has a sliver in it (15 m, J20) or lies in the headland band inside the turn line (J11), and counting it sent the first turn at the edge. Without a work area, or no such pass either side, the turn line decides.
- Rows wider than twice the turn radius (a wide spreader, skipped rows) get AgOpenGPS's wide turn (`CreateCurveWideTurn`): a quarter circle out of the pass, tangent to the turn line, the turn line itself, a quarter circle into the next pass, so the distance from the boundary holds on an angled headland (#544). The omega turn (one shape square to the track) is the fallback when it can't be built. It is also tried before a wide turn of no U-turn's shape is refused (`YouTurnCreationInput.NoWideTurn`): where the next pass runs on far past the end of the tractor's (a pass that ends at a notch of the fence the next one runs by), the wide turn ran round the field along the turn line to that far end. Fork only.
- Manual: a Dubins path at the configured `UTurnRadius` from 4 m ahead of the tractor to the point abreast on the target pass (an omega when the radius exceeds half the pass offset), laid out along the track's local heading (the curve's heading at the nearest point); the path's first point is the tractor itself. Like AgOpenGPS `BuildManualYouTurn`.
- Beside a hard outer boundary a manual turn keeps the implement the `UTurnDistanceFromBoundary` from the fence, unless the implement is already nearer than that at the press (a pass along the fence): then it only has to stay inside the fence, and one already across it may go no further across (`ManualTurnClearsHardBoundary`; the turn "from here" the app starts by itself keeps the whole distance, `startedByTheApp`). Before, a turn away from the fence was refused for where the implement already stood. Each corner of the implement is held to its own place at the press (`SweepKeepsToFence`): judged by the worst corner alone, one across the fence let another, inside then, cross it in the turn. AgOpenGPS's manual turn checks nothing. Fork only.
- The state machine's `StatusMessage` is not sent to the web client. What the operator must see is marked `YouTurnEffects.TellOperator` and shown as a toast (`GpsCycleResult.YouTurnNotice` → `ReportFailure`): a manual U-turn that was refused (every way out of `TriggerManual` but a started turn; with no line to turn on, no track, contour or a recorded path, the pipeline answers: `YouTurnEffects.NoLineForManualTurn`), and a turn that can't be made or was missed, once, with its sound (`SoundMissed`, `SoundCreationFailed`).
- The skip button's mode (Normal, Alternative, Ignore worked tracks) and the operator's row count are the field's own: `UTurnSkip.json` in the field folder (`UTurnSkipFileService`), written at each tap and when the field is saved (close, exit), read when it opens. A field without it opens in Normal with no rows skipped, so the last field's choice doesn't carry over.
- The turn hands back to line guidance with 4 m of arc remaining (`EarlyCompletionLookahead`).
- While a turn is driven (`IsExecuting`) the pass shown is the one it lands on (`GpsPipelineService.TurnTargetPass`): the highlighted line, the tool's band, the guidelines and the pass label, as AgOpenGPS, which moves to the next pass when the turn starts (`CYouTurn.YouTurnTrigger`). The pass worked (`HowManyPathsAway`: the worked-pass mark, the snake order, a turn backed out of, #588) still moves on in `CompleteTurn`.
- A turn that loops (the next pass closer than twice the radius) is moved in to stay inside the turn line, as AgOpenGPS; when the headland is narrower than the loop is deep it leaves its pass inside the headland line, still working, and the status says "Headland too narrow for this U-turn" with how far before the line (`TurnStartInsideHeadlandLine`).
- Obstacles (inner boundaries) chosen as *U-turn* (`AtObstacle == Turn`) get a turn line of their own, the obstacle offset outward by `UTurnDistanceFromBoundary` (`YouTurnCreationService.Obstacles.cs`; the box round the obstacle grown by that distance when the outline cannot be grown, `GrownBox`), like AgOpenGPS with Drive thru = No, a hard one by the implement's reach (half its width, the sections' total `ActualToolWidth`, not the stored `Tool.Width`, plus its offset, `HardReach`) more, so the pass itself keeps the implement that distance from a wall or a ditch: the turn-area test returns -1 outside the outer turn line, 0 in the workable area, k inside obstacle turn line k. A turn at an obstacle has no simple fallback and is never "turned from here" when missed; a hard obstacle keeps the implement outside. *Drive through* obstacles are driven through. A planned turn is planned again when the obstacles change (`YouTurnCreationService.ObstacleStamp`: each one's choice, Hard and every corner, `BoundaryPolygon.OutlineStamp`), a manual turn that would cross a U-turn obstacle, or land within 20 m of one (`ExitReach`, as the automatic turn), is refused, as is the simple fallback (`FallbackResult`; `LeavesField` keeps it out of the obstacles themselves only), and with AutoSteer on, a U-turn obstacle within 10 m plus 2 s of travel ahead with no turn planned before it takes AutoSteer off with the alarm (`ObstacleAheadWithoutTurn`: a turn lost, missed or aborted there, or U-turn off); an obstacle changed under a turn being driven (another choice, Hard, added, moved), with the rest of the turn or the 20 m after it running into it, does the same (`ObstacleChangedUnderTurn`: a turn being driven is not planned again). A turn's 20 m of exit keep the tractor outside every turn line and the implement the safety margin off every hard fence (`ClearsTurnObstacles`). The generator seats its arc and slides it to the turn line by a test that also blocks the room round every Go around obstacle (`IsPointBlockedForTurn`), and an arc that slides on without touching the line (seated inside a bend, sliding along the bend's heading) is seated further back (`MoveTurnInsideTurnLine`'s bound), so a turn at an obstacle just past a tree the pass goes round is made before the tree, not 127 m away. A turn lands on a straight stretch of the next pass: one whose last point is off the straight pass, on a bend round a Go around obstacle, is seated further back (`LandsOnBend`, 4 m steps, 30 m at most), as is one whose legs or exit meet an obstacle; in a turn a Go around obstacle gets a ditch's room (`GoAroundRoom`: half the track plus the larger of the Go around margin and the U-turn distance), the implement the U-turn distance off a hard one along the path and the safety margin while the rig settles on the next pass (8 m plus twice a trailed hitch). The entry leg before the arc is the pass itself, bent round its Go around obstacles with the Go around margin, and is not held to that room (`ClearsTurnObstacles`' `arcFrom`). A turn seated on a bend of its own pass (seated back from a hard fence near the end of a pass with a tree on it) starts at the bend's heading and lands on the next pass at that pass's heading (`ArcFromBend` in `YouTurnCreationService.GoAroundBend.cs`, `SagittaTurnGeometry.BuildToLine`: a counter-arc and the main arc, or, with the next pass further than two radii, an arc, a straight and a quarter circle), seated as far on as it fits; its shape is judged against 180° less the bend's angle to the pass, `LandsOnBend` is asked before the shape, and a turn on a bent pass that is still malformed is seated further back before it is refused. Built at the bend's heading, the arc ended up to 45° off the next pass and was refused as malformed: on a tester's field (a tree 33 m before a slanted hard fence, an 8 m front boom) every job stopped at its first turn with "U-turn blocked: implement would swing into a hard boundary". Near a hard fence a turn not built, or not of a U-turn's shape, now says "No U-turn fits inside the boundary at this end" (`TurnPathResult.NoTurnBuilt`), and the tractor at the turn line or past it "Too late for a U-turn at this end" (`TurnFailure.TooLate`); the swing message is the implement's clearance only. An obstacle added or moved onto the entry leg of a turn being driven (the bent pass, not held to a turn's room) stops it when the leg's pivot comes within what the bend keeps from it less its margin: half the implement and its offset, or half the track if wider (`EntryLegRunsInto`, `ObstacleDetour.KeepOutFor`). No crossing of the turn line ahead counts as too late only with the tractor within 3 m of it (`NoCrossingFailure`). The U-turn obstacle ahead of a turn (`ObstacleAhead`) is looked for once the pass has been in the workable area, as its end is: from outside the turn line at the start of a job it was not seen. Fork only. Tests: `TurnAtGoAroundBendTests`, `SagittaTurnToLineTests` (Models). New obstacles are Hard (`Boundary.AddNewObstacle`). An obstacle drawn on the map (*+ Polygon*, `RemoteAddObstacleFromMapPoints`) is kept to the field (`ObstacleDrawing.InField`, a Clipper2 intersection): wet ground at the end of the passes is drawn round from beyond the fence, not along it inside, and each part inside is an obstacle of its own, but a sliver under a tenth of the largest part (caught where the outline is drawn close to a fence that bends in and out) is left out and said; an outline that crosses itself is each loop it encloses (non-zero); one wholly outside, or drawn with no outer boundary, is refused (a tester's field, 24 Nov 2026; official #624 too). A field whose obstacles had no choice in its file (AgOpenGPS, or saved before) says once on opening how many now get a U-turn.
- Obstacles chosen as *Go around* (`AtObstacle == GoRound`; a new circle's default) bend the pass round them (`ObstacleDetour`: the obstacle grown by half the implement plus the Safety margin, the offset limited to 45° and rounded to the Curve radius, both settings on the U-turn tab and in the tool profile). One `GoAroundPasses` singleton serves the pipeline (steering, display, light bar) and the U-turn planner (the current pass walked to the turn, the next pass the exit lands on), so a turn is never planned from a line the tractor does not drive; it pins the side of each obstacle while the tractor is beside it and over on that side, so a nudge or snap never rebuilds the bend across the tree (the pin is let go when the tractor gets to the other side, as a U-turn next to the tree does). An obstacle with no room on the allowed side is promoted to a U-turn obstacle for that pass (toast "No room to go around Inner N on this pass: U-turn before it"); a turn keeps the tractor out of every Go around obstacle and the implement off a hard one; and with AutoSteer on, a Go around obstacle within 10 m plus 2 s straight ahead while the tractor is more than 1.5 m off the steered line takes AutoSteer off with the alarm. The margin is never under 0.3 m on a 6 m bend and more on a tighter one (`GoAroundPasses.MinMarginFor`), and the radius never under the tractor's minimum turning radius (`GoAroundPasses.Radius`), with a front implement h ahead never under √(r² + h²), the bend its pivot can keep the implement on (fork only: 5 m bends took a 4.5 m-ahead boom 1.35 m off the pass on a tester's field). A bend never moves the line more than two implement widths (never under 20 m) off the pass (`ObstacleDetour.MaxOffset`): a tree, or a row of trees, needing more is a U-turn obstacle for that pass. Design: `Plans/OBSTACLE_GO_AROUND_PLAN.md`; scenarios: `Tests/AgOpenWeb.Services.Tests/YouTurn/ObstacleScenarioTests.cs` (`GoAroundLegs`).
- A pass that leaves the turn area and comes back within `UTurnCrossGap` metres (U-turn settings, "Drive across a headland gap up to"; 0 = off), across the headland only (inside the boundary, outside the headland line and every obstacle not driven through, and never through an obstacle's turn area, which is turned at as without the setting), is not turned at the gap: `GapCrossingEnd` in `YouTurnCreationService` walks on past it in both crossing searches, the turn is planned at the far end and the pass is driven through the gap with the sections off by headland section control (a narrow tip of the field, a notch in its edge). The next pass is taken from abreast of the gap's end (`gapResumeIndex`), since abreast of the tractor it can be outside the field. Since official #850 the tractor's own pass is driven through any stretch outside the turn line that stays inside the fence, whatever this setting (`PassEnds.Find`, *One end of a pass* below), but never into a U-turn obstacle's turn area; the setting still decides for the next pass (`FindCurveOutTurnPoint`). A setting that is no length (not a number, infinite, negative) is 0 (every "longer than the setting" test is false for one that is not a number), and `config.set` takes only finite numbers for every setting (`D` in `RemoteServerWiring.Helpers.cs`; fork only).
- **One end of a pass** (`PassEnds.Find`): walking the pass from the tractor's point, the first point outside the turn line after the pass was inside, as AgOpenGPS (`CYouTurn.FindCurveTurnPoint`). The state machine's distance to the turn (`DistanceToHeadland`, measured along the pass, the pass and its end cached per pass: `DistanceToPassEnd`), the turn builder's arc start (`YouTurnCreationInput.TurnLineEndIndex`, set by `BuildCreationInput`), the retry with the turn line moved in and the fallback turn all use that end; the pass they walk is `YouTurnCreationService.PassForTurn` (the track offset, then extended). Not a straight ray along the heading (on a curve it left the field early on the outside of a bend, J10) and not the crossing driven in over at the start of a job. A stretch outside the turn line that stays inside the fence, the pass back inside the turn line after it, is driven through, not turned at: the end of a pass is the turn-line crossing that the fence's crossing follows (J19: pass -18 runs along a slanted edge 1.2–2.1 m inside the fence for 26 m with 180 m of field beyond; turned there, it landed 11 m short of the next pass's end and the job stopped; AgOpenGPS turns at the first point outside the turn line). The turn line is a margin for turning, not for driving along an edge. The next pass's end is found the same way (`FindCurveOutTurnPoint`, `PassEnds.IsDrivenThrough`, `YouTurnCreationInput.IsPointDrivenThrough`): taken to end at the first point outside the turn line, walking from abreast of the tractor, it "ended" where it runs past a bulge of the fence nearer than the U-turn distance, and the wide turn ran along the turn line all the way back to it (Tapada da Casa with an 8 m front boom, radius 5.4 m, 6.3 m from the boundary, one row skipped: 300 m round a corner, refused as malformed at pass -8 with 88.6 % sprayed; 94.3 % with both fixes and no turn refused; on a soft boundary the 200 m turn was driven). Test: `WideTurnNextPassEndTests`. Fork only. In this fork the turn line of a U-turn obstacle is never driven through that way: a point inside it is an end, as at the fence (fork only). When no real turn fits, the simple fallback (a semicircle) is seated back along the pass until it keeps `UTurnDistanceFromBoundary` from the fence: to the headland crossing and up to 30 m into the field past it, starting ahead of the tractor (`SimpleFallback`, fork only: renatorgr/AgOpenWeb#130, #135). Only on a straight pass: the fallback runs straight from the tractor, so on a curve it is seated nowhere (as official) and built again as the tractor comes closer; seated to fit 190 m away on a boundary curve, it was never joined and the missed turn was made "from here". It is then used only if every point of it stays in the field, outside obstacles a turn may not drive through (`LeavesField`, #698); otherwise no turn, the creation-failed sound and "No U-turn fits inside the boundary at this end - take over manually". A hard boundary refuses a turn that would start under or behind the tractor.
- A turn tried again with the turn lines moved in (the implement over a hard fence, a landing on a bend, an obstacle in its legs: `CreateTurnPath`'s setback loop, which moves the whole fence in) is seated back from the end of the pass the first try found: walking back from that end, at the moved line's crossing nearest to it (`YouTurnCreationInput.TurnLineEndIndex`, `TurnLineEndOnRetry`; not after a gap crossed). Moved in, the fence's turn line also crossed the pass where the fence bulges in beside it, and the first crossing ahead was taken: on Tapada da Casa an 8 m front boom near the north fence moved the line in 5 m and the turn was made 260 m early, beside the east bulge, landing on the next pass inside its crop and leaving a strip of it unsprayed. Test: `TurnSetbackStaysAtEndTests`. Official since #828; not after a gap crossed is fork only.
- The next pass must have room at this end: at least 20 m of it inside the cultivated area back from the end ahead (`IsPathLandable` / `LandingLength`), not just any piece of it inside the field; otherwise no turn, and the operator is told why and takes over at the end of the pass: "No U-turn onto the next pass: only 15 m of it at this end, 20 m needed", or, when the next pass runs on but nearer the fence than the U-turn distance after a short piece, "16 m in, it runs 1.0 m from the boundary, 2.0 m needed" (#712; `YouTurnPathingService.NoLandingMessage`; J9 and J18 on Olival, where no tractor fits between the pass and the fence). A curve with points far apart (drawn by taps: a point every tenth of the way between two taps, 20 m with taps 200 m apart) is walked a metre at a time in these tests (`YouTurnPathingService.WalkedPass`: the landing, the gap ahead, the length inside the area); asked at its points only, a pass with 22 m at this end and one point in them had no landing. Fork only. "End of field reached" is said only when no pass is left in the field. With skip worked passes the snake keeps its own message with the passes still to do (`SnakeNoRoomMessage`, fork only). In this fork, when no pass has room but the current pass goes on inside the field past the headland ahead (a corner the headland clips off it, a gap), there is no turn and AutoSteer drives on across it: "No room to turn at this headland - driving on across it" (`GapAhead` / `DrivesOnAcrossGap`, renatorgr/AgOpenWeb#129; upstream left it out until after the v1.0 freeze). The pass must run on at least 10 m inside past the gap (`MinRunAfterGap`): a sliver or spike of the headland line is not more field.
- Skip worked passes (the snake order): when the order's next pass has no room at this end, or its turn does not build, the turn goes onto the nearest pass still to do that has room, first on the side the order is going, then on the other side (one turn built per side; a pass at least the skip away before a nearer one; none further than twice the skip's step, else "N passes still to do, but no room on the next pass at this end" (`YouTurnStateMachine.SnakeNoRoomMessage`; "End of field reached" was read as the field done with 23 passes left; fork only) and the operator decides), never against the side the operator picked with the U-turn arrow, whichever way the order goes and when the end is tried again 1.5 s later (`TriedJustNow` gives the arrow back from `YouTurnWorkingState.HeldDirectionLeft`, in every mode; an arrow pointing the order's way was forgotten and the turn went to the other side, and in the normal mode the retry turned the default way; official #888, Chris's review) (`TurnOntoNearestOpenPass`; AgOpenGPS's `GetNextNotWorkedTrack` only steps outward on the turn's side past worked tracks, so the other side, passes nearer than the skip and the room and build checks are ours); the order is planned again from where it lands. The same nearest pass is tried first when the order's next pass still to do is further than twice the skip's step (`MaxFallbackStep`; the passes in between worked before), from any pass nearer than the order's, either side: the turn went from 17R to 7R along the headland, 80 m, with 16R and 14R open beside the tractor (a tester's field, simulator), and on 21R, with the order's 8R 13 passes over, 15R six over was never looked at and the turn onto 8R did not build, so there was no turn; with none of them taking a turn, the order's pass is turned onto as before. Fork only. With the operator's headland on and the sections off in it (`Tool.IsHeadlandSectionControl`), a pass the snake turns onto (the order's or a nearest one) must also have that room in the crop, inside the headland line (`SnakeLandable`; the U-turn arrow's swap too, official #887): the turn line is the fence moved in by the U-turn distance, so a pass lying in the headland band had room there and nothing to work, and unpainted it was still to do (a tester's field, simulator: 13L, 12 m of crop, and 15L, none, in a corner of the headland, before 12L). The order and its worked-pass test are then made for the crop too (`SnakeArea`): a pass lying only in the band is not in the order, since unpainted until the headland is worked it stayed "to do" for ever, and next in the order it hid the passes still to do beyond it ("End of field reached" with a pass 13 over left). Fork only. An end where no turn was made is remembered (`_failedEnd`) and tried again every 1.5 s (AgOpenGPS's wait before it builds again, `makeUTurnCounter`), silently, the operator having been told once, and at once when the pass, the direction of travel, the nudge, the order, the field or headland, the skip, the U-turn arrow or a turn setting changes. The normal and alternative modes remember such an end the same way (`TriedJustNow`, the field's stamp also counting the fence made hard or soft and the obstacles, the key the mode, the gap to drive across, the Go around settings and the track width; fork only): rebuilt on every cycle, a turn that could not be made (the tractor parked with AutoSteer on) filled a bug report's 2000 log lines in 13 s. It is forgotten when a turn is planned or driven, or after 1.5 s of not looking for one (off the line, out of the creation window); a shorter spell keeps it, so a heading flickering at the edge of the 20° the turn is looked for within does not build it every other cycle. Test: `FailedEndRetryTests`. The 1.5 s are 15 GPS cycles times the simulator's time-lapse (`TickContext.TimeLapse`), and the tractor must also have gone on a tenth of the way to the end, 2 m at least (`RetryAfterMoving`): turns are looked for from 2 km before the end, a try where none fits beside obstacles took 0.33 s on the mini PC, and tried every 15 cycles from 300 m out the simulator's 10x ran at 2.6x (Couto da Ribeira, 24 Nov 2026); parked, it is not tried again. What a try again writes in the log is held (`YouTurnLog`, the planner's three loggers) and left out when it makes no turn again, one line saying so ("Tried the end again 31 m on: still no turn"): each try wrote 140 lines, and the dump reached back 11 s. The zone line near an end with no turn is written when it changes or every 10 m. Fork only. The order is also planned again when the headland line or the outer boundary changes (`SnakeFieldStamp`: every point of each to the centimetre; four points of each were read, and a corner moved elsewhere kept the old order), and forgotten with what was found about each pass when the line moves or the passes are laid at another spacing (`ForgetPassStatus`: Reference Line Nudge, an end trimmed, the implement's width or the overlap changed; a turn planned but not yet driven is planned again; the moved line's pass N is another strip, and the old answers turned the snake past clean passes on Tapada da Casa). With the passes where they were but the implement reaching further to one side or less far (its offset changed, or its width and the overlap by the same amount: `YouTurnCreationService.HardReach`), only the turn planned but not yet driven is planned again: it was kept clear of a hard fence or a hard obstacle by the old reach. In the headland the snake looks for its next turn only heading out of the field (no cultivated ground ahead on the pass): a turn that ends in the headland leaves the tractor facing the field on its new pass, which is not the end of that pass. Fork only: a pass worked on the whole (half covered, `IsPassWorked`) is still to do at the end ahead when less than half of its 20 m inside the cultivated area next to that end is covered (`YouTurnPathingService.IsPassOpenAtThisEnd`, asked in `WorkedPasses` when a turn is planned, once per end): on a tester's field a pass 71 % covered, with its last 100 m at that end left, was turned past. Such a pass is turned onto only when the turn built gets to what is left: at least 5 m of unworked crop along the turn's last 20 m and 20 m on past it (`YouTurnPathingService.TurnReachesUnworkedGround`); otherwise the turn is dropped and the pass counts worked at this end (`TurnMissesOpenEnd`). A turn lands abreast of where it leaves the tractor's pass, and the next pass's end can lie beyond that (a slanting headland, a turn seated back from a fence or a tree): on a tester's field the turn from 1L onto 2R, seated back 12.5 m, drove the whole of 2R a second time and its end stayed as it was. Fork only: with the job's map, worked means the map (half covered), never `Track.WorkedPaths`, which a section on anywhere along the pass sets (the mark is used only with no map); and a pass still to do on the whole is worked at the end ahead when the piece a turn there lands on (from that end back until the pass leaves the cultivated area) is at least half covered (`IsPieceDoneAtThisEnd`): a bay cuts the pass and its short piece is done. On Baetas (official #815, the job harness) the short pieces below a bay marked their passes worked and the main pieces were never turned onto, "End of field reached" with 53 % undone; a pass with no bay is one piece, so one done only at this end is still turned onto here. A manual U-turn with *Ignore worked tracks* steps over the passes the same map test calls worked (`WorkedPasses` into `ComputeNextTrack`), not the mark: it stepped over a pass with only a short piece done. Fork only. A turn the snake builds that lands where a turn landed before in the job (the same pass, going the same way, its end within 30 m) on ground already worked (`TurnReachesUnworkedGround`) is not made, and that pass counts worked at this end (`TurnRepeatsLanding`, the last 8 landings, forgotten with the track or a moved line): those tests judge a pass from its end to the headland line, through an obstacle the pass turns at, so a pass whose piece before the obstacle was done still looked to do, and the beta robot's snake went back and forth between two passes for the rest of the job (simulator: 280 turns between 4R and 6R, 689 between 8R and 10R). Ground left unpainted (sections off) is turned onto again. Test: `SnakeRepeatLandingTests`. Fork only. These end tests (`IsPassOpenAtThisEnd`, `IsPieceDoneAtThisEnd`, `YouTurnPathingService.EndStretch`; a curve with long bare stretches, drawn with a few taps, is walked a metre at a time) cut a pass at a U-turn obstacle's turn line as at the headland line (`TurnObstacleCuts`: the obstacle grown by the distance from boundary, and the implement's reach for a hard one), so the piece between an obstacle and the headland is judged on its own: judged through the obstacle, six such pieces done looked to do at both ends, and the beta robot's snake turned round them for the rest of the job (simulator, about 150 turns, landings more than 30 m apart). Test: `SnakeObstaclePieceTests`. Fork only. With the operator's headland on, a snake turn at the field's edge (not at an obstacle) that drives more than 10 m over the crop of the passes between the two it joins (inside the headland line, more than a pass from both: `YouTurnPathingService.CropCrossed`, `MaxCropCrossed`) is not made (`TurnCrossesCrop`): the next pass still to do is looked at, else the no-room message and the operator turns by hand. The omega, square to the passes and touching the turn line at one point, runs into the crop where the turn line bends away beside it: out of a pocket under a spike of the fence, onto a pass 13 over (the wide turn did not build, that pass meeting a U-turn obstacle's turn line first), it crossed 40-57 m of worked crop and landed in the middle of the pass (the beta robot, simulator). Test: `SnakeTurnAcrossCropTests`. Fork only.
- **The snake's search in a live cycle** (fork only): the snake builds a turn onto each pass it tries (the order's, the nearest still to do on each side, the next one when a turn is dropped), inside the GPS cycle and before the cycle steers. Near obstacles and a hard fence a build took 0.1 to 0.5 s on a tester's Surface Pro 4 and one search built eight: cycles of 1.0 to 1.24 s took AutoSteer off with "no GPS/guidance update for over 1 s" (#169) four times in 16 minutes (Couto da Ribeira, 23 Nov 2026). A live cycle (`GpsPipelineService.ProcessCycle`'s `live`: a receiver, or the simulator at 1x) gives the search `YouTurnStateMachine.LiveCyclePlanningBudget` (250 ms, `TickContext.PlanningBudget`): once it has built one turn and used that time it stops before the next build and goes on in the next cycle where it stopped (`StartSearch`, `GoOnNextCycle`; the passes built onto give the same answer, `_searchNotBuilt`, `_searchCrossesCrop`), nothing said or sounded and no next line drawn until it ends. A search not gone on with in the very next cycle starts again. Tests' synchronous cycles and the time-lapse have no budget: the whole search in one call, as before. Test: `SnakeSearchOverCyclesTests`.
- The end of a pass at a hard outer boundary with no turn planned or driven there ("End of field reached", no turn that fits, U-turns off): as the pivot gets 1 m into the headland from the cultivated area (`HeadlandEntryCheckAfter`), on the line and heading out (the pass leaves the outer boundary within 300 m with no 10 m of field on it first, `YouTurnPathingService.PassLeavesFieldAhead`), AutoSteer goes off with the alarm, "AutoSteer off: end of the pass and no U-turn here. Turn by hand." (`YouTurnStateMachine.EndOfPassWithoutTurn`, step 5b'''' in `GpsPipelineService`; once per entry; fork only). With automatic U-turns switched off the operator drives the headland on the line as far as a turn would have taken the tractor: the look waits until the pivot, or the implement's working centre (a front implement gets there first; `IToolPositionService.HasPosition`, as a fixed tool is never "ready"), leaves the turn line, and still at least the metre in ("AutoSteer off: end of the pass and automatic U-turns are switched off"); with the turn line nearer the fence than the fence guard's 2 s of travel the guard stops it first. A metre into a 24 m headland, AutoSteer went off with 130 m of the pass still to the fence (Couto da Ribeira, 20 Nov 2026, U-turns off on purpose to follow the line to the end of the field). A turn planned or driven there puts the look off, not away: lost in the headland with no other planned within `HeadlandEntryTurnWait` cycles (U-turn switched off, the obstacles changed, a turn that no longer fits), the end is looked at then; one planned again at once (a nudge, the skip, the arrow) is not a turn lost. Left on, AutoSteer steered on along the line through the fence (as AgOpenGPS, which only sends geoStop): on Tapada da Casa the simulator drove on 2.8 km. Heading into the field, a headland strip with field past it, or a soft boundary do not count. This stop, the line facing out of the field below and a pass still open at this end measure from the headland line, where the crop ends, not from the turn line the U-turn uses (`TickContext.PassEndLine`, the line the pipeline arms them on: from the turn line a headland more than 10 m wider than the U-turn distance never stopped AutoSteer). On a pass bent round Go around obstacles, "on the line" and the pass walked ahead are the bend's (`GoAroundPasses.Current`): a bend leaves the crop up to 45° off the pass, and measured against the pass the stop took the tractor for one turning by hand (Tapada da Casa, simulator: 3R bent round two trees in the headland left the crop 36° off, and AutoSteer steered on along the bend for 40 m until the fence guard stopped it with the 8 m boom over the fence).
- **The automatic U-turn switch** (the right-hand U-turn button, `youturn.toggle`, beside AutoSteer and acting on touch;
  not saved) is logged with its cause (`[YouTurn] Automatic U-turns off: U-turn button`, `MainViewModel.SwitchYouTurn`;
  a turn it drops too), is in a bug report's `runtime_state.json` (`Operation`), and switched off with AutoSteer on it is
  told at once (a toast). The obstacle-ahead and end-of-pass stops say "automatic U-turns are switched off" instead of
  "no U-turn before it / here": a Couto da Ribeira report (20 Nov 2026) had the turn before an obstacle dropped with no
  line in the log, and four AutoSteer stops read as no room to turn. A closed track turns them off (#421); an open track
  chosen after it turns them back on, with a toast, when they were on before it (`TurnYouTurnsBackOnAfterClosedTrack`),
  unless the U-turn button was pressed or guidance stopped (no track) in between; the cycle button still turns them off
  when they are on, as AgOpenGPS. They stayed off: on Eucaliptal (22 Nov 2026) the AB line chosen again after the
  Boundary Curve stopped AutoSteer twice at the end of the pass. Tests: `YouTurnSwitchLogTests`. Fork only.
- A turn planned for the end the tractor was heading to is dropped when the tractor turns round on the pass (by hand, or the simulator's turn-around), and the turn at the end ahead is planned (TURNED ROUND in `YouTurnStateMachine.Tick`, `_plannedSameWay`; AgOpenGPS drops a created turn once the pivot leaves the turn area without it, `ResetCreatedYouTurn`). Kept, the headland stop and the fence guard below took it for a turn planned at the other end and left AutoSteer on (Tapada da Casa, simulator). Fork only.
- Only a turn keeps the implement off a hard outer boundary. With none driven, AutoSteer on, going forward, the tractor inside the fence, an end of the implement (half `ActualToolWidth` plus the offset either side of the tool) heading out across the fence, more than 0.5 m out and further than now within 2 s of travel, takes AutoSteer off with the alarm (`HardFenceGuard`, step 5b'''' in `GpsPipelineService`; fork only). With a turn planned ahead the 2 s stop at its start (`HardFenceGuard.AheadOf`), which the turn keeps clear: the guard ran only with no turn planned, so it was off for every pass with its turn planned at the far end, and a pass driven through a stretch outside the turn line took a 12 m boom's end 1.1 m over a spike of the fence (the beta robot on Quinta Torta, simulator; `HardFenceGuardTurnAheadTests`). AutoSteer joining a line from a corner with the line moved five passes swung an 8 m front boom 0.9 m over the fence. The 2 s are along the curve steered, not straight ahead: the implement's ends turn with the tractor about its turning centre, from the last cycle's steer angle (the one the line asked for when AutoSteer has just gone on; a cycle with no line steered before it waits for the next). Straight ahead leaves a bend: on a boundary curve's first lap (the boom's end on the fence) it took AutoSteer off at a bend with the tractor on the line (`HardFenceGuardPipelineTests`). Joining a line sideways beside the fence is an S that keeps the implement inside (*Stepping onto a line beside a hard fence*). Driving along the fence a little over it, back in, or below walking pace (`MinLookAhead`) does not count. Every guard here works with the tractor inside the fence, so AutoSteer on with the pivot and the implement's centre (its side offset too) both more than 1 m outside a hard fence goes off with the alarm unless the line ahead comes into the field within 30 m (`YouTurnPathingService.PassComesIntoFieldAhead`, `OutsideFenceReach`; engaged just outside and steered in, as the farm job harness starts 12 m out, is left alone; the line as steered, with the nudge, the implement's side shift, a Go around bend and a front implement's move, walked a metre at a time on a curve too, against the field's own fence, not an open curve's end opening; the bent or moved line is last cycle's and is walked only for the pass it was made for, `SteeredLineNow`, as the end-of-pass stop and the line facing out of the field walk it): "AutoSteer off: the tractor is outside the field and the line does not lead into it. Drive into the field by hand." (`GpsPipelineService.OutsideFenceReason`). The beta robot on Baetas (simulator) rolled 2 m past the fence where the field's edge slants across the passes and engaged there: AutoSteer drove up pass 12R, outside the field all the way, until it was 34 m from the fence. Test: `AutoSteerOutsideHardFenceTests`. Fork only.
- Beside a hard obstacle chosen as *U-turn* the turn before it keeps the implement out of it, on the pass. Two ways round that turn take AutoSteer off with the alarm (fork only). **Off the line** (more than 1.5 m: engaged off it, moved a pass, another track), going forward with no turn driven, a point of the implement (every metre along it, half `ActualToolWidth` plus the offset either side) heading more than 0.3 m into a hard obstacle not driven through, and further in than now, within 2 s of travel along the curve steered: "AutoSteer off: the implement is heading into Inner N. Steer by hand." (`HardFenceGuard.HeadingIn`, `GpsPipelineService.HardObstacleReason`, step 5b''''). The line moved a pass over beside a hard obstacle took the end of a 12 m boom 4.5 m ahead of the pivot 1.2 m into it, the tractor crossing 50° off the passes and going by the obstacle itself (the beta robot, simulator). On the line the look-ahead would see a tree before the pass bends round it, so there the pass and its turn keep the implement out. **With a turn planned**, `ObstacleAheadWithoutTurn` took the turn to be before the obstacle; with the tractor already inside the obstacle's turn line and the obstacle itself straight ahead it is not (a turn keeps the tractor outside every turn line), and AutoSteer engaged on the pass 7 m short of a hard obstacle, the turn at the far end planned, drove through it (`TurnObstacleAhead`'s `insideOnly`). **On the line too, for a hard obstacle the pass turns at** (chosen as U-turn, or with no room to go round on this pass): the same look-ahead, stopped where a turn planned ahead starts, and an implement already more than 0.3 m in it, "AutoSteer off: the implement is in Inner N. Steer by hand." (`HardFenceGuard.In`, `HardObstacleInReason`). AutoSteer engaged inside the obstacle's turn line on a pass that goes by beside it has no turn there and nothing ahead of the tractor: a 6 m implement was driven 50 m with its end 2 m inside a hard obstacle a metre beside the pass (the beta robot on Quinta Torta, simulator). A Go around obstacle is still left to its bend on the line. **Off the line** an implement already more than 0.3 m in a hard obstacle counts whatever was chosen at it, and a tractor moving (over 0.1 m/s) looks at least 1 m ahead (`HardFenceGuard.MinLookAhead`, `MovingSpeed`): two seconds of travel at 1 km/h are 0.56 m, under the least the look-ahead looked at, so pulling away nothing was looked at. Put by hand beside a hard Go around obstacle 4.5 m off the line and engaged at 0.28 m/s, a 12 m implement was 1.2 m in it with AutoSteer on for 7 s (the beta robot on Casa, simulator). **Wherever the tractor is from the line**, an implement already more than 0.3 m in a hard obstacle takes AutoSteer off, a Go around one too: its bend gives the implement the safety margin, "off the line" starts at 1.5 m, and between the two nothing looked. Engaged 0.45 m off the pass at 1 km/h and heading away from it, the tractor got 1.3 m off before it came back and a 12 m implement was 0.4 m in a hard Go around obstacle (margin 0.5 m) with AutoSteer on (the beta robot on Olival, simulator). The look-ahead is still not used for a Go around obstacle on the line. Tests: `HardObstacleGuardPipelineTests`, `HardFenceGuardTests`, `ObstacleUTurnTests`.
- Joining a new line in the headland (U-turn settings, `GuidanceConfig.LineEntryMode`: Nearest point, the default and AgOpenGPS's; Ask; Start of the pass; `uturn.entryMode`, `uturn.entryStraight`, saved with the tool profile; fork only): when AutoSteer is engaged, the line moved a pass or another track chosen, with the tractor in the headland (inside the field, outside the headland line), going forward, on an open track, `GpsPipelineService.UpdateLineEntry` asks `YouTurnCreationService.PlanLineEntry` for the way to the start of the pass: where the pass, driven either way, enters the cultivated area nearest the tractor (not behind it, no more than 100° from where it faces, 150 m at most; with none there, the tractor faces out of the field and the way turns it round, if it fits), reached by the shortest Dubins path at the U-turn radius from 3 m ahead (`DubinsPathService.GeneratePaths`) that keeps the pivot in the headland until it is on the pass `LineEntryStraight` (plus a front implement's reach) before the headland line, inside the field, the implement off a hard fence (`ManualTurnClearsHardBoundary`) and clear of turn obstacles (`ClearsTurnObstacles`). Start of the pass drives it like a turn (`FollowLineEntry`, the auto U-turn waits; it ends on the pass a little inside the crop, where the line takes over); Ask offers it (`GpsCycleResult.LineEntryOfferPass`, the scene's `entryOffer` and dashed `entryPath`, the approach sound, `youturn.entryTake` / `youturn.entryKeep`, 30 s) while the nearest point is steered, and plans it again from where the tractor is when taken. No way through says so once (`LineEntryNotice`, a toast) and the nearest point is used. A way or an offer is dropped by AutoSteer off, a pass, nudge or track change (the track's points replaced by an edit too), a manual U-turn or reverse; an answer counts only for the offer that was on the screen when it was given, not for one made in the same cycle. The way starts with a point every step over its first 3 m, so the tractor is steered onto it from where it stands. In any mode, AutoSteer joining a line in the headland facing out of the field (the line, driven the way the tractor faces, reaches a hard outer boundary before any crop, within `YouTurnPathingService.FaceFieldReach`, 50 m: `YouTurnStateMachine.LineAheadLeavesField`) with no way driven or offered goes off at once with the alarm, "AutoSteer off: the line ahead runs out of the field. Turn the tractor to face the field." (step 5b'''' in `GpsPipelineService`); it steered the tractor to the fence (Tapada da Casa). With the fence further ahead (up to 300 m) the line runs along the headland and is driven: AutoSteer goes off with the alarm when the fence is `HeadlandPassStopReach` (20 m) ahead on it, "AutoSteer off: the fence is near ahead on this line. Turn by hand." (`_headlandPassWatch`; not while a U-turn, a way onto a pass or an S is planned or driven; ended by another pass or 10 m in the crop), and a sliver of crop on it is not taken for the end of a pass. Refused at any distance, pass 13L on Tapada da Casa (23 Nov 2026), in the headland band along the west fence with the north fence 155 m ahead, could not be sprayed with AutoSteer. Tests: `LineEntryPipelineTests` (`A_line_along_the_headland_...`). With Ask, an offer answered with *keep*, left unanswered for its 30 s, or that cannot be planned again when taken leaves the nearest point steered, so the same look is made then (`_lineEntryOfferUntaken`). Tests: `LineEntryPlannerTests`, `LineEntryPipelineTests`. With the skip mode *Ignore worked tracks*, AutoSteer engaged in the headland joins the nearest pass still to do (the snake's tests, `YouTurnStateMachine.IsPassToJoin`: not worked on the whole, and its piece from the end the way enters it at not done, a bay cutting the pass) that the way reaches straight, whatever the mode (Ask offers it), a toast naming it (`NearestOpenPassEntry`: the pass under the tractor, then one either side and so on up to 8, of two the shorter way; one the way can't reach is passed over); a lateral move or another track is left alone. Tests: `NearestOpenPassEntryTests`.
- An open curve whose ends face each other (a boundary curve made the long way round, A and B with an opening between them): A and B are the line's ends, no run-on past them, and the opening is a virtual boundary (`CurveEndOpening`, a strip of twice the U-turn radius plus the distance from boundary past A and B): the U-turn turns there into the next pass as at the field's edge. A turn that would hand over past the end of the next pass's line (the inner passes, whose two ends meet in a V, with a skip) is not made (`TurnLanding.PastLineEnd`). Past an open curve's end with no turn to take over, AutoSteer goes off ("past end of curve", AgOpenGPS `CABCurve`) instead of steering back (#694).

Key parameters:
- `UTurnDistanceFromBoundary` - the turn line's distance in from the fence: the turn keeps the tractor inside it (not the implement, as AgOpenGPS). Never negative (the setter makes it 0: a negative one put the turn line outside the field), and a value that is no number is the default, 2 m
- `UTurnRadius` - the operator's turning radius (never filled in from the tractor)
- `UTurnExtension` - the legs' length into and out of the arc
- The headland's width plays no part in the turn: with a headland narrower than the turn is deep, the turn leaves its pass inside the headland line, still working ("Headland too narrow for this U-turn").

## A front implement round a bend
AutoSteer steers the pivot on the line; an implement fixed `h` ahead of it (`ToolMount.FrontFixed`,
`RigGeometry.AttachX`: the vehicle's front hitch plus the tool's working point from it) runs outside a bend of radius R by √(R² + h²) − R. `FrontToolLine.Steered` moves each point of
the line steered (after the Go around bend; a closed pass's points one for one, so `PassUnfit` still lines up; an
open pass first gets a point every metre near both ends of each straight stretch, all along a short one, `WithPointsNearStretchEnds`:
a pass bent round Go around trees is straight between its bends, and moved one for one the stretch after a bend ran
slanted, the tractor and the boom 0.40 m beside the pass for 100 m on a tester's field) towards the
bend's centre by R − √(R² − h²), the pivot's bend never tighter than `Vehicle.MinTurningRadius`; R is measured over
`max(h, 3 m)` either side of the point half way to the implement, headings over 2 m chords (a loop's ends overlap a
little where it starts). That is a closed pass's line. An open pass's line is the one the implement's pull draws
(`FrontToolLine.TowedShifts`): the implement walked along the pass in the way it is driven, the pivot following it
as a trailer follows its hitch, `h` behind; on a steady bend that is the same √(R² − h²), and it also holds through a
short S, which the window above averaged out: round a Go around tree (out of the pass at 45° and back, 6 m bends) the
Maxxum's boom 4.8 m ahead cut the first bend by 1.5 m and sprayed 3 m² inside the tree (the farm job harness on a
tester's field). Back on a straight stretch the pivot comes onto the pass as e^(−s/h). The pivot's own turn in the pull
is held to `Vehicle.MinTurningRadius`: on a bend tighter than √(r² + h²) (a recorded curve's S; Go around bends for a
front implement are never that tight) the pull alone had the pivot turn on the spot, down to a 1.5 m bend on a 6 m S;
held, the implement leaves the pass as little as the tractor allows. A pass that doubles back tighter than that (a
hairpin at the turning radius) still loops, and an open line bending under 0.9 of the turning radius anywhere
(`FrontToolLine.TightestBend`, over 2 m chords) is not used: the pass is steered as it is. Cached per pass and per way it is driven in
`GpsPipelineService.FrontToolSteered`; a line with no bend (an AB line no tree bends) is steered as it is. The pass shown, the
implement's band and U-turn, line-entry and S paths are not moved. Rear and trailed implements are not changed.
Tests: `FrontToolLineTests` (`Round_a_go_around_tree_the_implement_stays_on_the_bend`, `An_open_pass_is_never_steered_tighter_than_the_tractor_turns`), `FrontToolCurveTests`. Fork only.

## Going around obstacles
Inner boundaries chosen as *Go around* (`ObstacleAction.GoRound`) bend the pass round them
(`ObstacleDetour` in `Services/Track`). Each is grown by half the implement (and its offset, or half
the tractor's track if wider) plus the safety margin, and the pass is moved sideways as far as the
grown outline reaches across it, on the side needing the smaller move; obstacles close together
along the pass are gone round together, on one side. The sideways offset rises no steeper than 45°
and is rounded (Lasry-Lions: parabolas of 2R over it, then of R under it) so the bend is as short
as the curve radius allows and never less than the obstacle needs. The radius and the margin are
U-turn settings saved with the tool profile (`GuidanceConfig.GoAroundRadius` / `GoAroundMargin`,
`uturn.goAroundRadius` / `uturn.goAroundMargin`; 6 m and 1 m by default; 1-50 m and 0.3-10 m, held to
that when a profile is read too). A trailing or TBT
tool adds how far its working centre settles inside a curve of that radius (`ObstacleDetour.TrailedCutIn`,
the `ToolPositionService` geometry) to the margin, since round the obstacle the inside is its side. A
bend that enters another grown obstacle, a *U-turn* obstacle, or comes closer to the field's edge
tries the other side; with no room either way the obstacle is driven through and the operator gets a
toast (`GpsCycleResult.ObstacleNotice`). A bend also keeps the implement out of every *U-turn* obstacle
it passes (never nearer than half the implement, unless the pass itself already runs nearer), and an
obstacle whose outline could not be grown and lies across the pass is told as one with no room. The bend
is built once per pass and cached (the key holds every corner of every obstacle and of the field's edge,
`BoundaryPolygon.OutlineStamp`); it is the line
steered (`CalculateTrackGuidance`), the line shown and the light bar's line, AutoSteer on or off.
U-turn planning and pass numbering keep the straight pass.

**Slowing down before a bend** (fork only). With AutoSteer on the line (Pure Pursuit), a bend round a Go
around obstacle within 6 s of travel ahead (15 m at least) that the tractor is too fast for is told once each time
it comes to it, with a sound (`SoundEffect.GoAroundSlowDown`): "Slow down to N km/h for the bend round Inner X"
(`GoAroundSpeedWarning` in `Services/Pipeline`, `GpsCycleResult.GoAroundSpeedNotice`, a `[Go around]` log line
with the numbers). Only a warning: nothing steers or slows. Too fast is cutting the bend into the obstacle grown
by the keep-out by more than the safety margin in force less 10 cm (`GoAroundSpeed.Allowed`), worked out by
driving the line steered in a model of the app's own steering (`GoAroundSpeed.Cut`: the Pure Pursuit goal the
look-ahead ahead, as `GoalLookAheadFor` on the line, the wheels 0.2 s late and at most 15°/s, a bicycle of the
wheelbase), once per line, obstacle, way and half km/h. N is the fastest whole km/h that keeps within it; none
from 2 km/h up: "Slow right down ... (raise the margin)". What cuts a bend is the look-ahead, which grows with
the speed and with Steer Response: on the obstacle rig a 5 m bend lost 0.2 m of its margin from 4 to 12 km/h
with Steer Response 3 and twice that with 4, while wheels turning at 15, 25 or 35°/s changed it by under a
centimetre. Stanley is not looked at. Tests: `GoAroundSpeedTests`, `GoAroundSpeedRigTests` (its explicit `Table`
compares the speed told with the rig's own clearance by radius, margin, Steer Response and implement).

**A boundary curve's laps** (closed tracks) are bent too (fork only). They were not: on a tester's field
(Eucaliptal, 22 Nov 2026, the real tractor, an 8 m front boom) lap 2 was shown and steered straight through two
hard Go around obstacles. A lap steered is bent as a loop (`ObstacleDetour.Compute`'s `loop`, asked by
`GoAroundPasses.For` with `Lap.Whole`): started at the vertex furthest from every Go around obstacle
(`FromSeam`), so no bend is cut at the lap's seam, and a bend that would reach round the whole lap is not made
(the obstacle is blocked). The piece of a lap shown beside a neck (`FitPieceShown`) is bent as an open line
(`Lap.Piece`); the U-turn planner asks with `Lap.None` and gets no bend, as before (laps have no U-turns). A front
implement's line on a bent lap is the implement's pull round the loop (`FrontToolLine.TowedLoop`: the lap three
times, the middle one kept). The neck check (`PassUnfit`) is made on the unbent lap. An obstacle with no room
either side on a lap is a blocked obstacle: AutoSteer goes off 10 m plus 2 s before it, with the toast "No room
to go around Inner N on this lap: steer round by hand". Tests: `ClosedLapGoAroundTests`, `ObstacleDetourTests`
(`A_lap_is_bent_as_a_loop...`), `GoAroundPassesTests`.

## A closed track's pass across a neck
On a closed track (a boundary curve) the offset leaves out the points too near the reference and joins what
is left, so where the field is narrower than twice a pass's offset the pass runs across the gap along the
fence (a tester's field: the second lap 0.1 m from the fence of a bump, an 8 m boom). `CurveProcessing.PassUnfit`
marks the points nearer the reference than the offset by more than half a pass (never under 0.5 m; a bend
opened to the turn radius is let off by how far it moved, judged against the pass as offset), worked out once
per pass (`OffsetPassCache.Unfit`, `GpsPipelineService.PassUnfit`). The pass shown, the implement's band and
the guidelines leave that stretch out (the stretch nearest the tractor: `FitPieceShown`, `CurveProcessing.FitPieces`),
and with AutoSteer on, such a stretch within 10 m plus 2 s of travel ahead on the pass steered
(`CurveProcessing.UnfitAhead`, from `CalculateTrackGuidance`) takes AutoSteer off with the alarm
(`GpsPipelineService.PassDoesNotFitReason`). An implement shifted to one side runs on the pass's centre, the
tool offset beside the line steered: its own line is looked at too, since across a neck the tractor's line can
fit where the implement's does not. Tests: `ClosedPassFitTests` (Models), `ClosedPassNeckTests`. Fork only.

**The seam.** A closed track's last point repeats its first, so `CreateOffsetCurveWithInfo` offset the seam's
point twice (along the last stretch's normal and the first's); on the inside of the bend there the second copy
lay behind the first, the pass ran 0.2 m on past the seam and back (a 26 m lap), and a tractor engaged 0.4 m off
the line at the seam was steered to a goal behind it and turned round. On the inside the repeat is left out (on
the outside it starts the seam's arc, as before). The arc round the seam goes through the collision test like any
other: untested, a seam at the tip of a narrow slot into the field (a tester's field, its slot reaching the far fence)
put the whole arc round the tip into the lap, out across the fence beyond it (8.7 m on a 12 m lap), and the lap
crossed itself coming back, so AutoSteer engaged near the crossing could take the other leg. Tests:
`ClosedPassSeamTests` (Models), `ClosedPassSeamPipelineTests`. Fork only (the official code has both).

## Stepping onto a line beside a hard fence
AutoSteer joining a line sideways (engaged off it, moved a pass, another track; `UpdateSideStep` in
`GpsPipelineService`, after the line entry) with a hard outer boundary: a front implement swings out ahead of the
pivot as the tractor straightens onto the line (a point h ahead on a curve of radius R goes past it by about
h²/2R), and on a tester's field the lateral move onto the lap 4 m from the fence took an 8 m front boom (4.5 m
ahead) across it until `HardFenceGuard` stopped AutoSteer. `SideStep.Plan` makes the way onto the line: an arc at
the U-turn radius (or the tractor's turning radius, if larger) bringing a steep heading round to 20°, 10° or 0°
from the line's, then a quintic S in the line's own measure (offset, heading and bend matched at both ends),
the shortest from 10 to 150 m along the line that bends no tighter and along which the implement's ends (a rigid
model: `SideStepTool`, the working centre ahead of or behind the pivot, a trailed implement at the hitch) stay
inside the fence or no further out than the line itself puts them plus 0.1 m, and the fence guard's own check
(2 s of travel at the speed, at least 2 m/s) would not fire more than on the line. Planned only when an end comes
within 2 m (plus the implement's distance ahead or behind) of the fence on the shortest such way, the tractor more
than 1 m off the line and heading within 75° of it, and not near an obstacle (a corner or an edge of it within 2 m
of the S: the pass may bend round it), nor in the cycle a manual U-turn is asked for (the operator's move). On an
open pass with U-turns the S and the 20 m after it stay in the cultivated area once in it (U-turns are not planned
while it is driven). Driven like the way to the start of the pass (`_sideStepPath`, drawn as `LineEntryPath`),
the line taking over at its end; dropped when the line or the pass changes, AutoSteer goes off, a manual U-turn is
asked for, or the tractor reverses. No S fits, or beside the fence the line ends or every S bends too tight before
one could: AutoSteer off with the alarm (`SideStepNoRoomReason`). The fence
guard keeps running on the S. Tests: `SideStepTests`, `SideStepPipelineTests`. Fork only.

## Passes outside a hard fence
A pass lying wholly outside a hard outer boundary is never worked, so it is never shown or moved onto
(`HardFencePasses`; fork only). With AutoSteer off the pass picked is the nearest one not wholly outside
(`UpdateDisplayTrack`), and a lateral move onto one is refused with a toast (`GpsCycleResult.PassNotice`). An AB
line's pass is outside when every corner of the fence is on one side of it; a curve's is looked at as its
reference moved sideways every 2 m (a closed track's stretch back to its first point too) without the offset's clean-up (a corner it crosses into the field counts as
inside), an open curve's with its 200 m run-on past each end. Worked out once per pass and kept until the track,
the fence, the width or the nudge change. On a tester's field the simulator drove on out of the field after
AutoSteer went off, and the lap outside the fence of the boundary curve was shown. A soft boundary changes
nothing. Tests: `HardFencePassesTests`.

## A short AB line, and a pass with no line
AgOpenGPS's A+ line keeps B about 1 m from A, and an offset pass leaves out every point within 1 m of the one
before it, so every pass of such a line but the line itself had one point and no line to steer: on Couto da
Ribeira (the beta robot, simulator) AutoSteer held the wheels straight through the hard fence 24 times,
since the pipeline steered 0 with AutoSteer left on and the hard fence guard needs a line. An AB line under 10 m
is read with A where it was and B 100 m out along its heading (the stored one when it agrees with A to B within
5°, as AgOpenGPS keeps it more exactly than the rounded points; `Track.LengthenShortABLine`, from `field.geojson`,
`TrackLines.txt` and `ABLines.txt`), and AutoSteer on a pass with no line goes off with the alarm
(`GpsPipelineService.NoLineReason`). Tests: `ShortABLineTests` (Models), `ShortABLineLoadTests`,
`NoLineAutoSteerOffTests`. The reading is official since #847; AutoSteer off on a pass with no line is fork only.

## The fallback U-turn
The simple fallback (`SimpleFallback`, a semicircle, used with a soft boundary when no real turn fits) is seated
with the path as it is driven: smoothed at each step (`TurnPathSmoothing.Smooth`, which pulls an 8 m arc about
0.2 m inwards, a tighter one more), so the distance from the fence and `LeavesField` are held by the smoothed
path; checked unsmoothed, a notch of the fence inside the bend was 0.95 m from the path with 1.5 m asked. The
last step is the limit itself (the headland crossing plus 30 m, or the tractor): a step past it skipped a turn
that fitted only there. When the U-turn arrow is refused ("U-turn direction not changed", `RefuseDirectionSwap`)
in the cycle where no turn builds, the operator reads why there is no turn (`ReportNoTurn`), not the arrow's
refusal with the failure sound. Tests: `FallbackTurnInsideBoundaryTests`, `FailedEndRetryTests`. Fork only.

## Steer wizard: tuning the wheels
The Steer Wizard's *Tune steering* step (`TuneSteeringStepViewModel`, after the Ackermann circle and before the
gains page; arithmetic in `SteerTuneMath`; fork only) finds the three settings of the module's own loop,
Minimum To Move, Max Limit and Proportional Gain, by moving the wheels a few degrees through Free Drive, standing
still or at walking pace (not above 6 km/h to start, 10 km/h stops it; wheels within 8° of straight). In order:
- **Stop check**: before anything moves by itself the operator stops the module once with the stop the tractor
  has (`StopKindFor`: steer switch or button, a turn sensor or a pressure/current sensor with a trip point, else
  the page's Stop button, and the page says that is the only stop), and the step sees it stop. A turn sensor set to
  trip at 0 counts lets go at once, so the step says that before anything moves.
- **Minimum To Move**, each side: the module's PWM raised quickly, then slowly, until the wheels move; then that
  PWM less one is held and must leave the wheels still (a steering that answers late reads high on any rise). The
  setting is a little under the easier side.
- **Max Limit**: the PWM that turns the wheels at the speed chosen (15, 25 or 35°/s, `wizard.set|TargetWheelSpeed`).
  When the wheels stop getting faster before that (a valve fully open, a motor at its top speed), the lowest PWM
  that gives 90 % of the fastest they turned (`SteerTuneMath.Knee`, `KneeShare`), and the result says above which
  PWM they turn hardly any faster: on a tester's proportional valve (17 Nov 2026) the step took PWM 162 for 2°/s
  more than at 103, 3.9 A on a coil rated 2.5 A. It is never under 50 (the module's lowest) nor close to Minimum
  To Move: held there, a quick steering turns faster than chosen, and the result says how fast
  (`SteerTuneMath.SpeedAtPwm`).
- **Never above the operator's Max Limit**, in any part of the run nor in the result (`PwmCap`; the app can't
  know what a valve's coil or a motor takes): the first-movement search stops there, the speed levels stop there,
  and a result held there says so.
- **Proportional Gain**: 4° turns up a ladder of gains; the lowest gain that settles as fast as the best one
  without swinging, then checked with an 8° turn.

The test values are in the settings only while it runs. Every way out (Stop, ✕, Next, Back, the browser lost, the
steer switch, a kickout, wheels held, wheels moving while only held or the wrong way, too fast) lets go of the
module and puts the operator's values back; the values found are used only after *Use these values* and saved with
the profile on Finish. With no kickout sensor the step itself notices a hand on the wheel (wheels that stop
answering; the wheel taken at every tenth of a second of a run on three simulated steerings is let go within 2.5 s). Web client: `case 'tune'` in `js/wizards.js` (buttons shown by
`testStage`; the result's `Measurements:` tail is folded away). Tests: `SteerWizardTuneTests` on a simulated
steering (`FakeSteering`); the tuning stories of the steer wizard harness (`TuneScenarios.cs`). First run on a
tractor 17 Nov 2026 (a load-sensing valve block, standing still): Minimum To Move and the gain looked right; the Max
Limit rules above came from it.

**What the tests found goes to the log** (`SwitchGatedWizardStep.Log` / `LogLines`, wired by
`MainViewModel.NewSteerWizard`; fork only): the three steps that move the wheels (motor test, maximum steering
angle, tune steering) write every result or refusal the page shows as `[Steer wizard] <step title>: ...` lines, a
line of the page a log line, so the tune's `Measurements:` (first movement per side, wheel speed per PWM, each gain
tried) are in a Bug Report Dump. Tune steering also logs its start (wheel angle, speed, the three values in use,
the stop the tractor has) and, when a run is stopped, what it had measured by then; the max angle test logs the
start angle with the two pushes. Before this a dump showed only `[Steer settings]` changes, and reading a tractor
run (18 Nov 2026) needed a screenshot of each result and the tester's typed readings. A new result text of these
steps needs nothing more: it is logged when `PhaseResult` changes.

## Steer wizard: wheels that stop short of a lock
The *Maximum Steering Angle* step (`MaxSteeringAngleStepViewModel`) takes no reading under 20° either side as the
lock (`ShortLockDeg`): most steering turns 30° or more each way, and wheels that stop that short ran out of push or
turned too slowly for its plateau test (under 2°/s). On a tester's load-sensing block with its relief valve still at
the lowest (17 Nov 2026) the wheels stopped at 12° right and 3.6° left standing, 36° by hand, and 90 % of 3.6 became
the Max Steer Angle every line was steered with. Such a result, and wheels that turn one way only, keep the setting
and end with what to check (`WhatToCheckHeader`, `WeakSteeringChecks`: the AutoSteer block's relief valve a quarter
turn at a time, never the tractor's own nor past the rated pressure; the hoses and LS line; a Keya: the engine
running, the current trip (standing still the steering wheel is at its heaviest), a slipping mount; Max Limit within
the coil's rating), which the page folds away (`data-fold="howto"` in `js/wizards.js`, as the
tune step's measurements) and sets out as a list (`wzHowTo`: one point a line, "• " a case, "– " a kind of steering,
"1. " a step, "· " a point; as running text the tester could not find his own steering's part). The motor test's stalled reading points there too. A result line that starts with
`WizardStepViewModel.WarningMark` ("⚠ ") is shown in an amber box (`wzResult`, `.wz-warn`): in the same font as the
rest of the result, the tester read past it. "Turned right but hardly left" is said only when the side that went
reached 20° or more from the start (`LockMovementError`): the left reading is taken after the right one and the start
is anywhere between the two stops, so with too little push one reading lands beside it. The same block read start 1.9°,
right 7.7°, left 0.7° (18 Nov 2026), and the one-side wording sent the tester looking for a fault on the left; such
readings now say how far the wheels moved in all and where they stopped each way. A push whose 6 s ran out with the
wheels still moving at its last reading and 1° or more further on than a second before (`StillTurningDeg`) read no
lock either (wheels that stopped just before the time ran out, too few steady readings for a plateau, read their lock:
the approach alone is still in the last second; official #882, Sourcery): the result
says which side ran out of time (`StillTurningNote`) and the setting is kept. The left push starts from the right lock,
so a steering slower than 12°/s over 36° locks read the left side short, and 90 % of that was saved (36° right, 25° left
at 10°/s: 22). Every amber result of this step leaves Max Steer Angle as it was. Tests: `MaxSteeringAngleStepTests`. Fork only.

## Steer wizard: closed by the host
When the screen in control goes away (its link quiet for the deadman's 1.5 s, its socket dropped) the host closes
the steer wizard (`MainViewModel.OnBrowserControlEnded`), and a page that comes back from that can still have the
wizard on it: the window covered by another, a tablet that slept. Its buttons then did nothing, the host having no
wizard to take them (a tester's tractor, 18 Nov 2026: a tuning run's values on the screen, no way to take them or to
leave the step). The host says it closed it: `MainViewModel.SteerWizardClosedByHost` (set there, cleared when the
wizard is closed from a screen or opened again) puts `WizardDto.ClosedByHost` in the Wizard frame (its last byte,
`Closed`), sent once (`MapBroadcaster.WizardToSend`) and in the seed of a screen that connects again. The page
(`wzHostClosed` in `js/wizards.js`) then shows that the wizard was closed and why, a tuning run's values found and
not taken (the lines of the last result before "Not in use yet"), and Close as the only button (`wzShowGone`,
`.wz-overlay.gone`; `wzGoneReset` clears that message, or a wizard opened again showed it until its first frame);
Close from the screen in control sends `wizard.cancel`, which clears the flag. Before official #883 (Chris's review)
the page counted Ticks with no Wizard frame, which was a guess the host can answer. The host writes why control
ended and what it turned off in the log (`[Control] The screen in control is gone (...); turned off: ...`), and a
tuning run's values lost with the wizard (`[Steer wizard] Closed with the tuning values found and not taken: ...`):
a bug report had neither. Tests: `SteerWizardSafetyTests` (`Browser_lost_says_in_the_log...`,
`Browser_lost_with_tuning_values_not_taken...`, `Browser_lost_with_the_wizard_open_tells_the_screens...`),
`WizardFrameTests` (RemoteServer). Fork only.

## Steer wizard: the motor test's minimum PWM
The *Auto Motor Calibration* step (`AutoMotorCalibrationStepViewModel`) holds a 5° Free Drive target, raises the
gain and reads the module's PWM where the wheels come to rest: the minimum that moves them. The module's PWM is the
gain times the error plus its minimum PWM, so the ramp runs on a minimum PWM of its own (`RampMinPwm`, 5, or the
operator's if lower; the operator's is put back unless a new one is saved): on the operator's value nothing under it
can be read, and with 64 set (saved while a block's relief valve was too low) a valve that moves from 48 read 64 on
every run (a tester's tractor, 18 Nov 2026). A reading of two thirds of Max Limit or more is a stall and is not saved
(`IsStalledReading`, `StalledShare`). It was half: a proportional valve held to its coil's current works between about
45 and 90 to 105, so its true minimum (42 to 48 on two tractors) read as stalled. Standing still the tyres hold the
wheels (57 standing, 48 at walking pace on that tractor), and the refusal says to run the test at walking pace.
Leaving the step mid-ramp puts the operator's gain and minimum PWM back at once (`RestoreOperatorSettings`): Cancel
or a lost screen leaves the step and saves the profile straight away (`EndRemoteSteerWizard`), and the ramp's
finally runs only on the host loop's next turn, so the ramp's own values (gain 5 to 200, minimum PWM 5) were saved
(official #881, Sourcery). Tests: `AutoMotorCalKpRampTests`. Fork only.

## Tuning on the line
The AutoSteer page's *Tune on Line* panel (Full view, beside Smart WAS; `LineTuneViewModel` over
`LineTuneRun` in `ViewModels/LineTune`; fork only) tunes the guidance gains with AutoSteer on a straight
line at 3-20 km/h: for each value tried the line is nudged 0.30 m right through the nudge intent
(`IPipelineIntents.RequestGuidanceNudge`) and back, and the run times how fast the cross-track error
settles, how far past it goes and how often it swings. Pure Pursuit: Steer Response (look-ahead hold) down
or up a ladder from the operator's own value; Stanley: Aggressiveness, then two Overshoot Reduction trials.
The value tried steers through `IGpsPipelineService.SetGuidanceGainsOverride` (the pipeline reads the
override instead of `GuidanceConfig`), so the settings change only on *Use these values* (saved with the
vehicle profile). A value that runs away (0.25 m past where it started, half the step past the line, three
swings) is cut at once: a calm value steers back to the moved line before the line moves back. AutoSteer
off, a speed change, a turn or the headland near, no RTK, contour, reverse or a hand nudge or snap pause or end
the run, with the line moved back and the operator's values steering again; a steering setting changed (the
integral gains too, and what the module and the guidance take from the AutoSteer and vehicle settings: the module's
gain and PWM limits, WAS zero and counts, Ackermann, wheelbase, Max Steer Angle, the antenna's place), another vehicle profile loaded or the browser in control gone ends it, and a result waiting
to be taken is dropped by such a change (`LineTuneRun.DropResult`): it was found for other settings. Only a nudge
that is exactly the run's own move is the run's (a hand nudge in the same cycle ends it), and a move that never
shows on the line is taken out of what the run still has to move back. The calm value steering back is never
quicker than the operator's own (`RescueFor`). The override is for the line only: a U-turn and contour steer
with the operator's gains. State rides the Status frame (`LineTuneDto`, `statusBar.lineTune`), commands
`linetune.start|stop|accept|discard`, client in `js/config-panels.js`. Tests: `LineTuneRunTests` on a simulated
tractor with the real `TrackGuidanceService` and a steering with dead time, rate limit and lag (`LineTuneSim`).

Rules from the first runs on tractors (17 and 18 Nov 2026, 3.3 to 6 km/h; keep them at any change):
- **No value starts while the tractor is on its way to the line**, however early Start was pressed: further
  than `JoinNearM` (0.5 m) the run only waits, with no time limit; near the line it waits for the operator's own
  values to hold it for `JoinHoldS`. If they never do within `JoinTimeoutS` the value starts all the same (it is a
  value to measure); if they keep swinging the tractor across the line (`JoinSwingM` out on one side, then the
  other, `JoinSwingTurns` times running), the calm value steers first. Once across is a tractor joining at an
  angle (one went 1.95 m past, engaged in a turn): it is left alone, and a change of pass starts the wait anew.
  A value put in while AutoSteer was still joining the line was blamed for the join.
- **Every wait is as long in metres at any speed** (`TimeFactor` = `FullSpeedKmh` 8 / the speed, never under 1):
  the guidance takes about as far, not as long, to come onto a line. The run measures at the speed driven, the
  panel says to drive at the speed worked at, and the result names the speed it was found at.
- **The speed is a mean of `SpeedMeanS`** (1 s): a receiver at a steady 6 km/h reads 5.0 to 7.3 from one reading
  to the next, and one reading out of the band dropped a value.
- **Where the tractor rests is not where it was**: with slack in the steering it rests 4 to 9 cm from where it was
  before a move. The quick way to settle is unchanged (back within the band of where it was); when the time is up
  and the tractor rests steady within `SteadyMaxM` of where it was, the move counts, measured again against where
  it rests (`MeasureAgainst`). Never take a steady stretch earlier than that for a rest: the turning point of a
  slow swing looks like one.
- **No clean value is still an answer**: when none is good, the value that came back both ways and went past the
  line the least is proposed (`LineTuneLevel.CameBack`), and the result says that a calmer value does not cure it
  (slack, Minimum To Move). The calm value after a cut has only to calm the tractor, anywhere beside the line.
- **What the run did goes to the log** (`LineTuneRun.Log`, `[Line tune] ...` lines): one line per value and move,
  with the numbers, so a bug report tells what happened without reading the positions.
- `LineTuneSim` with the steering a Steer Wizard measured (24 deg/s, 0.19 s late) matches a tractor at 3 to 4 km/h
  and is far too pessimistic at 6: do not read field advice from it.

## CI/CD

GitHub Actions workflows, split by purpose:

- **`build-and-release.yml`** ("CI") — on every push / PR to `main`: runs the test suite plus a
  compile-check of each platform head (Desktop, Android, iOS). No packaging, no releases.
- **`build-deploy-bundles.yml`** — the single packaging + release publisher. Runs the
  `deploy/{linux,windows,macos}/package.sh` scripts + builds the signed Android APK on clean runners:
  - on a **`v*` tag** (or manual dispatch with a tag) → publishes one complete Release, marked
    prerelease so it never shows as "Latest" (the beta is the build users run), with every
    artifact: Linux daemon (x64/arm64) + desktop launcher (x64/arm64) tarballs, Windows zip
    (launcher + service installer), macOS `.dmg`, and the Android APK; its notes end with every
    PR merged since the previous version tag (GitHub's generated notes, `.github/release.yml`);
  - on a **daily schedule** → refreshes a rolling `beta` prerelease with the same artifacts, only
    when `develop` has moved on since the beta was built (otherwise the run builds nothing);
  - on a manual dispatch, by its `publish` input: `beta` (default) refreshes the rolling
    beta from `develop` right away (title carries build time + commit); `release` publishes
    the Release named by `release_tag`; `none` builds + uploads artifacts only (dry run).

- **Scenario harnesses, nightly** — eleven workflows, each running its harness on `develop` (when it has a
  commit from the last 25 hours) or by hand, keeping its findings report as an artifact and in the run's
  summary; PR CI only builds them. GitHub's own schedule started them 5–8 hours late every day, so the bug
  report Worker starts them (`Tools/bugreport-worker`, `NIGHTLY_CRON` 05:07 UTC, 23:07 US Central, `NIGHTLY_WORKFLOWS`
  in its `wrangler.toml`: a change there needs `wrangler deploy`) as a `workflow_dispatch` with `nightly=true`, which keeps the 25-hour check;
  side by side, each on its own runner: `field-scenarios.yml`, `steer-wizard-scenarios.yml` (also fails when its safety watch saw the
  wheels move when they shouldn't), `vehicle-tool-scenarios.yml`, `machine-module-scenarios.yml`,
  `gps-ntrip-scenarios.yml`, `job-scenarios.yml` (the five job harnesses, up to about 80 min, one combined report), `isobus-scenarios.yml`,
  `web-client-scenarios.yml`, `update-scenarios.yml`, `working-day-scenarios.yml`, `dump-replay-scenarios.yml`.
  The slow ones that rarely change (the Vehicle & Tool settings sweep, working day, update, dump replay) run only when a
  commit of the last 25 hours touched what they cover, and every Sunday whatever changed (`.github/scripts/nightly-fresh.sh`,
  the paths in each workflow; add a path there when a harness starts covering more). Each harness's `README.md` says what it covers.

To cut a release (about weekly, so each one stays small): bump `sys/version.h` and the three heads' display versions, then push a tag — `git tag v26.7.x && git push origin v26.7.x`. A summary on top of the generated list goes in with `gh release edit`.

## Legacy Code

- `ABLine.cs` - Marked `[Obsolete]`, retained only for AgOpenGPS file I/O compatibility
- Use `Track` model for all new guidance code
- `TestRunner/` - Legacy console test harness, superseded by NUnit test projects in `Tests/`
- `Simulators/AgOpenWeb.VehicleSimulator` - Avalonia desktop dev tool, kept as is
