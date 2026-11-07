# ISOBUS scenarios: the careful and the careless operator

AgOpenWeb's whole backend runs in-process with its real UDP service on port 9999. An emulated AOG-TaskController (PGN 240 every 100 ms per implement with the implement's actual sections, PGN 221 messages, PGN 229 / 241 / 242 from source 0x7F, as the real one) listens on 127.0.0.1:8888 with the implements on its bus (sections that follow the setpoints after a valve delay in automatic, their own switches in manual); a steer module stub sends PGN 253. A tractor sends RTK fixes at 10 Hz in real time, driving north on fresh ground with master Auto. The screen's sections, the setpoints the task controller received, the implement's sections and what AgOpenWeb shows of them are compared every 100 ms.

## Findings (each kind once, most serious first)

1. **Bug** `ISOBUS-NO-SECTIONS-UNDER-17` ×1: Spraying 10 s: the screen had sections on for 10.0 s and painted 264 m², the task controller received PGN 254 ×1447 and 239 ×1447 but no PGN 229 at all, so the 8-section implement did nothing (its sections stayed 00000000). AgOpenWeb sends PGN 229 (the 64 section states) to the modules only when the tool has more than 16 sections (AutoSteerService.SendPgns: "below 17 sections, 239 is sufficient and 229 is skipped"), but the task controller reads the sections from PGN 229 only (AOG-TaskController app.cpp handles 0xE5, 0xF1, 0xF2 and 0xD6; not 254 or 239). So an ISOBUS implement with 16 sections or fewer never gets AgOpenWeb's section states: in automatic section control it does nothing while the screen paints coverage. AgOpenGPS sends 229 whatever the section count (AgIO relays it to the modules), and so does AgOpenWeb's own loopback copy for rate controllers (SendLoopbackPgns), which the task controller does not listen to.
   Seen in: C1
