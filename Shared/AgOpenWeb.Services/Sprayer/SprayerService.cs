// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Sprayer;

/// <summary>
/// Sprayer readout from the machine module (renatorgr Sprayer_ESP32, before it Pulverizador_Nano).
///
/// <list type="bullet">
/// <item><b>PGN 160 (0xA0)</b> from the machine module (source 123) once a second:
/// { 0x80, 0x81, 123, 0xA0, 10, pressure cbar u16, flow cL/min u16, total dL u32,
/// master u8, sections u8, CRC }.</item>
/// <item><b>PGN 161 (0xA1)</b> right after it (Sprayer_ESP32 0.10 and newer): ISOBUS Process
/// Data Messages, 8 bytes each { command (low 4 bits, 3 = value) + element bits 0-3, element
/// bits 4-11, DDI u16, value i32 }, see <see cref="TryParseProcessData"/>.</item>
/// </list>
///
/// The module checks the sprayer with its own settings (target rate, nozzles, pressure limits,
/// flow tolerance, set on its page): the app shows the module's rate, target, pressure for the
/// target and alarms, and sounds the alarms. The app has no sprayer settings of its own (as the
/// official AgOpenWeb's rate controllers, Docs/PGN.md "Rate controllers"); it only adds the
/// width of the open sections (Tool tab) and <see cref="SprayerAlarms.NoData"/> when the module
/// stops sending. A module that sends only PGN 160 (an older program) gets the readings shown,
/// without a rate or alarms.
/// </summary>
public sealed class SprayerService : ISprayerService
{
    /// <summary>The source address the machine module sends from.</summary>
    public const byte MachineSource = 123;

