// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Sprayer;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Sprayer;

/// <summary>The sprayer readout: PGN 160 from the machine module, checked against the
/// nozzles of the open sections.</summary>
[TestFixture]
[NonParallelizable] // sets the global clock
public class SprayerServiceTests
{
    private IUdpCommunicationService _udp = null!;
    private IAudioService _audio = null!;
    private ConfigurationStore _config = null!;
    private SprayerService _service = null!;
    private TestClock _clock = null!;

    // An 8 m boom: sections of 2, 1, 2, 1 and 2 m, nozzles every 0.5 m = 4, 2, 4, 2, 4 nozzles.
    private static readonly double[] WidthsCm = { 200, 100, 200, 100, 200 };

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock(new DateTime(2026, 10, 21, 9, 0, 0));
        Clock.Set(_clock);
        _udp = Substitute.For<IUdpCommunicationService>();
        _audio = Substitute.For<IAudioService>();
        _config = new ConfigurationStore();
        for (int i = 0; i < WidthsCm.Length; i++) _config.Tool.SetSectionWidth(i, WidthsCm[i]);
        _config.Machine.SprayerNozzleSpacing = 0.5;
        _config.Machine.SprayerNozzleFlow = 1.6;
        _config.Machine.SprayerReferencePressure = 3.0;
        _config.Machine.SprayerMinPressure = 1.0;
        _config.Machine.SprayerMaxPressure = 6.0;
        _config.Machine.SprayerFlowTolerance = 20;
        _service = new SprayerService(_udp, _config, _audio);
    }

    [TearDown]
    public void TearDown() => Clock.Reset();

    // PGN 160 as Pulverizador_Nano builds it (CRC = sum of source..last payload byte).
    private static byte[] Frame(double bar, double lpm, double litres = 0, bool master = true, int sections = 0)
    {
        ushort p = (ushort)Math.Round(bar * 100), c = (ushort)Math.Round(lpm * 100);
        uint t = (uint)Math.Round(litres * 10);
        var f = new byte[]
        {
            0x80, 0x81, 123, 0xA0, 10,
            (byte)p, (byte)(p >> 8), (byte)c, (byte)(c >> 8),
            (byte)t, (byte)(t >> 8), (byte)(t >> 16), (byte)(t >> 24),
            (byte)(master ? 1 : 0), (byte)sections, 0,
        };
        int sum = 0;
        for (int i = 2; i < f.Length - 1; i++) sum += f[i];
        f[^1] = (byte)sum;
        return f;
    }

    private void Receive(byte[] frame) =>
        _udp.DataReceived += Raise.EventWith(new UdpDataReceivedEventArgs { Data = frame, PGN = frame[3] });

    // One message a second, as the module sends them.
    private void Seconds(int n, double bar, double lpm, int sections)
    {
        for (int i = 0; i < n; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Receive(Frame(bar, lpm, sections: sections));
        }
    }

    [Test]
    public void BeforeAnyMessage_NoStatus()
    {
        Assert.That(_service.Status, Is.Null);
    }

    [Test]
    public void Message_IsDecoded()
    {
        Receive(Frame(3.25, 14.5, litres: 123.4, master: true, sections: 0b10101));

        var s = _service.Status!.Value;
        Assert.That(s.PressureBar, Is.EqualTo(3.25).Within(1e-9));
        Assert.That(s.FlowLpm, Is.EqualTo(14.5).Within(1e-9));
        Assert.That(s.TotalLitres, Is.EqualTo(123.4).Within(1e-9));
        Assert.That(s.MasterOn, Is.True);
        Assert.That(s.Sections, Is.EqualTo(0b10101));
    }

    [Test]
    public void OpenSections_GiveTheirNozzlesAndWidth()
    {
        Receive(Frame(3.0, 0, sections: 0b11111));
        Assert.That(_service.Status!.Value.OpenNozzles, Is.EqualTo(16));
        Assert.That(_service.Status!.Value.OpenWidthM, Is.EqualTo(8.0).Within(1e-9));

        Receive(Frame(3.0, 0, sections: 0b01010)); // sections 2 and 4: 2 + 2 nozzles
        Assert.That(_service.Status!.Value.OpenNozzles, Is.EqualTo(4));
        Assert.That(_service.Status!.Value.OpenWidthM, Is.EqualTo(2.0).Within(1e-9));
    }

    [Test]
    public void ExpectedFlow_FollowsTheOpenNozzlesAndThePressure()
    {
        Receive(Frame(3.0, 0, sections: 0b00001)); // 4 nozzles at the chart pressure
        Assert.That(_service.Status!.Value.ExpectedFlowLpm, Is.EqualTo(6.4).Within(1e-9));

        Receive(Frame(12.0, 0, sections: 0b11111)); // 16 nozzles, 4× the pressure = 2× the flow
        Assert.That(_service.Status!.Value.ExpectedFlowLpm, Is.EqualTo(16 * 1.6 * 2).Within(1e-9));
    }

    [Test]
    public void FlowAsExpected_NoAlarm()
    {
        Seconds(10, 3.0, 25.6, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        _audio.DidNotReceive().Play(Arg.Any<SoundEffect>());
    }

    [Test]
    public void FlowLow_AlarmsAfterThreeMessages_AndSounds()
    {
        Seconds(5, 3.0, 25.6, 0b11111);                 // settled, all fine
        Seconds(2, 3.0, 12.0, 0b11111);                 // a blocked filter: half the flow
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None), "not yet: 2 messages");
        Seconds(1, 3.0, 12.0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowLow));
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
    }

    [Test]
    public void FlowHigh_Alarms()
    {
        Seconds(6, 3.0, 40.0, 0b11111); // a nozzle gone, or a burst hose
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowHigh));
    }

    [Test]
    public void SectionChange_FlowCheckWaitsForTheValves()
    {
        Seconds(5, 3.0, 25.6, 0b11111);
        // Sections 1 and 2 close: 10 nozzles left (16 L/min), but the flow meter still says
        // 25.6 for a moment. No alarm while it catches up.
        Seconds(3, 3.0, 25.6, 0b11100);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Seconds(3, 3.0, 16.0, 0b11100);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
    }

    [Test]
    public void AlarmLimit_FollowsTheOpenSections()
    {
        // 16 L/min is right for 10 nozzles but far too little for 16.
        Seconds(6, 3.0, 16.0, 0b11100);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Seconds(6, 3.0, 16.0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowLow));
    }

    [Test]
    public void PressureLow_OnlyWhileSpraying()
    {
        Seconds(5, 0.2, 0, 0);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None), "pump off, sections off");
        Seconds(5, 0.2, 0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms & SprayerAlarms.PressureLow, Is.EqualTo(SprayerAlarms.PressureLow));
    }

    [Test]
    public void PressureHigh_Alarms()
    {
        Seconds(3, 7.0, 0, 0);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.PressureHigh));
    }

    [Test]
    public void FlowWithSectionsOff_Alarms()
    {
        Seconds(6, 3.0, 6.4, 0); // a section switched to manual, or a valve stuck open
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowSectionsOff));
    }

    [Test]
    public void NozzleFlowZero_NoFlowCheck()
    {
        _config.Machine.SprayerNozzleFlow = 0;
        Seconds(6, 3.0, 2.0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Assert.That(double.IsNaN(_service.Status!.Value.ExpectedFlowLpm), Is.True);
    }

    [Test]
    public void AlarmSoundsAgain_EveryTenSeconds()
    {
        Seconds(3, 7.0, 0, 0);
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
        Seconds(9, 7.0, 0, 0);
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
        Seconds(1, 7.0, 0, 0);
        _audio.Received(2).Play(SoundEffect.SprayerAlarm);
    }

    [Test]
    public void Silence_ReportsNoData()
    {
        Seconds(1, 3.0, 0, 0);
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.NoData));
    }

    [Test]
    public void OtherSourceOrShortFrame_Ignored()
    {
        var f = Frame(3.0, 10);
        f[2] = 126;
        Receive(f);
        Receive(Frame(3.0, 10)[..10]);
        Assert.That(_service.Status, Is.Null);
    }

    // ---- PGN 161: a module that checks the sprayer itself (Sprayer_ESP32 0.10)

    // One process data value as the module builds it (zPgn.ino pdValue): command 3 + element, DDI, int32.
    private static IEnumerable<byte> Value(int element, int ddi, int value) => new[]
    {
        (byte)(3 | (element & 0x0F) << 4), (byte)(element >> 4), (byte)ddi, (byte)(ddi >> 8),
        (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
    };

    // PGN 161: the readings, the rate (L/ha), target, pressure for the target, limits and alarms;
    // a null leaves that value out, as the module does when it doesn't know it.
    private static byte[] ProcessFrame(double bar, double lpm, int sections, double? rate = null, double? target = null,
        double? need = null, int? alarms = 0, double minBar = 1.0, double maxBar = 6.0, int boom = 8)
    {
        var body = new List<byte>();
        body.AddRange(Value(1, 194, (int)Math.Round(bar * 1_000_000)));
        body.AddRange(Value(1, 37, (int)Math.Round(lpm * 1_000_000 / 60)));
        body.AddRange(Value(0, 351, 12_345));
        body.AddRange(Value(1, 141, 1));
        uint condensed = 0;
        for (int i = 0; i < 16; i++) condensed |= (uint)(i >= boom ? 3 : (sections >> i & 1) == 1 ? 1 : 0) << (2 * i);
        body.AddRange(Value(1, 161, (int)condensed));
        if (rate is { } r) body.AddRange(Value(1, 2, (int)Math.Round(r * 10_000)));
        if (target is { } t) body.AddRange(Value(1, 1, (int)Math.Round(t * 10_000)));
        if (need is { } n) body.AddRange(Value(1, 193, (int)Math.Round(n * 1_000_000)));
        if (minBar > 0) body.AddRange(Value(1, 195, (int)Math.Round(minBar * 1_000_000)));
        if (maxBar > 0) body.AddRange(Value(1, 196, (int)Math.Round(maxBar * 1_000_000)));
        if (alarms is { } a) body.AddRange(Value(1, 57344, a));
        var f = new List<byte> { 0x80, 0x81, 123, 0xA1, (byte)body.Count };
        f.AddRange(body);
        int sum = 0;
        for (int i = 2; i < f.Count; i++) sum += f[i];
        f.Add((byte)sum);
        return f.ToArray();
    }

    // One second as Sprayer_ESP32 0.10 sends it: PGN 160, then PGN 161.
    private void ModuleSeconds(int n, double bar, double lpm, int sections, double? rate = null, double? target = null,
        double? need = null, int? alarms = 0)
    {
        for (int i = 0; i < n; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Receive(Frame(bar, lpm, sections: sections));
            Receive(ProcessFrame(bar, lpm, sections, rate, target, need, alarms));
        }
    }

    [Test]
    public void ProcessData_IsDecoded()
    {
        var f = ProcessFrame(2.75, 12.5, 0b1010_0101, rate: 152.3, target: 150, need: 2.6, alarms: 4 | 16);
        Assert.That(SprayerService.TryParseProcessData(f, out var pd), Is.True);
        Assert.That(pd.PressureBar, Is.EqualTo(2.75).Within(1e-6));
        Assert.That(pd.FlowLpm, Is.EqualTo(12.5).Within(1e-4));
        Assert.That(pd.TotalLitres, Is.EqualTo(12.345).Within(1e-9));
        Assert.That(pd.MasterOn, Is.True);
        Assert.That(pd.Sections, Is.EqualTo(0b1010_0101));
        Assert.That(pd.RateLha, Is.EqualTo(152.3).Within(1e-9));
        Assert.That(pd.TargetLha, Is.EqualTo(150).Within(1e-9));
        Assert.That(pd.PressureNeededBar, Is.EqualTo(2.6).Within(1e-9));
        Assert.That(pd.MinPressureBar, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(pd.MaxPressureBar, Is.EqualTo(6.0).Within(1e-9));
        Assert.That(pd.Alarms, Is.EqualTo(SprayerAlarms.FlowLow | SprayerAlarms.FlowSectionsOff));
    }

    [Test]
    public void ProcessData_ValuesLeftOut_AreNaN()
    {
        Assert.That(SprayerService.TryParseProcessData(ProcessFrame(3, 0, 0, alarms: null, minBar: 0, maxBar: 0), out var pd), Is.True);
        Assert.That(double.IsNaN(pd.RateLha) && double.IsNaN(pd.TargetLha) && double.IsNaN(pd.PressureNeededBar), Is.True);
        Assert.That(double.IsNaN(pd.MinPressureBar) && double.IsNaN(pd.MaxPressureBar), Is.True);
        Assert.That(pd.Alarms, Is.Null);
    }

    [Test]
    public void ProcessData_BadFrame_Rejected()
    {
        var f = ProcessFrame(3, 0, 0);
        f[4] = 12; // not a whole number of 8-byte values
        Assert.That(SprayerService.TryParseProcessData(f, out _), Is.False);
        Assert.That(SprayerService.TryParseProcessData(ProcessFrame(3, 0, 0)[..20], out _), Is.False);
    }

    [Test]
    public void ModuleChecks_ItsRateTargetAndAlarmsAreShown()
    {
        ModuleSeconds(1, 2.8, 12.8, 0b11111, rate: 121, target: 120, need: 2.7);

        var s = _service.Status!.Value;
        Assert.That(s.ModuleChecks, Is.True);
        Assert.That(s.RateLha, Is.EqualTo(121).Within(1e-9));
        Assert.That(s.TargetLha, Is.EqualTo(120).Within(1e-9));
        Assert.That(s.PressureNeededBar, Is.EqualTo(2.7).Within(1e-9));
        Assert.That(s.MaxPressureBar, Is.EqualTo(6.0).Within(1e-9));
        Assert.That(s.OpenWidthM, Is.EqualTo(8.0).Within(1e-9));   // still from the Tool tab
        Assert.That(s.OpenNozzles, Is.Zero);                       // the Machine tab's nozzles are not used
        Assert.That(double.IsNaN(s.ExpectedFlowLpm), Is.True);
    }

    [Test]
    public void ModuleChecks_TheAppDoesNotCheckWithItsOwnSettings()
    {
        // 0.5 bar is below the Machine tab's 1 bar minimum and 5 L/min far below its 25.6 L/min
        // for 16 nozzles at 3 bar, but the module says nothing is wrong.
        ModuleSeconds(6, 0.5, 5, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        _audio.DidNotReceive().Play(Arg.Any<SoundEffect>());
    }

    [Test]
    public void ModuleAlarm_IsShownAndSounds_AgainEveryTenSeconds()
    {
        ModuleSeconds(1, 0.5, 5, 0b11111, alarms: 1);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.PressureLow));
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
        ModuleSeconds(9, 0.5, 5, 0b11111, alarms: 1);
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
        ModuleSeconds(1, 0.5, 5, 0b11111, alarms: 1);
        _audio.Received(2).Play(SoundEffect.SprayerAlarm);
        ModuleSeconds(1, 0.5, 5, 0b11111, alarms: 1 | 4);  // a new one sounds at once
        _audio.Received(3).Play(SoundEffect.SprayerAlarm);
    }

    [Test]
    public void ProcessDataWithoutAlarms_TheAppChecks()
    {
        ModuleSeconds(3, 7.0, 0, 0, alarms: null);
        var s = _service.Status!.Value;
        Assert.That(s.ModuleChecks, Is.False);
        Assert.That(s.Alarms, Is.EqualTo(SprayerAlarms.PressureHigh));
    }

    [Test]
    public void ModuleStopsSendingProcessData_TheAppChecksAgain()
    {
        ModuleSeconds(2, 7.0, 0, 0);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Seconds(3, 7.0, 0, 0);    // an older program on the module: PGN 160 only
        Assert.That(_service.Status!.Value.ModuleChecks, Is.False);
        Seconds(3, 7.0, 0, 0);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.PressureHigh));
    }

    [Test]
    public void ModuleSilent_ReportsNoData()
    {
        ModuleSeconds(1, 3.0, 0, 0, alarms: 2);
        _clock.Advance(TimeSpan.FromSeconds(4));
        var s = _service.Status!.Value;
        Assert.That(s.ModuleChecks, Is.False);
        Assert.That(s.Alarms & SprayerAlarms.NoData, Is.EqualTo(SprayerAlarms.NoData));
    }
}