2. **Improvement** `ISOBUS-NO-GNSS-QUALITY` ×1: in 14 s the task controller received no PGN 214 (AgIO's GPS data, source 0x7C): AgOpenWeb sends it to the loopback port 15555 only (SendGpsDataToLoopback), and the task controller listens on 8888, so it never learns the fix quality and tells every implement DDI 514 = 1 (plain GPS) whatever the receiver has, as the AOG-TaskController protocol notes for a default AgIO install; some implements gate section control on the GNSS quality
   Seen in: C2
3. **Improvement** `ISOBUS-REQUEST-REFUSED-SILENT` ×1: the implement did not take automatic section control and AgOpenWeb said nothing: the button just stays on manual, as if the tap had not registered (AgOpenGPS does the same)
   Seen in: K2
4. **Improvement** `ISOBUS-SECTION-COUNT-MISMATCH-SILENT` ×1: the tool has 20 sections and the implement reports 12; AgOpenWeb paints 20 and says nothing: sections 13-20 show as worked on the map but no implement has them (the heartbeat carries the implement's count, so a one-line warning is possible)
   Seen in: K7
5. **Improvement** `ISOBUS-TC-LOST-SILENT` ×1: with the task controller gone for 30 s AgOpenWeb kept the sections on and painted 775 m² of coverage no implement was told about; the only sign is the ISOBUS button disappearing (no alarm, no message), while a lost machine module at least changes the status dot
   Seen in: K4

## Careful operator

### C1. An 8-section sprayer on the task controller, section control automatic

The task controller and the steer module are on, the sprayer (8 sections, 12 m) is registered; then AgOpenWeb starts. The operator types the tool (8 × 150 cm), opens a field, starts the job with master Auto and taps the ISOBUS button for automatic section control.

- ✓ the ISOBUS button shows: 8 sections, section control off, implement sections 00000000
- ✓ one PGN 241 for the tap, none repeated once the heartbeat showed it
- ✗ **Bug** `ISOBUS-NO-SECTIONS-UNDER-17`: Spraying 10 s: the screen had sections on for 10.0 s and painted 264 m², the task controller received PGN 254 ×1447 and 239 ×1447 but no PGN 229 at all, so the 8-section implement did nothing (its sections stayed 00000000). AgOpenWeb sends PGN 229 (the 64 section states) to the modules only when the tool has more than 16 sections (AutoSteerService.SendPgns: "below 17 sections, 239 is sufficient and 229 is skipped"), but the task controller reads the sections from PGN 229 only (AOG-TaskController app.cpp handles 0xE5, 0xF1, 0xF2 and 0xD6; not 254 or 239). So an ISOBUS implement with 16 sections or fewer never gets AgOpenWeb's section states: in automatic section control it does nothing while the screen paints coverage. AgOpenGPS sends 229 whatever the section count (AgIO relays it to the modules), and so does AgOpenWeb's own loopback copy for rate controllers (SendLoopbackPgns), which the task controller does not listen to.
- • the task controller received PGN 254 ×1447 and 239 ×1447 in this time (they carry sections 1-16 too, but the task controller does not read them)
- ✓ the sprayer morning: no exception, no hung host loop, no error logged

The task controller received: 229 ×0 (0 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×1752, 239 ×1752, 200 ×18, 201 ×0, 202 ×0; heartbeats sent 159, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.3 s  AgOpenWeb started; task controller heard after 0.0 s
  1.2 s  Tool: 8 sections × 150 cm; Save
  3.5 s  Field, AB line, job, master Auto
  3.6 s  ISOBUS button tapped; section control on after 0.00 s
 14.8 s  Spraying 10 s: 10.0 s: screen sections on 10.0 s; task controller heard 10.0 s, not heard 0.0 s; auto shown 10.0 s; on screen but implement off 1:10.0s 2:10.0s 3:10.0s 4:10.0s 5:10.0s 6:10.0s 7:10.0s 8:10.0s; screen on with no PGN 229 at the task controller 10.0 s; coverage painted 264 m² (264 m² with the implement's sections all off)
 14.9 s  Master off
 17.9 s  Driving 3 s with the master off: 3.0 s: screen sections on 0.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 0 m²
--- task controller log (its own clock)
0.0s implement Sprayer (8 sections) registering
0.0s started
0.1s 221 "Implement: Sprayer (8 sections, 12 m)"
4.0s 241 request: section control enabled
```

</details>

### C2. A 24-section planter, section control automatic

After the sprayer, the planter (24 sections, 12 m) is plugged into the bus and AgOpenWeb starts for it. The operator types the tool (24 × 50 cm), opens a field, job, master Auto, ISOBUS button for automatic section control, and plants.

- ✓ Planting 10 s: every implement section followed the screen
- ✓ Planting 10 s: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- • the task controller received PGN 229 about 99 times a second (2 setpoint changes in all), 254 ×99/s, 239 ×99/s (AgOpenGPS sends each once per fix, 10/s; the task controller parses every one)
- ✗ **Improvement** `ISOBUS-NO-GNSS-QUALITY`: in 14 s the task controller received no PGN 214 (AgIO's GPS data, source 0x7C): AgOpenWeb sends it to the loopback port 15555 only (SendGpsDataToLoopback), and the task controller listens on 8888, so it never learns the fix quality and tells every implement DDI 514 = 1 (plain GPS) whatever the receiver has, as the AOG-TaskController protocol notes for a default AgIO install; some implements gate section control on the GNSS quality
- • no PGN 242 (process data: speed DDI 156, distance 597, cross-track error) reached the task controller; AgOpenWeb does not send it, so a task controller acting as the tractor ECU has no speed to give the implements (AgIO sends none either)
- ✓ the planter set-up: no exception, no hung host loop, no error logged

The task controller received: 229 ×1415 (2 changes), 241 ×2, 242 ×0, 214 ×0, 254 ×3176, 239 ×3176, 200 ×33, 201 ×0, 202 ×0; heartbeats sent 289, messages sent 3; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.0 s  AgOpenWeb started; task controller heard after 0.0 s; 24 sections
  2.1 s  Tool: 24 sections × 50 cm; Save
  4.2 s  Field, AB line, job, master Auto
  4.3 s  ISOBUS button tapped; section control on after 0.02 s
 14.3 s  Planting 10 s: 10.0 s: screen sections on 10.0 s; task controller heard 10.0 s, not heard 0.0 s; auto shown 10.0 s; coverage painted 264 m² (4 m² with the implement's sections all off)
--- task controller log (its own clock)
0.0s implement Sprayer (8 sections) registering
0.0s started
0.1s 221 "Implement: Sprayer (8 sections, 12 m)"
4.0s 241 request: section control enabled
18.5s 221 "Implement lost: Sprayer"
18.5s implement Sprayer lost
18.5s implement Planter (24 sections) registering
18.5s 221 "Implement: Planter (24 sections, 12 m)"
23.0s 241 request: section control enabled
```

</details>

### C3. Master off and on, section control manual and back

While planting: master off at the end of a pass and on again; then the operator switches the implement to manual (ISOBUS button), drives a bit, and back to automatic.

- ✓ 3 s with the master off: every implement section followed the screen
- ✓ 3 s with the master off: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ 3 s planting again: every implement section followed the screen
- ✓ 3 s planting again: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- • in manual AgOpenWeb keeps its own sections on and paints coverage (108 m²) while the implement's own switches decide what it does (its sections 000000000000000000000000); the ISOBUS row shows the difference, as AgOpenGPS
- ✓ one PGN 241 per tap (two taps, two requests)
- ✓ 3 s planting again: every implement section followed the screen
- ✓ 3 s planting again: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the mode changes: no exception, no hung host loop, no error logged

The task controller received: 229 ×2747 (4 changes), 241 ×4, 242 ×0, 214 ×0, 254 ×4508, 239 ×4508, 200 ×46, 201 ×0, 202 ×0; heartbeats sent 410, messages sent 3; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.1 s  Master off
  3.1 s  3 s with the master off: 3.0 s: screen sections on 0.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 0 m²
  3.2 s  Master Auto
  6.2 s  3 s planting again: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 77 m² (5 m² with the implement's sections all off)
  6.2 s  ISOBUS button tapped; section control off (manual) after 0.00 s
 10.4 s  4 s in manual: 4.1 s: screen sections on 4.1 s; task controller heard 4.1 s, not heard 0.0 s; auto shown 0.0 s; on screen but implement off 1:3.8s 2:3.8s 3:3.8s 4:3.8s 5:3.8s 6:3.8s 7:3.8s 8:3.8s 9:3.8s 10:3.8s 11:3.8s 12:3.8s … (24 sections); coverage painted 108 m² (103 m² with the implement's sections all off)
 10.4 s  ISOBUS button tapped; section control on after 0.00 s
 13.4 s  3 s planting again: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 77 m² (5 m² with the implement's sections all off)
--- task controller log (its own clock)
0.0s implement Sprayer (8 sections) registering
0.0s started
0.1s 221 "Implement: Sprayer (8 sections, 12 m)"
4.0s 241 request: section control enabled
18.5s 221 "Implement lost: Sprayer"
18.5s implement Sprayer lost
18.5s implement Planter (24 sections) registering
18.5s 221 "Implement: Planter (24 sections, 12 m)"
23.0s 241 request: section control enabled
39.2s 241 request: section control disabled
43.4s 241 request: section control enabled
```

</details>

### C4. The task controller restarts

The task controller process restarts (the HAT's daemon updated, or a crash): 3 s away, then back with section control off for every implement, as after any restart. The operator does nothing.

- ✓ the ISOBUS button went away 1.0 s after the last heartbeat
- ✓ AgOpenWeb asked for automatic once more when the task controller came back in manual (the operator's last choice), and once only
- ✓ 4 s planting after the restart: every implement section followed the screen
- ✓ 4 s planting after the restart: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the restart: no exception, no hung host loop, no error logged

The task controller received: 229 ×3165 (5 changes), 241 ×5, 242 ×0, 214 ×0, 254 ×4926, 239 ×4926, 200 ×50, 201 ×0, 202 ×0; heartbeats sent 447, messages sent 4; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.0 s  Task controller stopped
  3.0 s  3 s without the task controller: 3.0 s: screen sections on 3.0 s; task controller heard 1.0 s, not heard 2.0 s; auto shown 1.0 s; screen on with no PGN 229 at the task controller 2.0 s; coverage painted 77 m² (53 m² with no task controller heard)
  3.0 s  Task controller back (section control off, as after a restart)
  3.2 s  heard again after 0.1 s; section control on again after 0.14 s
  7.2 s  4 s planting after the restart: 4.0 s: screen sections on 4.0 s; task controller heard 4.0 s, not heard 0.0 s; auto shown 4.0 s; coverage painted 104 m²
--- task controller log (its own clock)
0.0s started
0.1s 221 "Implement: Sprayer (8 sections, 12 m)"
4.0s 241 request: section control enabled
18.5s 221 "Implement lost: Sprayer"
18.5s implement Sprayer lost
18.5s implement Planter (24 sections) registering
18.5s 221 "Implement: Planter (24 sections, 12 m)"
23.0s 241 request: section control enabled
39.2s 241 request: section control disabled
43.4s 241 request: section control enabled
46.5s stopped
49.5s restarted
49.6s 221 "Implement: Planter (24 sections, 12 m)"
49.6s 241 request: section control enabled
```

</details>

### C5. The planter unplugged and plugged back

The planter's ISOBUS plug comes out for 4 s (the task controller stays up with no implement, its beacon on), then goes back in and the planter registers again.

- ✓ after 0.10 s the button shows the task controller with no implement (0 sections)
- ✓ when the planter left: the task controller's message(s) "Implement lost: Planter" reached AgOpenWeb (PGN 221 from source 0x80), and hardware messages are on in Display, so the operator sees them
- ✓ after 0.04 s the button shows 24 sections again, section control on
- ✓ 4 s planting again: every implement section followed the screen
- ✓ 4 s planting again: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ when the planter came back: the task controller's message(s) "Implement: Planter (24 sections, 12 m)" reached AgOpenWeb (PGN 221 from source 0x80), and hardware messages are on in Display, so the operator sees them
- ✓ the implement swap: no exception, no hung host loop, no error logged

The task controller received: 229 ×4027 (5 changes), 241 ×5, 242 ×0, 214 ×0, 254 ×5788, 239 ×5788, 200 ×59, 201 ×0, 202 ×0; heartbeats sent 523, messages sent 6; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.0 s  Planter unplugged
  4.1 s  4 s with no implement: 4.0 s: screen sections on 4.0 s; task controller heard 4.0 s, not heard 0.0 s; auto shown 0.0 s; coverage painted 104 m²
  4.1 s  Planter plugged back
  8.7 s  4 s planting again: 4.0 s: screen sections on 4.0 s; task controller heard 4.0 s, not heard 0.0 s; auto shown 4.0 s; coverage painted 104 m²
--- task controller log (its own clock)
18.5s implement Sprayer lost
18.5s implement Planter (24 sections) registering
18.5s 221 "Implement: Planter (24 sections, 12 m)"
23.0s 241 request: section control enabled
39.2s 241 request: section control disabled
43.4s 241 request: section control enabled
46.5s stopped
49.5s restarted
49.6s 221 "Implement: Planter (24 sections, 12 m)"
49.6s 241 request: section control enabled
53.7s 221 "Implement lost: Planter"
53.7s implement Planter lost
57.8s implement Planter (24 sections) registering
57.9s 221 "Implement: Planter (24 sections, 12 m)"
```

</details>

### C6. AgOpenWeb restarted with the task controller on

At lunch the operator closes AgOpenWeb (the tablet's battery), the task controller and the planter stay on in automatic. After the restart: a new field and on with the work. AgOpenWeb has nothing to ask: it should show what the task controller reports and not send a mode of its own.

- ✓ the restarted AgOpenWeb shows the task controller's mode (automatic) and sent no PGN 241 of its own
- ✓ 5 s planting after lunch: every implement section followed the screen
- ✓ 5 s planting after lunch: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the afternoon: no exception, no hung host loop, no error logged

The task controller received: 229 ×4944 (7 changes), 241 ×5, 242 ×0, 214 ×0, 254 ×6705, 239 ×6705, 200 ×69, 201 ×0, 202 ×0; heartbeats sent 604, messages sent 6; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.1 s  AgOpenWeb restarted; task controller heard after 0.1 s; 24 sections, section control on; tool 24 sections
  9.2 s  5 s planting after lunch: 5.0 s: screen sections on 5.0 s; task controller heard 5.0 s, not heard 0.0 s; auto shown 5.0 s; coverage painted 133 m² (5 m² with the implement's sections all off)
--- task controller log (its own clock)
18.5s implement Sprayer lost
18.5s implement Planter (24 sections) registering
18.5s 221 "Implement: Planter (24 sections, 12 m)"
23.0s 241 request: section control enabled
39.2s 241 request: section control disabled
43.4s 241 request: section control enabled
46.5s stopped
49.5s restarted
49.6s 221 "Implement: Planter (24 sections, 12 m)"
49.6s 241 request: section control enabled
53.7s 221 "Implement lost: Planter"
53.7s implement Planter lost
57.8s implement Planter (24 sections) registering
57.9s 221 "Implement: Planter (24 sections, 12 m)"
```

</details>

## Careless operator

### K1. Task controller started after AgOpenWeb, mid-job

AgOpenWeb is up with the 24-section planter's tool, the job open and master Auto; the HAT's task controller daemon only starts 4 s later (the planter already on its bus). The operator then taps the ISOBUS button.

- ✓ no ISOBUS button while no task controller is heard
- ✓ the ISOBUS button appeared within 1.5 s of the first heartbeat
- ✓ 5 s planting: every implement section followed the screen
- ✓ 5 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ when the task controller started: the task controller's message(s) "Implement: Planter (24 sections, 12 m)" reached AgOpenWeb (PGN 221 from source 0x80), and hardware messages are on in Display, so the operator sees them
- ✓ the late start: no exception, no hung host loop, no error logged

The task controller received: 229 ×512 (1 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×512, 239 ×512, 200 ×5, 201 ×0, 202 ×0; heartbeats sent 45, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  4.2 s  AgOpenWeb started; tool 24 × 50 cm; job open, master Auto; no task controller yet
  8.3 s  4 s with no task controller: 4.1 s: screen sections on 4.1 s; task controller heard 0.0 s, not heard 4.1 s; auto shown 0.0 s; on screen but implement off 1:4.1s 2:4.1s 3:4.1s 4:4.1s 5:4.1s 6:4.1s 7:4.1s 8:4.1s 9:4.1s 10:4.1s 11:4.1s 12:4.1s … (24 sections); screen on with no PGN 229 at the task controller 4.1 s; coverage painted 108 m² (108 m² with the implement's sections all off) (108 m² with no task controller heard)
  8.3 s  The task controller starts
  8.3 s  heard after 0.0 s: 24 sections, section control off
  8.5 s  ISOBUS button tapped; section control on after 0.08 s
 13.5 s  5 s planting: 5.0 s: screen sections on 5.0 s; task controller heard 5.0 s, not heard 0.0 s; auto shown 5.0 s; coverage painted 130 m² (3 m² with the implement's sections all off)
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s started
0.0s stopped
8.3s restarted
8.3s 221 "Implement: Planter (24 sections, 12 m)"
8.3s 241 request: section control enabled
```

</details>

### K2. A task controller that ignores the request, then one that takes 1.5 s

The implement refuses automatic section control (an implement that only works in manual, an older task controller): PGN 241 changes nothing. The operator taps the ISOBUS button and waits. Then the task controller is one that takes 1.5 s to apply the request.

- ✓ AgOpenWeb asked every 0.5 s for 3 s, then stopped and showed the mode the task controller reports (manual)
- ✗ **Improvement** `ISOBUS-REQUEST-REFUSED-SILENT`: the implement did not take automatic section control and AgOpenWeb said nothing: the button just stays on manual, as if the tap had not registered (AgOpenGPS does the same)
- ✓ a slow task controller still ends in automatic; AgOpenWeb repeated the request 2 time(s) meanwhile
- ✓ 4 s planting: every implement section followed the screen
- ✓ 4 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the refusals: no exception, no hung host loop, no error logged

The task controller received: 229 ×1433 (2 changes), 241 ×9, 242 ×0, 214 ×0, 254 ×1434, 239 ×1434, 200 ×15, 201 ×0, 202 ×0; heartbeats sent 119, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  4.3 s  ISOBUS button tapped (the task controller ignores PGN 241)
  8.8 s  4.5 s waiting: 4.5 s: screen sections on 4.5 s; task controller heard 4.5 s, not heard 0.0 s; auto shown 0.0 s; on screen but implement off 1:4.5s 2:4.5s 3:4.5s 4:4.5s 5:4.5s 6:4.5s 7:4.5s 8:4.5s 9:4.5s 10:4.5s 11:4.5s 12:4.5s … (24 sections); coverage painted 120 m² (120 m² with the implement's sections all off)
  8.8 s  PGN 241 sent ×6 in 4.5 s (0 after 3.6 s); the button now shows section control off
 10.4 s  ISOBUS button tapped (the task controller applies it after 1.5 s): section control on after 1.57 s, PGN 241 ×3
 14.4 s  4 s planting: 4.0 s: screen sections on 4.0 s; task controller heard 4.0 s, not heard 0.0 s; auto shown 4.0 s; coverage painted 104 m² (5 m² with the implement's sections all off)
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s started
0.1s 221 "Implement: Planter (24 sections, 12 m)"
4.2s 241 request: section control enabled (ignored)
4.8s 241 request: section control enabled (ignored)
5.4s 241 request: section control enabled (ignored)
5.9s 241 request: section control enabled (ignored)
6.4s 241 request: section control enabled (ignored)
7.0s 241 request: section control enabled (ignored)
8.8s 241 request: section control enabled (in 1500 ms)
9.4s 241 request: section control enabled (in 1500 ms)
10.0s 241 request: section control enabled (in 1500 ms)
10.3s section control enabled (late)
```

</details>

### K3. Section control switched on the tractor's terminal

Planting in automatic. The operator switches the implement to manual on the tractor's own terminal (the task controller reports it, AgOpenWeb asked nothing), drives 3 s, then back to automatic on the terminal.

- ✓ 3 s planting: every implement section followed the screen
- ✓ 3 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ AgOpenWeb showed manual 0.04 s later
- ✓ AgOpenWeb followed the terminal and sent no PGN 241 to undo it
- ✓ AgOpenWeb showed automatic 0.06 s later
- ✓ 3 s planting again: every implement section followed the screen
- ✓ 3 s planting again: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the terminal: no exception, no hung host loop, no error logged

The task controller received: 229 ×1335 (2 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×1335, 239 ×1335, 200 ×14, 201 ×0, 202 ×0; heartbeats sent 114, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  4.3 s  ISOBUS button tapped; section control on after 0.00 s
  7.4 s  3 s planting: 3.1 s: screen sections on 3.1 s; task controller heard 3.1 s, not heard 0.0 s; auto shown 3.1 s; coverage painted 82 m² (5 m² with the implement's sections all off)
  7.4 s  Manual chosen on the terminal
 10.4 s  3 s in manual: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 0.0 s; on screen but implement off 1:2.7s 2:2.7s 3:2.7s 4:2.7s 5:2.7s 6:2.7s 7:2.7s 8:2.7s 9:2.7s 10:2.7s 11:2.7s 12:2.7s … (24 sections); coverage painted 80 m² (74 m² with the implement's sections all off)
 10.4 s  Automatic chosen on the terminal
 13.5 s  3 s planting again: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 79 m² (6 m² with the implement's sections all off)
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s started
0.0s 221 "Implement: Planter (24 sections, 12 m)"
4.2s 241 request: section control enabled
```

</details>

### K4. The task controller's link drops for 5 s and for 30 s

Planting in automatic; the HAT's Wi-Fi to the tablet drops (nothing in or out for the task controller) for 5 s, then later for 30 s. The implement keeps its last state. The operator keeps driving.

- ✓ 3 s planting: every implement section followed the screen
- ✓ 3 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the ISOBUS button went away 1.0 s after the last heartbeat
- ✓ 3 s planting after the drop: every implement section followed the screen
- ✓ 3 s planting after the drop: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the ISOBUS button went away 1.0 s after the last heartbeat
- ✗ **Improvement** `ISOBUS-TC-LOST-SILENT`: with the task controller gone for 30 s AgOpenWeb kept the sections on and painted 775 m² of coverage no implement was told about; the only sign is the ISOBUS button disappearing (no alarm, no message), while a lost machine module at least changes the status dot
- ✓ 3 s planting after the drop: every implement section followed the screen
- ✓ 3 s planting after the drop: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ the drops: no exception, no hung host loop, no error logged

The task controller received: 229 ×1333 (2 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×1332, 239 ×1333, 200 ×14, 201 ×0, 202 ×0; heartbeats sent 416, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  4.3 s  ISOBUS button tapped; section control on after 0.00 s
  7.3 s  3 s planting: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 79 m² (5 m² with the implement's sections all off)
  7.3 s  Link down for 5 s
 12.3 s  5 s with the link down: 5.0 s: screen sections on 5.0 s; task controller heard 1.0 s, not heard 4.0 s; auto shown 1.0 s; screen on with no PGN 229 at the task controller 4.0 s; coverage painted 130 m² (106 m² with no task controller heard)
 12.3 s  Link back
 12.4 s  heard again after 0.1 s; PGN 241 sent since the drop ×0
 15.4 s  3 s planting after the drop: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 77 m²
 15.4 s  Link down for 30 s
 45.4 s  30 s with the link down: 30.0 s: screen sections on 30.0 s; task controller heard 1.0 s, not heard 29.0 s; auto shown 1.0 s; screen on with no PGN 229 at the task controller 29.0 s; coverage painted 799 m² (775 m² with no task controller heard)
 45.4 s  Link back
 45.4 s  heard again after 0.0 s; PGN 241 sent since the drop ×0
 48.4 s  3 s planting after the drop: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 79 m²
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s started
0.1s 221 "Implement: Planter (24 sections, 12 m)"
4.2s 241 request: section control enabled
```

</details>

### K5. Malformed and odd frames from the task controller

A task controller with its beacon off and no implement sends, one at a time: a heartbeat whose length does not match its section count, one for 70 sections, one with a wrong checksum, one from another source address, one with an extra byte, one cut short, one with no payload. AgOpenWeb must ignore what is wrong, as AgOpenGPS does, and must not fall over.

- ✓ a heartbeat whose length does not match its section count is ignored (as AgOpenGPS: length must be 2 + ceil(n / 8))
- • a heartbeat for 70 sections is taken: the status says 70, the reported states stop at 64 (the protocol's maximum), the browser builds a 64-cell row and the tooltip says 70
- • a heartbeat with a wrong checksum is taken (as AgOpenGPS, which does not check it; the task controller does not check AgOpenWeb's either)
- ✓ PGN 240 from another source than the task controller (0x80) is ignored
- • a beacon with an extra byte is taken as alive with no implement
- ✓ a frame cut short is ignored
- ✓ a heartbeat with no payload is ignored
- ✓ a good heartbeat after the bad ones is taken: 8 sections, 1-4 on, frame 8 sections
- ✓ the bad frames: no exception, no hung host loop, no error logged

The task controller received: 229 ×0 (0 changes), 241 ×0, 242 ×0, 214 ×0, 254 ×604, 239 ×604, 200 ×7, 201 ×0, 202 ×0; heartbeats sent 0, messages sent 0; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  1.8 s  length 3 for 12 sections (needs 4): alive False, 0 sections, reported none, frame none
  2.1 s  70 sections: alive True, 70 sections, reported 1111111111111111111111111111111111111111111111111111111111111111, frame 70 sections
  3.1 s  wrong checksum: alive True, 8 sections, reported 01010101, frame 8 sections
  4.1 s  PGN 240 from source 0x7B (a machine module): alive False, 0 sections, reported none, frame none
  4.4 s  no implement, one extra byte (length 3): alive True, 0 sections, reported none, frame 0 sections
  5.5 s  cut short (length says 5, 3 bytes there): alive False, 0 sections, reported none, frame none
  5.8 s  no payload (length 0): alive False, 0 sections, reported none, frame none
  6.1 s  a good one (8 sections, 1-4 on): alive True, 8 sections, reported 11110000, frame 8 sections
--- task controller log (its own clock)
0.0s started
```

</details>

### K6. 64 sections

A 24 m planter with 64 sections (the protocol's maximum): tool 64 × 37.5 cm, automatic section control. Section 64 (bit 63 of PGN 229 and of PGN 240) must get through both ways.

- ✓ 6 s planting: every implement section followed the screen
- ✓ 6 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ section 64 flows: screen → PGN 229 → implement → PGN 240 → status → frame
- ✓ 64 sections: no exception, no hung host loop, no error logged

The task controller received: 229 ×1339 (2 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×1339, 239 ×1339, 200 ×14, 201 ×0, 202 ×0; heartbeats sent 113, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  7.5 s  ISOBUS button tapped; section control on after 0.00 s
 13.5 s  6 s planting: 6.0 s: screen sections on 6.0 s; task controller heard 6.0 s, not heard 0.0 s; auto shown 6.0 s; coverage painted 317 m² (10 m² with the implement's sections all off)
 13.5 s  section 64: screen on, implement on, reported on, frame on; status 64 sections
--- task controller log (its own clock)
0.0s implement Planter 64 (64 sections) registering
0.0s started
0.1s 221 "Implement: Planter 64 (64 sections, 24 m)"
7.4s 241 request: section control enabled
```

</details>

### K7. Tool typed with 20 sections, implement with 12

The operator's tool profile says 20 sections (the old planter); the implement on the bus has 12. Automatic section control. The task controller forwards the first 12 of AgOpenWeb's 20 to the implement; the screen paints 20.

- ✓ 6 s planting: every implement section followed the screen (the first 12: tool 20 sections, implement 12)
- ✓ 6 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✗ **Improvement** `ISOBUS-SECTION-COUNT-MISMATCH-SILENT`: the tool has 20 sections and the implement reports 12; AgOpenWeb paints 20 and says nothing: sections 13-20 show as worked on the map but no implement has them (the heartbeat carries the implement's count, so a one-line warning is possible)
- ✓ the mismatch: no exception, no hung host loop, no error logged

The task controller received: 229 ×986 (2 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×987, 239 ×987, 200 ×10, 201 ×0, 202 ×0; heartbeats sent 84, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  3.9 s  ISOBUS button tapped; section control on after 0.00 s
  9.9 s  6 s planting: 6.0 s: screen sections on 6.0 s; task controller heard 6.0 s, not heard 0.0 s; auto shown 6.0 s; coverage painted 132 m² (6 m² with the implement's sections all off)
  9.9 s  status: implement 12 sections, tool 20; the ISOBUS row has 12 cells under 20 buttons
--- task controller log (its own clock)
0.0s implement Planter 12 (12 sections) registering
0.0s started
0.1s 221 "Implement: Planter 12 (12 sections, 6 m)"
3.9s 241 request: section control enabled
```

</details>

### K8. Two implements with sections on the bus

A 24-section planter and a 12-section fertiliser hopper, both on the task controller (it sends one PGN 240 per implement every 100 ms). The tool is the planter's 24 sections, automatic section control.

- ✓ the status stayed on one implement
- ✓ 5 s planting: every implement section followed the screen
- ✓ 5 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ two implements: no exception, no hung host loop, no error logged

The task controller received: 229 ×1219 (2 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×1219, 239 ×1219, 200 ×13, 201 ×0, 202 ×0; heartbeats sent 214, messages sent 2; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  4.3 s  ISOBUS button tapped; section control on after 0.00 s
  7.3 s  in 3 s the status's section count took the values 12 and changed 0 times
 12.3 s  5 s planting: 5.0 s: screen sections on 5.0 s; task controller heard 5.0 s, not heard 0.0 s; auto shown 5.0 s; coverage painted 133 m²
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s implement Fertiliser (12 sections) registering
0.0s started
0.1s 221 "Implement: Planter (24 sections, 12 m)"
0.1s 221 "Implement: Fertiliser (12 sections, 12 m)"
4.2s 241 request: section control enabled
```

</details>

### K9. Task controller with its beacon off and no implement yet

The task controller runs with aogHeartbeatEnabled false (the setting for AgOpenGPS before 6.8.2 beta 5) and no implement is plugged in yet. 3 s later the planter is plugged in.

- • no ISOBUS button for 3 s: with the beacon off and no implement the task controller sends nothing, so the operator cannot tell it is running (the protocol has no other sign; AgOpenGPS is the same)
- ✓ the button appeared 0.08 s after the implement registered
- ✓ the beacon: no exception, no hung host loop, no error logged

The task controller received: 229 ×0 (0 changes), 241 ×0, 242 ×0, 214 ×0, 254 ×311, 239 ×311, 200 ×4, 201 ×0, 202 ×0; heartbeats sent 1, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  3.0 s  Planter plugged in
--- task controller log (its own clock)
0.0s started
3.0s implement Planter (24 sections) registering
3.0s 221 "Implement: Planter (24 sections, 12 m)"
```

</details>

### K10. Field closed with the implement in automatic

Planting in automatic, sections on; the operator closes the field (end of the day) without touching the master or the ISOBUS button. The implement must stop.

- ✓ 3 s planting: every implement section followed the screen
- ✓ 3 s planting: the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s
- ✓ after the field closed the task controller got all sections off and the implement stopped
- ✓ the close: no exception, no hung host loop, no error logged

The task controller received: 229 ×1182 (3 changes), 241 ×1, 242 ×0, 214 ×0, 254 ×1182, 239 ×1182, 200 ×12, 201 ×0, 202 ×0; heartbeats sent 104, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  4.3 s  ISOBUS button tapped; section control on after 0.00 s
  7.3 s  3 s planting: 3.0 s: screen sections on 3.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 79 m² (5 m² with the implement's sections all off)
  7.4 s  Field closed
  8.9 s  1.5 s later: screen 000000000000000000000000, setpoints at the task controller 000000000000000000000000 (9 ms old), implement 000000000000000000000000; field open False
 11.9 s  3 s driving with no field: 3.0 s: screen sections on 0.0 s; task controller heard 3.0 s, not heard 0.0 s; auto shown 3.0 s; coverage painted 0 m²
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s started
0.1s 221 "Implement: Planter (24 sections, 12 m)"
4.2s 241 request: section control enabled
```

</details>

### K11. Module subnet changed from the network page

The operator sets the module subnet to 192.168.5 on the network page. AgOpenWeb sends PGN 201 to every module; the task controller rebinds to that subnet (on the loopback here, it just notes it).

- ✓ the task controller received PGN 201 in the layout it reads (0xC9 0xC9 192 168 5)
- ✓ the subnet change: no exception, no hung host loop, no error logged

The task controller received: 229 ×0 (0 changes), 241 ×0, 242 ×0, 214 ×0, 254 ×112, 239 ×111, 200 ×2, 201 ×1, 202 ×0; heartbeats sent 10, messages sent 1; checksum errors 0, short packets 0, other sources dropped 0

<details><summary>Timeline</summary>

```
  0.1 s  Subnet 192.168.5 set
--- task controller log (its own clock)
0.0s implement Planter (24 sections) registering
0.0s started
0.1s 221 "Implement: Planter (24 sections, 12 m)"
0.1s 201 subnet 192.168.5
```

</details>
