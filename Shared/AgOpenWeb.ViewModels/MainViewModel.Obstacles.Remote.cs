// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using AgOpenWeb.Models;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the boundary list's choice per obstacle (what the tractor does at an
/// inner boundary, <see cref="ObstacleAction"/>), one row at a time or every obstacle at once
/// from a column's title. Saved with the field like the drive-through and hard flags. Also the
/// obstacles ticked in the list, deleted together.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// The obstacle on boundary list row <paramref name="listIndex"/> (the index the list and
    /// <c>boundary.select</c> use) gets <paramref name="action"/>. The row stays selected.
    /// </summary>
    public void RemoteSetObstacleAction(int listIndex, ObstacleAction action)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        int i = InnerIndexOfListRow(boundary, listIndex);
        if (i < 0)
        {
            StatusMessage = "Only an inner boundary has a choice at the obstacle";
            return;
        }

        boundary.InnerBoundaries[i].AtObstacle = action;
        SaveObstacleChoice(boundary, fieldPath, listIndex);
        StatusMessage = $"Inner {i + 1}: {Describe(action)}";
    }

    /// <summary>Every inner boundary of the field gets <paramref name="action"/>.</summary>
    public void RemoteSetAllObstacleActions(ObstacleAction action)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        int count = 0;
        foreach (var inner in boundary.InnerBoundaries)
        {
            if (!inner.IsValid) continue;
            inner.AtObstacle = action;
            count++;
        }
        if (count == 0)
        {
            StatusMessage = "The field has no inner boundaries";
            return;
        }

        SaveObstacleChoice(boundary, fieldPath, SelectedBoundaryIndex);
        StatusMessage = $"All {count} inner boundaries: {Describe(action)}";
    }

    /// <summary>
    /// The obstacles on boundary list rows <paramref name="listRows"/> are deleted at once, with
    /// one confirmation in the web client instead of one per row: a field's trees drawn again (a
    /// tester had over 40 to delete one by one). Only obstacles: the outer boundary is never
    /// deleted this way (on its own it can go only when it is the last one, as AgOpenGPS).
    /// <paramref name="listRowCount"/> is how many rows the client's list had when the operator
    /// ticked them; a list of another length (a row added or deleted in between) changes nothing,
    /// as does a row that is not an obstacle, so a row number never names another obstacle. Fork only.
    /// </summary>
    public void RemoteDeleteObstacles(int listRowCount, IReadOnlyCollection<int> listRows)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        int rows = (boundary.OuterBoundary is { IsValid: true } ? 1 : 0) + boundary.InnerBoundaries.Count(b => b.IsValid);
        if (listRowCount != rows)
        {
            ReportFailure("The boundary list changed: tick the obstacles again");
            return;
        }

        var inner = new SortedSet<int>();
        foreach (int row in listRows)
        {
            int i = InnerIndexOfListRow(boundary, row);
            if (i < 0)
            {
                ReportFailure("Only obstacles can be deleted together: nothing deleted");
                return;
            }
            inner.Add(i);
        }
        if (inner.Count == 0)
        {
            ReportFailure("Tick the obstacles to delete");
            return;
        }

        foreach (int i in inner.Reverse()) boundary.InnerBoundaries.RemoveAt(i);
        SaveFieldBoundary(boundary, fieldPath);
        RefreshBoundaryList();
        SetCurrentBoundary(boundary);

        // As one delete: with no boundary left, the background photo placed on it goes too.
        bool left = boundary.OuterBoundary is { IsValid: true } || boundary.InnerBoundaries.Any(b => b.IsValid);
        if (!left) DeleteBackgroundImage(fieldPath);
        StatusMessage = (inner.Count == 1 ? "1 obstacle deleted" : $"{inner.Count} obstacles deleted")
            + (left ? "" : "; background image removed");
    }

    private bool TryLoadBoundaryForEdit(out Boundary boundary, out string fieldPath)
    {
        boundary = null!;
        fieldPath = "";
        if (string.IsNullOrEmpty(CurrentFieldName)) return false;
        fieldPath = Path.Combine(_settingsService.Settings.FieldsDirectory, CurrentFieldName);
        var loaded = LoadFieldBoundary(fieldPath);
        if (loaded == null) return false;
        boundary = loaded;
        return true;
    }

    private void SaveObstacleChoice(Boundary boundary, string fieldPath, int keepSelected)
    {
        SaveFieldBoundary(boundary, fieldPath);
        SetCurrentBoundary(boundary);
        RefreshBoundaryList(keepSelection: true); // no tick sees the row unselected in between
        SelectedBoundaryIndex = keepSelected >= 0 && keepSelected < BoundaryItems.Count ? keepSelected : -1;
    }

    /// <summary>
    /// The index in <see cref="Boundary.InnerBoundaries"/> of list row <paramref name="listIndex"/>,
    /// or -1 for the outer boundary or no row: the list counts the valid outer boundary first,
    /// then the valid inner ones in order (<see cref="RefreshBoundaryList"/>).
    /// </summary>
    private static int InnerIndexOfListRow(Boundary boundary, int listIndex)
    {
        int row = boundary.OuterBoundary is { IsValid: true } ? 1 : 0;
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            if (!boundary.InnerBoundaries[i].IsValid) continue;
            if (row == listIndex) return i;
            row++;
        }
        return -1;
    }

    private static string Describe(ObstacleAction action) => action switch
    {
        ObstacleAction.GoRound => "go around",
        ObstacleAction.DriveThrough => "drive through",
        _ => "U-turn",
    };
}
