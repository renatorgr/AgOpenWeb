// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Buffers.Text;

namespace AgOpenWeb.Models.GPS;

// A GPS on a trailed implement (fork only): a second AiO board on the implement, set to its "Implement" role, sends its
// positions to UDP 9990 and nothing to the program's 9999 (renatorgr/Boards, Firmware/v4.5_AiO_WEB README "Implement
// mode"). The app draws where the implement really is beside the model it steers by, and writes both to a file, so the
// model can be checked against the ground: it steers nothing.

/// <summary>One position from the implement's GPS: a <c>$PANDA</c> (an F9P / X20P, the IMU's heading and roll in
/// tenths of a degree) or a <c>$PAOGI</c> (a UM982 or a dual F9P: the antennas' heading and the roll in degrees).</summary>
/// <param name="TimeCentiseconds">The receiver's time of day (UTC), in hundredths of a second; -1 none.</param>
/// <param name="Heading">Degrees; NaN with none (an empty field, a $PANDA with no IMU).</param>
/// <param name="HasDualHeading">The heading is the antennas' ($PAOGI), not an IMU's.</param>
/// <param name="Roll">Degrees, as the module sends it (no calibration); NaN with none.</param>
public readonly record struct ImplementGpsFix(
    int TimeCentiseconds,
    double Latitude,
    double Longitude,
    int FixQuality,
    int Satellites,
    double Hdop,
    double Altitude,
    double Age,
    double SpeedKnots,
    double Heading,
    bool HasDualHeading,
    double Roll,
    bool IsPaogi);

/// <summary>Reads the implement's sentences. Only <c>$PANDA</c> and <c>$PAOGI</c>, with a right checksum.</summary>
public static class ImplementGpsSentence
{
    private const int MaxFields = 20;

    /// <summary>One sentence (a datagram may end in CR LF). False for anything else.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out ImplementGpsFix fix)
    {
        fix = default;
        int end = data.Length;
        while (end > 0 && (data[end - 1] == '\r' || data[end - 1] == '\n' || data[end - 1] == ' ' || data[end - 1] == 0)) end--;
        data = data[..end];
        if (data.Length < 12 || data[0] != '$') return false;
        int star = data.LastIndexOf((byte)'*');
        if (star < 0 || star + 3 != data.Length) return false;
        byte sum = 0;
        for (int i = 1; i < star; i++) sum ^= data[i];
        if (!Utf8Parser.TryParse(data.Slice(star + 1, 2), out byte given, out int used, 'X') || used != 2 || given != sum)
            return false;

        var body = data.Slice(1, star - 1);
        Span<int> starts = stackalloc int[MaxFields];
        Span<int> ends = stackalloc int[MaxFields];
        int n = 0, from = 0;
        for (int i = 0; i <= body.Length && n < MaxFields; i++)
        {
            if (i == body.Length || body[i] == ',')
            {
                starts[n] = from; ends[n] = i; n++;
                from = i + 1;
            }
        }
        if (n < 14) return false;
        ReadOnlySpan<byte> F(int k, ReadOnlySpan<byte> b, Span<int> s, Span<int> e) => b[s[k]..e[k]];

        var type = F(0, body, starts, ends);
        bool paogi = type.SequenceEqual("PAOGI"u8);
        if (!paogi && !type.SequenceEqual("PANDA"u8)) return false;

        int time = ParseTime(F(1, body, starts, ends));
        double lat = ParseLatLon(F(2, body, starts, ends));
        double lon = ParseLatLon(F(4, body, starts, ends));
        if (double.IsNaN(lat) || double.IsNaN(lon)) return false;
        var ns = F(3, body, starts, ends);
        var ew = F(5, body, starts, ends);
        if (ns.Length > 0 && ns[0] == 'S') lat = -lat;
        if (ew.Length > 0 && ew[0] == 'W') lon = -lon;
        int fixQ = Int(F(6, body, starts, ends), 0);
        int sats = Int(F(7, body, starts, ends), 0);
        double hdop = Num(F(8, body, starts, ends));
        double alt = Num(F(9, body, starts, ends));
        double age = Num(F(10, body, starts, ends));
        double knots = Num(F(11, body, starts, ends));

        double heading = double.NaN, roll = double.NaN;
        var hf = F(12, body, starts, ends);
        var rf = F(13, body, starts, ends);
        if (paogi)
        {
            heading = Num(hf);
            roll = Num(rf);
        }
        else
        {
            // $PANDA: the IMU's heading and roll as whole tenths of a degree, 65535 = no IMU
            int h = Int(hf, 65535);
            if (h != 65535)
            {
                heading = h * 0.1;
                if (rf.Length > 0 && Utf8Parser.TryParse(rf, out int r, out int ru) && ru == rf.Length) roll = r * 0.1;
            }
        }
        fix = new ImplementGpsFix(time, lat, lon, fixQ, sats, hdop, alt, age, knots, heading,
            paogi && !double.IsNaN(heading), roll, paogi);
        return true;
    }

    // hhmmss.ss -> hundredths of a second of the day; -1 when it does not read
    private static int ParseTime(ReadOnlySpan<byte> f)
    {
        if (f.Length < 6 || !Utf8Parser.TryParse(f, out double v, out int used) || used != f.Length) return -1;
        int hh = (int)(v / 10000), mm = (int)(v / 100) % 100;
        double ss = v - hh * 10000 - mm * 100;
        if (hh > 23 || mm > 59 || ss < 0 || ss >= 61) return -1;
        return hh * 360000 + mm * 6000 + (int)Math.Round(ss * 100);
    }

