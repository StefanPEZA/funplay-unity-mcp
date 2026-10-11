// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Hierarchy")]
    internal static class HierarchyFunctions
    {
        [Description("Browse the scene hierarchy tree. Returns a tree-like view of GameObjects " +
                     "with their components, active state, and tags. " +
                     "Use root_name to start from a specific object, or leave empty for full scene.")]
        [ReadOnlyTool]
        public static string GetHierarchy(
            [ToolParam("Root object name, hierarchy path, or instance ID to start from (empty = entire scene). Finds inactive objects too.", Required = false)] string root_name = "",
            [ToolParam("Maximum depth to traverse (1-10)", Required = false)] int depth = 3,
            [ToolParam("Include component names on each object", Required = false)] bool include_components = true,
            [ToolParam("Include inactive objects", Required = false)] bool include_inactive = true,
            [ToolParam("Revision from a previous reply: if the result is unchanged, only {unchanged:true, revision} returns. Replies of 1 KB or more carry a revision.", Required = false)] string if_revision = null)
        {
            try
            {
                depth = Mathf.Clamp(depth, 1, 10);
                var sb = new StringBuilder();

                if (!string.IsNullOrEmpty(root_name))
                {
                    // ObjectsHelper already searches every loaded scene (additively loaded ones
                    // included) plus the open prefab stage, and finds inactive objects.
                    var root = ObjectsHelper.FindTarget(root_name);
                    if (root == null)
                        return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { root_name });

                    PrintNode(sb, root.transform, 0, depth, include_components, include_inactive);
                }
                else
                {
                    // Full hierarchy: every loaded scene, not just the active one.
                    var activeScene = SceneManager.GetActiveScene();
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.isLoaded) continue;
                        sb.AppendLine(scene == activeScene ? $"Scene: {scene.name}" : $"Scene: {scene.name} (additive)");
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            if (!include_inactive && !root.activeSelf) continue;
                            PrintNode(sb, root.transform, 0, depth, include_components, include_inactive);
                        }
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        // --- Helpers ---

        private static void PrintNode(StringBuilder sb, Transform t, int indent, int maxDepth,
            bool includeComponents, bool includeInactive)
        {
            if (!includeInactive && !t.gameObject.activeSelf) return;

            string prefix = indent > 0 ? new string(' ', indent * 2) + "|- " : "";
            string active = t.gameObject.activeSelf ? "" : " [INACTIVE]";
            string tag = t.tag != "Untagged" ? $" tag={t.tag}" : "";

            if (includeComponents)
            {
                string comps = GetComponentSummary(t.gameObject);
                sb.AppendLine($"{prefix}{t.name}{active}{tag} [{comps}]");
            }
            else
            {
                sb.AppendLine($"{prefix}{t.name}{active}{tag}");
            }

            if (indent < maxDepth)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    // A run of look-alike siblings (Cubie (0, 1, 0), Cubie (1, 1, 0) ...) at the depth limit, or without
                    // children, would print one identical line each; print the run once.
                    var run = LookAlikeRun(t, i, includeComponents, includeInactive, leavesOnly: indent + 1 < maxDepth);
                    if (run >= MinCollapsedRun)
                    {
                        PrintRun(sb, t, i, run, indent + 1, includeComponents, includeInactive);
                        i += run - 1;
                        continue;
                    }
                    PrintNode(sb, t.GetChild(i), indent + 1, maxDepth, includeComponents, includeInactive);
                }
            }
            else if (t.childCount > 0)
            {
                string childPrefix = new string(' ', (indent + 1) * 2) + "|- ";
                sb.AppendLine($"{childPrefix}... ({Children(t.childCount)})");
            }
        }

        private const int MinCollapsedRun = 3;

        private static string Children(int count) => count == 1 ? "1 child" : $"{count} children";

        /// <summary>
        /// How many siblings from <paramref name="start"/> on share a name stem, active state, tag and components
        /// (inactive siblings that would not be printed end the run; with <paramref name="leavesOnly"/>, so does a
        /// sibling with children, whose subtree would be printed).
        /// </summary>
        private static int LookAlikeRun(Transform parent, int start, bool includeComponents, bool includeInactive, bool leavesOnly)
        {
            var first = parent.GetChild(start);
            if (!includeInactive && !first.gameObject.activeSelf) return 1;
            if (leavesOnly && first.childCount > 0) return 1;
            var key = LookAlikeKey(first, includeComponents);
            var run = 1;
            for (int i = start + 1; i < parent.childCount; i++)
            {
                var sibling = parent.GetChild(i);
                if (!includeInactive && !sibling.gameObject.activeSelf) break;
                if (leavesOnly && sibling.childCount > 0) break;
                if (LookAlikeKey(sibling, includeComponents) != key) break;
                run++;
            }
            return run;
        }

        private static string LookAlikeKey(Transform t, bool includeComponents)
            => NameStem(t.name) + "|" + t.gameObject.activeSelf + "|" + t.tag + "|" + (includeComponents ? GetComponentSummary(t.gameObject) : "");

        /// <summary>The name without a trailing " (...)", number or "_number": "Cubie (1, 0, -1)" and "Tile_12" give "Cubie" and "Tile".</summary>
        internal static string NameStem(string name)
        {
            var stem = name.TrimEnd();
            if (stem.EndsWith(")"))
            {
                var open = stem.LastIndexOf('(');
                if (open > 0) stem = stem.Substring(0, open);
            }
            stem = stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd(' ', '_', '-', '.');
            return stem.Length > 0 ? stem : name;
        }

        private static void PrintRun(StringBuilder sb, Transform parent, int start, int count, int indent,
            bool includeComponents, bool includeInactive)
        {
            var first = parent.GetChild(start);
            var last = parent.GetChild(start + count - 1);
            var grandchildren = 0;
            for (int i = start; i < start + count; i++) grandchildren += parent.GetChild(i).childCount;

            string prefix = new string(' ', indent * 2) + "|- ";
            string active = first.gameObject.activeSelf ? "" : " [INACTIVE]";
            string tag = first.tag != "Untagged" ? $" tag={first.tag}" : "";
            string comps = includeComponents ? $" [{GetComponentSummary(first.gameObject)}]" : "";
            sb.AppendLine($"{prefix}{count}x {first.name} .. {last.name}{active}{tag}{comps}");
            if (grandchildren > 0)
                sb.AppendLine($"{new string(' ', (indent + 1) * 2)}|- ... ({Children(grandchildren)} in total)");
        }

        private static string GetComponentSummary(GameObject go)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                string name = comp.GetType().Name;
                if (name == "Transform" || name == "RectTransform") continue; // Always present, skip
                names.Add(name);
            }
            return names.Count > 0 ? string.Join(", ", names) : "-";
        }
    }
}