    /// <summary>No message for this long = <see cref="SprayerAlarms.NoData"/>.</summary>
    public static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The alarm sounds again this often while one is on.</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>The DDIs read from PGN 161 (ISO 11783-11 data dictionary; 57344 is in the
    /// proprietary range).</summary>
    public const ushort DdiSetpointRate = 1, DdiActualRate = 2, DdiActualFlow = 37,
        DdiWorkState = 141, DdiCondensedWorkState = 161, DdiSetpointPressure = 193,
        DdiActualPressure = 194, DdiMinPressure = 195, DdiMaxPressure = 196,
        DdiTotalVolumeMl = 351, DdiSprayerAlarms = 57344;

    /// <summary>The alarm bits a module may report (NoData is the app's own).</summary>
    private const SprayerAlarms ModuleAlarmBits = SprayerAlarms.PressureLow | SprayerAlarms.PressureHigh
        | SprayerAlarms.FlowLow | SprayerAlarms.FlowHigh | SprayerAlarms.FlowSectionsOff;

    private readonly ConfigurationStore _config;
    private readonly IAudioService _audio;
    private readonly object _lock = new();

    private DateTime _lastMessage;     // default = never
    private SprayerStatus _status;
    private DateTime _lastSound;
    private DateTime _lastProcessData;  // default = never; only messages with the module's alarms count
    private SprayerProcessData _module;

    public SprayerService(IUdpCommunicationService udp, ConfigurationStore config, IAudioService audio)
    {
        _config = config;
        _audio = audio;
        udp.DataReceived += OnUdpDataReceived;
    }

    public SprayerStatus? Status
    {
        get
        {
            lock (_lock)
            {
                if (_lastMessage == default) return null;
                DateTime now = Clock.Current.Now;
                var s = _status;
                if (ModuleChecks(now))
                {
                    s = s with
                    {
                        ModuleChecks = true,
                        RateLha = _module.RateLha,
                        TargetLha = _module.TargetLha,
                        PressureNeededBar = _module.PressureNeededBar,
                        MinPressureBar = _module.MinPressureBar,
                        MaxPressureBar = _module.MaxPressureBar,
                        Alarms = _module.Alarms ?? SprayerAlarms.None,
                    };
                }
                return now - _lastMessage < NoDataTimeout
                    ? s
                    : s with { Alarms = s.Alarms | SprayerAlarms.NoData };
            }
        }
    }

    /// <summary>A PGN 161 with the module's alarms came in the last <see cref="NoDataTimeout"/>.</summary>
    private bool ModuleChecks(DateTime now) =>
        _lastProcessData != default && now - _lastProcessData < NoDataTimeout;

    private void OnUdpDataReceived(object? sender, UdpDataReceivedEventArgs e)
    {
        if (e.PGN == PgnNumbers.SPRAYER_PROCESS_DATA)
        {
            OnProcessData(e.Data);
            return;
        }
        if (e.PGN != PgnNumbers.SPRAYER_DATA) return;
        if (!TryParse(e.Data, out double pressure, out double flow, out double total, out bool master, out int sections))
            return;

        lock (_lock)
        {
            _lastMessage = Clock.Current.Now;
            // The width of the open sections, from the Tool tab (the module's L/ha uses the same
            // widths, sent to it in PGN 235).
            double width = 0;
            for (int i = 0; i < 8; i++)
                if ((sections & (1 << i)) != 0) width += _config.Tool.GetSectionWidth(i) / 100.0;
            _status = new SprayerStatus(pressure, flow, total, master, sections, width, SprayerAlarms.None);
        }
    }

    private void OnProcessData(byte[] data)
    {
        if (!TryParseProcessData(data, out var pd) || pd.Alarms is not { } alarms) return;
        lock (_lock)
        {
            DateTime now = Clock.Current.Now;
            var before = ModuleChecks(now) ? _module.Alarms ?? SprayerAlarms.None : SprayerAlarms.None;
            _lastProcessData = now;
            _module = pd;

            // Sound when one of the module's alarms comes on, and again every RepeatInterval.
            if (alarms != SprayerAlarms.None
                && ((alarms & ~before) != SprayerAlarms.None || now - _lastSound >= RepeatInterval))
            {
                _lastSound = now;
                _audio.Play(SoundEffect.SprayerAlarm);
            }
        }
    }

    /// <summary>
    /// PGN 161 as the module sends it: ISOBUS Process Data Messages, 8 bytes each, little-endian:
    /// byte 0 = command (bits 0-3; only 3, "value", is read) + element bits 0-3 (bits 4-7),
    /// byte 1 = element bits 4-11, bytes 2-3 = DDI, bytes 4-7 = value (int32). Only elements 0
    /// (the sprayer) and 1 (its boom) are read. The CRC is not checked (as for the other module PGNs).
    /// </summary>
    public static bool TryParseProcessData(byte[] data, out SprayerProcessData values)
    {
        values = default;
        if (data.Length < 6 || data[0] != PgnMessage.HEADER1 || data[1] != PgnMessage.HEADER2) return false;
        if (data[2] != MachineSource || data[3] != PgnNumbers.SPRAYER_PROCESS_DATA) return false;
        int length = data[4];
        if (length % 8 != 0 || data.Length < 5 + length) return false;

        double pressure = double.NaN, flow = double.NaN, total = double.NaN, rate = double.NaN, target = double.NaN;
        double need = double.NaN, min = double.NaN, max = double.NaN;
        bool? master = null;
        int? sections = null;
        SprayerAlarms? alarms = null;
        for (int o = 5; o + 8 <= 5 + length; o += 8)
        {
            int command = data[o] & 0x0F, element = (data[o] >> 4) | (data[o + 1] << 4);
            if (command != 3 || element > 1) continue;
            int ddi = data[o + 2] | data[o + 3] << 8;
            int v = data[o + 4] | data[o + 5] << 8 | data[o + 6] << 16 | data[o + 7] << 24;
            switch (ddi)
            {
                case DdiActualPressure: pressure = v / 1_000_000.0; break;     // 0.1 Pa
                case DdiActualFlow: flow = v * 60.0 / 1_000_000.0; break;      // mm³/s
                case DdiTotalVolumeMl: total = v / 1000.0; break;               // ml
                case DdiWorkState: master = v == 1; break;
                case DdiCondensedWorkState:
                {
                    int bits = 0;
                    for (int i = 0; i < 16; i++)
                        if (((uint)v >> (2 * i) & 3) == 1) bits |= 1 << i;   // 01 = on; 11 = not there
                    sections = bits;
                    break;
                }
                case DdiActualRate: rate = v / 10_000.0; break;                // 0.01 mm³/m²
                case DdiSetpointRate: target = v / 10_000.0; break;
                case DdiSetpointPressure: need = v / 1_000_000.0; break;
                case DdiMinPressure: min = v / 1_000_000.0; break;
                case DdiMaxPressure: max = v / 1_000_000.0; break;
                case DdiSprayerAlarms: alarms = (SprayerAlarms)v & ModuleAlarmBits; break;
            }
        }
        values = new SprayerProcessData(pressure, flow, total, master, sections, rate, target, need, min, max, alarms);
        return true;
    }

    /// <summary>
    /// PGN 160 as the module sends it. The CRC is not checked (as for the other module PGNs).
    /// </summary>
    public static bool TryParse(byte[] data, out double pressureBar, out double flowLpm,
        out double totalLitres, out bool masterOn, out int sections)
    {
        pressureBar = flowLpm = totalLitres = 0;
        masterOn = false;
        sections = 0;

        if (data.Length < 16 || data[0] != PgnMessage.HEADER1 || data[1] != PgnMessage.HEADER2) return false;
        if (data[2] != MachineSource || data[3] != PgnNumbers.SPRAYER_DATA || data[4] < 10) return false;

        pressureBar = (data[5] | data[6] << 8) / 100.0;
        flowLpm = (data[7] | data[8] << 8) / 100.0;
        totalLitres = (uint)(data[9] | data[10] << 8 | data[11] << 16 | data[12] << 24) / 10.0;
        masterOn = data[13] != 0;
        sections = data[14];
        return true;
    }
}
