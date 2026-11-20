// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the numbers behind the field data card (#321), after
/// AgOpenGPS <c>CFieldData</c> / <c>FormFieldData</c>. Worked area and the trip area live
/// in the coverage service; this adds the trip distance and gathers the rest.
/// </summary>
public partial class MainViewModel
{
    /// <summary>A step shorter than this isn't counted (AgOpenGPS: 0.1 m), so a parked
    /// tractor's position noise doesn't add up.</summary>
    internal const double TripMinStepMeters = 0.1;

    /// <summary>A step longer than this is a jump (first fix, simulator moved, field
    /// changed), not travel.</summary>
    internal const double TripMaxStepMeters = 50;

    private bool _tripHasLast;
    private double _tripLastEasting, _tripLastNorthing;

    /// <summary>Distance driven since the trip was last reset, metres. Starts again when a
    /// field is opened and when the applied area is deleted, like AgOpenGPS's distanceUser.</summary>
    public double TripDistanceMeters { get; private set; }

    /// <summary>Count the travel to this fix into the trip distance.</summary>
    internal void AccumulateTripDistance(double easting, double northing)
    {
        if (!_tripHasLast)
        {
            _tripHasLast = true;
            _tripLastEasting = easting;
            _tripLastNorthing = northing;
            return;
        }
        double de = easting - _tripLastEasting, dn = northing - _tripLastNorthing;
        double step = Math.Sqrt(de * de + dn * dn);
        if (step < TripMinStepMeters) return;
        if (step <= TripMaxStepMeters) TripDistanceMeters += step;
        _tripLastEasting = easting;
        _tripLastNorthing = northing;
    }

    /// <summary>The trip distance starts again; the next fix is its first point.</summary>
    internal void RestartTripDistance()
    {
        TripDistanceMeters = 0;
        _tripHasLast = false;
    }

    /// <summary>The card's Reset button: trip area and trip distance back to zero.</summary>
    public void ResetTrip()
    {
        _coverageMapService.ResetUserArea();
        RestartTripDistance();
    }

    /// <summary>Width of the sections that are on now, metres: the part of the implement that is
    /// working. A section counts while <see cref="AgOpenWeb.Services.Interfaces.SectionControlState.IsOn"/>,
    /// the state the coverage map paints by, so the Rate and the Trip area count the same sections.</summary>
    public double WorkingWidthMeters
    {
        get
        {
            var states = _sectionControlService.SectionStates;
            int n = Math.Min(_sectionControlService.NumSections, states?.Count ?? 0);
            double width = 0;
            for (int i = 0; i < n; i++)
                if (states![i].IsOn) width += Math.Max(0, states[i].Width);
            return width;
        }
    }

    /// <summary>The card's Rate, ha/hr: the speed times the width of the sections that are on
    /// (AgOpenGPS's formula, <c>CFieldData.WorkRateHectares</c>: width × km/h × 0.1). With every
    /// section off (Off, or Auto and off in the headland) nothing is being worked and the rate is
    /// 0; with half the boom on it is half. AgOpenGPS counts the whole implement whether its
    /// sections are on or not, so the rate read the same parked in the headland. Fork only.</summary>
    public double WorkRateHaPerHour => WorkingWidthMeters * Math.Abs(State.Vehicle.Speed) * 3.6 * 0.1;

    /// <summary>Boundary area less inner boundaries, m²; 0 with no boundary.</summary>
    public double FieldBoundaryAreaSqM
    {
        get
        {
            var b = State.Field.CurrentBoundary;
            return b != null && b.IsValid ? Math.Max(0, b.AreaHectares * 10000.0) : 0;
        }
    }
}