    // ddmm.mmmm (lat) or dddmm.mmmm (lon) -> degrees; NaN when empty or not a number
    private static double ParseLatLon(ReadOnlySpan<byte> f)
    {
        if (f.Length == 0 || !Utf8Parser.TryParse(f, out double v, out int used) || used != f.Length) return double.NaN;
        double deg = Math.Floor(v / 100);
        return deg + (v - deg * 100) / 60.0;
    }

    private static double Num(ReadOnlySpan<byte> f) =>
        f.Length > 0 && Utf8Parser.TryParse(f, out double v, out int used) && used == f.Length ? v : double.NaN;

    private static int Int(ReadOnlySpan<byte> f, int none)
    {
        if (f.Length == 0) return none;
        if (Utf8Parser.TryParse(f, out int v, out int used) && used == f.Length) return v;
        // a receiver may write the fix or the satellites as "4.0"
        return Utf8Parser.TryParse(f, out double d, out used) && used == f.Length ? (int)d : none;
    }
}

/// <summary>Where the implement is, from its GPS and the tractor's pin.</summary>
/// <param name="AntennaE">The position antenna on the field's plane.</param>
/// <param name="CentreE">The point of the implement's centre line under the antennas: the antenna's height through the
/// roll and its side offset taken out.</param>
/// <param name="HeadingRad">The implement's heading: from that point to the pin (a trailed implement's centre line runs
/// through its pin), radians, 0 north, clockwise.</param>
/// <param name="WorkE">Its working centre: the model's distance behind the pin along that heading.</param>
/// <param name="AcrossM">The working centre against the model's: + right of the model's heading, metres.</param>
/// <param name="AlongM">... + ahead of it.</param>
/// <param name="HeadingDiffDeg">Its heading less the model's, degrees.</param>
/// <param name="ReceiverHeadingDiffDeg">The receiver's own heading (a dual's, or an IMU's) less <paramref name="HeadingRad"/>,
/// degrees; NaN with none.</param>
public readonly record struct ImplementGpsPose(
    double AntennaE, double AntennaN,
    double CentreE, double CentreN,
    double HeadingRad,
    double WorkE, double WorkN,
    double AcrossM, double AlongM,
    double HeadingDiffDeg,
    double ReceiverHeadingDiffDeg);

/// <summary>The implement's pose from its antenna and the tractor's pin (the model's: the tractor's GPS places it to a
/// centimetre). Pure, for tests.</summary>
public static class ImplementGpsGeometry
{
    /// <summary>An antenna nearer the pin than this gives no heading.</summary>
    public const double MinPinDistance = 0.8;

    /// <param name="rollDeg">The implement's roll, calibrated (+ = tilted right, the tractor's convention); NaN = none.</param>
    /// <param name="receiverHeadingDeg">The receiver's own heading; NaN = none.</param>
    /// <param name="behindPin">The pin to the working centre (<c>RigGeometry.ToolBehindPivot</c>).</param>
    /// <param name="antennaHeight">The antenna's height over the ground (m).</param>
    /// <param name="antennaRight">How far right of the implement's centre line the position antenna is (m, - = left).</param>
    public static ImplementGpsPose? Solve(
        double antennaE, double antennaN, double rollDeg, double receiverHeadingDeg,
        double pinE, double pinN, double behindPin, double antennaHeight, double antennaRight,
        double modelWorkE, double modelWorkN, double modelHeadingRad)
    {
        double dx0 = pinE - antennaE, dy0 = pinN - antennaN;
        if (dx0 * dx0 + dy0 * dy0 < MinPinDistance * MinPinDistance) return null;

        // The heading sets which way "right" is for the roll and the side offset, and those move the point the heading
        // is taken from: three rounds settle it to well under a millimetre.
        double heading = Math.Atan2(dx0, dy0);
        double cE = antennaE, cN = antennaN;
        double roll = double.IsNaN(rollDeg) ? 0 : rollDeg * Math.PI / 180.0;
        for (int round = 0; round < 3; round++)
        {
            double rE = Math.Cos(heading), rN = -Math.Sin(heading);    // right of the heading
            // a tilt to the right carries a high antenna right of the ground under it (AntennaToPivotTransform)
            double side = -Math.Sin(roll) * antennaHeight - antennaRight;
            cE = antennaE + rE * side;
            cN = antennaN + rN * side;
            double dx = pinE - cE, dy = pinN - cN;
            if (dx * dx + dy * dy < MinPinDistance * MinPinDistance) return null;
            heading = Math.Atan2(dx, dy);
        }
        if (heading < 0) heading += 2 * Math.PI;

        double workE = pinE - Math.Sin(heading) * behindPin;
        double workN = pinN - Math.Cos(heading) * behindPin;
        double dE = workE - modelWorkE, dN = workN - modelWorkN;
        double across = dE * Math.Cos(modelHeadingRad) - dN * Math.Sin(modelHeadingRad);
        double along = dE * Math.Sin(modelHeadingRad) + dN * Math.Cos(modelHeadingRad);
        double headingDiff = Wrap180((heading - modelHeadingRad) * 180.0 / Math.PI);
        double receiverDiff = double.IsNaN(receiverHeadingDeg) ? double.NaN
            : Wrap180(receiverHeadingDeg - heading * 180.0 / Math.PI);
        return new ImplementGpsPose(antennaE, antennaN, cE, cN, heading, workE, workN, across, along, headingDiff, receiverDiff);
    }

    public static double Wrap180(double deg)
    {
        deg %= 360.0;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }
}
