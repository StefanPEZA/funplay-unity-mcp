// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        [Description("Compare the live UI Toolkit tree with a snapshot from get_visual_tree(snapshot=true): elements added and removed (each subtree once, at its top element, with subtree_size) and elements whose displayed, enabled, classes, text, opacity or bounds changed. Elements are matched by reference, so siblings moved by BringToFront are not reported; changes inherited from a parent with the same change (a dialog's children appearing with it) and bounds of hidden elements are left out. update_snapshot=true replaces the snapshot with the current state for the next diff. Snapshots (8 kept) are cleared on Play Mode changes and script reloads.")]
        [ReadOnlyTool]
        public static object DiffVisualTree(
            [ToolParam("snapshot_id from get_visual_tree(snapshot=true)")] string snapshot_id,
            [ToolParam("Replace the snapshot with the current state afterwards", Required = false)] bool update_snapshot = false,
            [ToolParam("Maximum added + removed + changed entries, 1..1000", Required = false)] int max_changes = 200)
        {
            try
            {
                var snapshot = VisualTreeSnapshots.Find(snapshot_id ?? "")
                    ?? throw new UIToolkitException("SNAPSHOT_NOT_FOUND", "Unknown snapshot_id; snapshots are cleared on Play Mode changes and script reloads, and only the last 8 are kept. Take a new one with get_visual_tree(snapshot=true).");
                max_changes = Mathf.Clamp(max_changes, 1, 1000);
                List<Component> hosts;
                try { hosts = ResolveHosts(snapshot.Target); }
                catch (UIToolkitException ex) when (ex.Code == "NO_UI_TOOLKIT_HOSTS") { hosts = new List<Component>(); }
                var current = VisualTreeSnapshots.Capture(hosts);
                var diff = VisualTreeSnapshots.Compare(snapshot, current);
                var ageMs = (int)(DateTime.UtcNow - snapshot.Created).TotalMilliseconds;
                if (update_snapshot) VisualTreeSnapshots.Rebase(snapshot, current);

                var remaining = max_changes;
                IEnumerable<T> Take<T>(IEnumerable<T> source)
                {
                    foreach (var item in source)
                    {
                        if (remaining <= 0) yield break;
                        remaining--;
                        yield return item;
                    }
                }
                var changed = Take(diff.Changed).Select(c => (object)new { host_id = c.state.HostId, element_path = c.state.Path, element = c.state.Label, changes = c.changes }).ToList();
                var added = Take(diff.Added).Select(a => (object)new
                {
                    host_id = a.state.HostId, element_path = a.state.Path, element = a.state.Label, displayed = a.state.Displayed,
                    classes = a.state.Classes, bounds = a.state.Displayed ? VisualTreeSnapshots.RectJson(a.state.World) : null, subtree_size = a.subtree
                }).ToList();
                var removed = Take(diff.Removed).Select(r => (object)new
                {
                    host_id = r.state.HostId, snapshot_element_path = r.state.Path, element = r.state.Label, subtree_size = r.subtree
                }).ToList();
                var total = diff.Added.Count + diff.Removed.Count + diff.Changed.Count;
                return Response.Success(total == 0
                        ? "No changes since the snapshot."
                        : $"{diff.Changed.Count} changed, {diff.Added.Count} added, {diff.Removed.Count} removed since the snapshot.",
                    new
                    {
                        snapshot_id = snapshot.Id,
                        snapshot_age_ms = ageMs,
                        changed,
                        added,
                        removed,
                        counts = new { changed = diff.Changed.Count, added = diff.Added.Count, removed = diff.Removed.Count },
                        truncated = total > max_changes,
                        snapshot_truncated = snapshot.Truncated || current.Truncated ? (bool?)true : null,
                        snapshot_updated = update_snapshot
                    });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("VISUAL_TREE_DIFF_FAILED", new { detail = ex.Message }); }
        }
    }
}
