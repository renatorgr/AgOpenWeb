// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Threading.Tasks;
using AgOpenWeb.Services.GeoJson;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: moving the open field's drawing (fork only). The operator lines the
/// field up with the ground using the Offset Fix (the tractor on the map moves against the
/// drawing), then keeps it in the field: the drawing moves by the opposite of the offset and the
/// offset goes back to 0, so what the map shows is unchanged and is now saved.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The largest move, m. A field drawn on a photo is off by a few metres; more is a slip.</summary>
    public const double MaxFieldMove = 50.0;

    /// <summary>
    /// Move the open field's drawing by (<paramref name="dEasting"/>, <paramref name="dNorthing"/>)
    /// metres (<see cref="GeoJsonFieldService.MoveField"/>) and the Offset Fix by the same, so the
    /// tractor keeps its place on the map. The field is saved, moved on disk and opened again
    /// with its job. Refused with AutoSteer on or a boundary being recorded.
    /// </summary>
    public Task RemoteMoveFieldAsync(double dEasting, double dNorthing, bool moveTracks)
    {
        if (!IsFieldOpen || ActiveField is not { DirectoryPath.Length: > 0 } field)
        {
            ReportFailure("Open a field first");
            return Task.CompletedTask;
        }
        if (!double.IsFinite(dEasting) || !double.IsFinite(dNorthing)) return Task.CompletedTask;
        double distance = Math.Sqrt(dEasting * dEasting + dNorthing * dNorthing);
        if (distance < 0.005)
        {
            ReportFailure("Nothing to move: the offset is 0");
            return Task.CompletedTask;
        }
        if (distance > MaxFieldMove)
        {
            ReportFailure($"A field can be moved {MaxFieldMove.ToString("0", CultureInfo.InvariantCulture)} m at most");
            return Task.CompletedTask;
        }
        if (IsAutoSteerEngaged)
        {
            ReportFailure("Turn AutoSteer off before moving the field");
            return Task.CompletedTask;
        }
        if (IsBoundaryRecording)
        {
            ReportFailure("Finish the boundary recording before moving the field");
            return Task.CompletedTask;
        }

        string dir = field.DirectoryPath, name = CurrentFieldName;
        // Coverage worked with no job would be lost when the field closes: ask first, as Close does.
        if (TryShowUnsavedCoverageGuard(() => _ = MoveFieldCoreAsync(dir, name, dEasting, dNorthing, moveTracks)))
            return Task.CompletedTask;
        return MoveFieldCoreAsync(dir, name, dEasting, dNorthing, moveTracks);
    }

    private async Task MoveFieldCoreAsync(string dir, string name, double dEasting, double dNorthing, bool moveTracks)
    {
        string? task = _jobService.ActiveJob?.TaskName;
        // Opening a field puts the simulator at the field's origin; put it back where it was.
        var simAt = _simulatorService.CurrentPosition;
        double simHeading = _simulatorService.HeadingRadians;

        await CloseFieldAsync(); // saves the field, its tracks and the job's coverage first
        GeoJsonFieldService.FieldMoveResult? moved = null;
        try
        {
            var m = GeoJsonFieldService.MoveField(dir, dEasting, dNorthing, moveTracks);
            moved = m;
            _logger.LogInformation("[Field] Moved {Field} {Move}: {Boundaries} boundary, {Obstacles} obstacles, {Headland} headland parts, {Tracks} tracks, photo {Photo}",
                name, DescribeMove(dEasting, dNorthing), m.Boundaries, m.Obstacles, m.HeadlandParts, m.Tracks, m.Photo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Field] Could not move field {Field}", name);
        }

        if (task != null) await OpenFieldResumingJobAsync(dir, name, task);
        else await OpenFieldOnlyAsync(dir, name);

        if (simAt.Latitude != 0 || simAt.Longitude != 0)
        {
            SetSimulatorCoordinates(simAt.Latitude, simAt.Longitude);
            _simulatorService.SetHeading(simHeading);
        }

        if (moved == null)
        {
            ReportFailure("The field could not be moved; it is as it was");
            return;
        }
        // The tractor keeps its place against the drawing: the offset moves with it.
        OffsetFixSet(State.Field.DriftEasting + dEasting, State.Field.DriftNorthing + dNorthing);
        // The host's one way to tell the web client something is its failure toast.
        ReportFailure($"Field moved {DescribeMove(dEasting, dNorthing)}"
            + (moveTracks || !HasTracksOtherThanRecorded() ? "" : "; its tracks stay where they were"));
    }

    private bool HasTracksOtherThanRecorded()
    {
        foreach (var t in SavedTracks)
            if (!t.IsRecordedPath && !t.IsContour) return true;
        return false;
    }

    /// <summary>A move in words, east or west first: "1.20 m east, 0.40 m south".</summary>
    public static string DescribeMove(double dEasting, double dNorthing)
    {
        static string M(double v) => Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture) + " m";
        var parts = new System.Collections.Generic.List<string>(2);
        if (Math.Abs(dEasting) >= 0.005) parts.Add(M(dEasting) + (dEasting > 0 ? " east" : " west"));
        if (Math.Abs(dNorthing) >= 0.005) parts.Add(M(dNorthing) + (dNorthing > 0 ? " north" : " south"));
        return string.Join(", ", parts);
    }
}
