// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Visual tree snapshots for diff_visual_tree. Keyed by element reference, not path: paths are
    /// child indices and BringToFront shifts every sibling, so a path diff would report everything.
    /// The store is small (oldest evicted) and cleared on Play Mode changes and domain reload; the
    /// strong references keep detached elements alive until their snapshot is evicted.
    /// </summary>
    [InitializeOnLoad]
    internal static class VisualTreeSnapshots
    {
        internal const int MaxSnapshots = 8;
        internal const int MaxElements = 20000;

        private static readonly List<Snapshot> Store = new List<Snapshot>();
        private static int _counter;

        static VisualTreeSnapshots()
        {
            EditorApplication.playModeStateChanged += _ => Clear();
        }

        /// <summary>Compact per-element state compared by the diff.</summary>
        internal sealed class ElementState
        {
            public string HostId, Path, Label;
            public VisualElement Parent;
            public bool Displayed, Enabled, HasLayout;
            public string[] Classes;
            public string Text;
            public Rect Layout, World;
            public float Opacity;
        }

        internal sealed class Snapshot
        {
            public string Id;
            public string Target;
            public DateTime Created;
            public Dictionary<VisualElement, ElementState> Elements;
            public bool Truncated;
        }

        internal static Snapshot Take(string target, IEnumerable<Component> hosts)
        {
            var snapshot = Capture(hosts);
            snapshot.Id = "vt_" + (++_counter) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            snapshot.Target = target;
            Store.Add(snapshot);
            while (Store.Count > MaxSnapshots) Store.RemoveAt(0);
            return snapshot;
        }

        internal static Snapshot Find(string id) => Store.FirstOrDefault(s => s.Id == id);

        /// <summary>Replaces a stored snapshot's state with <paramref name="current"/> (diff with update_snapshot).</summary>
        internal static void Rebase(Snapshot snapshot, Snapshot current)
        {
            snapshot.Elements = current.Elements;
            snapshot.Truncated = current.Truncated;
            snapshot.Created = current.Created;
        }

        internal static void Clear() => Store.Clear();

        /// <summary>The whole tree of every host (nested hosts' subtrees under their own host), uncapped by depth.</summary>
        internal static Snapshot Capture(IEnumerable<Component> hosts)
        {
            var list = hosts.ToList();
            var hostRoots = UIToolkitPanels.HostRoots(list);
            var elements = new Dictionary<VisualElement, ElementState>();
            var truncated = false;
            foreach (var host in list)
            {
                var root = UIToolkitPanels.GetRoot(host);
                if (root?.panel == null) continue;
                UIToolkitPanels.ValidateLayout(root.panel);
                var hostId = ObjectIdHelper.GetSerializableId(host.gameObject);
                var display = new UIToolkitPanels.DisplayCache();
                foreach (var entry in UIToolkitPanels.Walk(root, exclude: hostRoots))
                {
                    if (elements.Count >= MaxElements) { truncated = true; break; }
                    var element = entry.Value;
                    if (elements.ContainsKey(element)) continue;
                    var hasLayout = UIToolkitPanels.HasLayout(element);
                    elements[element] = new ElementState
                    {
                        HostId = hostId,
                        Path = entry.Key,
                        Label = UIToolkitPanels.Label(element),
                        Parent = element.hierarchy.parent,
                        Displayed = display.IsDisplayed(element),
                        Enabled = element.enabledInHierarchy,
                        HasLayout = hasLayout,
                        Classes = element.GetClasses().OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                        Text = UIToolkitPanels.GetText(element),
                        Layout = element.layout,
                        World = element.worldBound,
                        Opacity = element.resolvedStyle.opacity
                    };
                }
            }
            return new Snapshot { Created = DateTime.UtcNow, Elements = elements, Truncated = truncated };
        }

        /// <summary>
        /// Differences between two captures. Added/removed subtrees are reported once at their top
        /// element. A displayed/enabled change inherited from a parent with the same change, and
        /// bounds changes of elements hidden before or after, are left out, so opening a dialog
        /// reports the dialog, not every element inside it. Bounds compare the layout rect relative
        /// to the parent, so a moved container doesn't report its children.
        /// </summary>
        internal static Diff Compare(Snapshot before, Snapshot after)
        {
            var diff = new Diff();
            var addedSet = new HashSet<VisualElement>(after.Elements.Keys.Where(e => !before.Elements.ContainsKey(e)));
            var removedSet = new HashSet<VisualElement>(before.Elements.Keys.Where(e => !after.Elements.ContainsKey(e)));
            diff.Added = TopLevel(addedSet, after.Elements);
            diff.Removed = TopLevel(removedSet, before.Elements);

            foreach (var pair in after.Elements)
            {
                if (!before.Elements.TryGetValue(pair.Key, out var old)) continue;
                var now = pair.Value;
                var changes = new Dictionary<string, object>();
                // Reparented (e.g. an item dropped into another slot): its parent-relative layout may not change.
                if (!ReferenceEquals(old.Parent, now.Parent))
                    changes["parent"] = new
                    {
                        before = old.Parent != null ? UIToolkitPanels.Label(old.Parent) : null,
                        after = now.Parent != null ? UIToolkitPanels.Label(now.Parent) : null,
                        path_before = old.Path
                    };
                ElementState parentOld = null, parentNow = null;
                var parentInBoth = ReferenceEquals(old.Parent, now.Parent) && now.Parent != null &&
                                   before.Elements.TryGetValue(now.Parent, out parentOld) && after.Elements.TryGetValue(now.Parent, out parentNow);

                if (old.Displayed != now.Displayed && !(parentInBoth && parentOld.Displayed == old.Displayed && parentNow.Displayed == now.Displayed && parentOld.Displayed != parentNow.Displayed))
                    changes["displayed"] = new { before = old.Displayed, after = now.Displayed };
                if (old.Enabled != now.Enabled && !(parentInBoth && parentOld.Enabled == old.Enabled && parentNow.Enabled == now.Enabled && parentOld.Enabled != parentNow.Enabled))
                    changes["enabled"] = new { before = old.Enabled, after = now.Enabled };
                if (!old.Classes.SequenceEqual(now.Classes))
                    changes["classes"] = new { added = now.Classes.Except(old.Classes).ToArray(), removed = old.Classes.Except(now.Classes).ToArray() };
                if (!string.Equals(old.Text, now.Text, StringComparison.Ordinal))
                    changes["text"] = new { before = old.Text, after = now.Text };
                if (Mathf.Abs(old.Opacity - now.Opacity) > 0.01f)
                    changes["opacity"] = new { before = (float)Math.Round(old.Opacity, 3), after = (float)Math.Round(now.Opacity, 3) };
                if (old.Displayed && now.Displayed && old.HasLayout && now.HasLayout && Moved(old.Layout, now.Layout))
                    changes["bounds"] = new { before = RectJson(old.World), after = RectJson(now.World) };
                if (changes.Count > 0) diff.Changed.Add((pair.Key, now, changes));
            }
            return diff;
        }

        internal sealed class Diff
        {
            public List<(VisualElement element, ElementState state, int subtree)> Added, Removed;
            public List<(VisualElement element, ElementState state, Dictionary<string, object> changes)> Changed =
                new List<(VisualElement, ElementState, Dictionary<string, object>)>();
        }

        private static List<(VisualElement, ElementState, int)> TopLevel(HashSet<VisualElement> set, Dictionary<VisualElement, ElementState> states)
        {
            var sizes = new Dictionary<VisualElement, int>();
            foreach (var element in set)
            {
                var top = element;
                for (var parent = states[element].Parent; parent != null && set.Contains(parent); parent = states[parent].Parent) top = parent;
                sizes[top] = sizes.TryGetValue(top, out var n) ? n + 1 : 1;
            }
            return sizes.Select(s => (s.Key, states[s.Key], s.Value)).OrderBy(x => x.Item2.HostId, StringComparer.Ordinal).ThenBy(x => x.Item2.Path, StringComparer.Ordinal).ToList();
        }

        private static bool Moved(Rect a, Rect b)
        {
            bool Diff(float x, float y) => float.IsNaN(x) != float.IsNaN(y) || !float.IsNaN(x) && Mathf.Abs(x - y) > 0.5f;
            return Diff(a.x, b.x) || Diff(a.y, b.y) || Diff(a.width, b.width) || Diff(a.height, b.height);
        }

        internal static object RectJson(Rect r) =>
            float.IsNaN(r.x) || float.IsNaN(r.width) ? null : new { x = (float)Math.Round(r.x, 1), y = (float)Math.Round(r.y, 1), width = (float)Math.Round(r.width, 1), height = (float)Math.Round(r.height, 1) };
    }
}
