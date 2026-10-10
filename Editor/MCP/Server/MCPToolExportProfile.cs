// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using Funplay.Editor.Settings;

namespace Funplay.Editor.MCP.Server
{
    internal enum MCPToolExportProfile
    {
        Core,
        Full
    }

    internal static class MCPToolExportPolicy
    {
        /// <summary>
        /// Core base list: the everyday loop (inspect, edit, save, compile, play, verify, test) without
        /// Full. UI framework tools come from the detected UI pack, optional packs from settings.
        /// </summary>
        private static readonly HashSet<string> BaseCoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "execute_code",
            // Input and visual verification
            "simulate_key_press",
            "simulate_mouse_click",
            "simulate_mouse_drag",
            "simulate_ui_scroll",
            "raycast_at_point",
            "capture_scene_view",
            "capture_game_view",
            "record_game_view",
            "extract_recording_frames",
            "get_recording_frame",
            "get_visual_coordinates",
            "get_object_screen_bounds",
            // Editor state, compile, play, tasks
            "get_console_logs",
            "get_compilation_errors",
            "prepare_editor",
            "get_task",
            "cancel_task",
            "get_editor_state",
            "get_selection",
            "set_selection",
            "find_project_types",
            "get_tool_capabilities",
            "execute_menu_item",
            "run_tests",
            // Scenes
            "get_scene_info",
            "get_hierarchy",
            "open_scene",
            "save_scene",
            "list_scenes",
            "list_dirty_scenes",
            // GameObjects and components
            "find_game_objects",
            "get_game_object_info",
            "create_game_object",
            "delete_game_object",
            "set_active",
            "set_transform",
            "set_parent",
            "rename_game_object",
            "add_component",
            "instantiate_prefab",
            "list_components",
            "get_component_properties",
            "set_component_property",
            "set_component_properties",
            // Prefabs
            "get_prefab_stage",
            "open_prefab_stage",
            "save_prefab_stage",
            "close_prefab_stage",
            "set_prefab_property",
            "set_prefab_properties",
            // Assets and ScriptableObjects
            "find_assets",
            "find_references",
            "delete_asset",
            "get_scriptable_object",
            "set_scriptable_object_properties"
        };

        internal const string UiPackUgui = "ugui";
        internal const string UiPackUIToolkit = "uitk";
        internal const string UiPackBoth = "both";
        internal const string UiPackAuto = "auto";

        /// <summary>uGUI (Canvas) tools added to Core when the project uses uGUI.</summary>
        internal static readonly IReadOnlyCollection<string> UguiPackTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audit_ui", "inspect_ui_sprites", "get_ui_defaults", "create_project_ui"
        };

        /// <summary>UI Toolkit tools added to Core when the project uses UI Toolkit.</summary>
        internal static readonly IReadOnlyCollection<string> UIToolkitPackTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_ui_toolkit_info", "create_ui_toolkit_panel", "get_visual_tree", "click_ui_toolkit_element",
            "scroll_ui_toolkit", "pick_ui_toolkit_element", "configure_ui_toolkit_panel", "create_panel_settings"
        };

        /// <summary>Packs a user can add to Core (Tool Exposure window).</summary>
        internal static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> OptionalPacks =
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["animation"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "create_animation_clip", "create_animator_controller", "assign_animator", "get_animator_state",
                    "play_animator_state", "set_animator_parameter", "director_evaluate"
                },
                ["performance"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "profiler_start", "profiler_stop", "profiler_status", "get_frame_timing", "get_counters",
                    "get_performance_snapshot", "get_object_memory", "get_top_memory_objects", "analyze_scene_complexity",
                    "frame_debugger_enable", "frame_debugger_disable", "frame_debugger_get_events", "memory_take_snapshot",
                    "memory_list_snapshots", "memory_compare_snapshots", "memory_query_top_objects", "memory_query_references"
                },
                ["lighting"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "bake_lightmaps", "get_lighting_settings", "set_lighting_settings"
                },
                ["packages"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "list_packages", "install_package", "remove_package"
                }
            };

        private static readonly HashSet<string> CoreTools = BuildDefaultCore(UiPackBoth, null);

        /// <summary>Base list plus both UI packs: the default Core when no project-specific pack applies.</summary>
        public static IReadOnlyCollection<string> DefaultCoreTools => CoreTools;

        public static IReadOnlyCollection<string> BaseCoreToolList => BaseCoreTools;

        /// <summary>Default Core for a UI pack ("ugui", "uitk", "both") plus optional packs.</summary>
        public static HashSet<string> BuildDefaultCore(string uiPack, IEnumerable<string> optionalPacks)
        {
            var tools = new HashSet<string>(BaseCoreTools, StringComparer.OrdinalIgnoreCase);
            var pack = NormalizeUiPack(uiPack);
            if (pack != UiPackUIToolkit)
                tools.UnionWith(UguiPackTools);
            if (pack != UiPackUgui)
                tools.UnionWith(UIToolkitPackTools);
            if (optionalPacks != null)
                foreach (var name in optionalPacks)
                    if (name != null && OptionalPacks.TryGetValue(name, out var extra))
                        tools.UnionWith(extra);
            return tools;
        }

        /// <summary>"ugui", "uitk" or "both"; anything else (none detected, unknown) exposes both.</summary>
        public static string NormalizeUiPack(string uiPack)
        {
            switch ((uiPack ?? string.Empty).Trim().ToLowerInvariant())
            {
                case UiPackUgui: return UiPackUgui;
                case UiPackUIToolkit: return UiPackUIToolkit;
                default: return UiPackBoth;
            }
        }

        /// <summary>The UI pack in effect: the user's choice, or the stored detection result for "auto".</summary>
        public static string GetEffectiveUiPack(ISettingsController settings)
        {
            var choice = (settings?.MCPUiPack ?? UiPackAuto).Trim().ToLowerInvariant();
            return NormalizeUiPack(choice == UiPackAuto ? settings?.MCPDetectedUiPack : choice);
        }

        public static MCPToolExportProfile Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return MCPToolExportProfile.Core;

            switch (value.Trim().ToLowerInvariant())
            {
                case "full":
                    return MCPToolExportProfile.Full;
                default:
                    return MCPToolExportProfile.Core;
            }
        }

        public static string ToSettingValue(MCPToolExportProfile profile)
        {
            switch (profile)
            {
                case MCPToolExportProfile.Full:
                    return "full";
                default:
                    return "core";
            }
        }

        public static bool IsToolAllowed(
            string toolName,
            MCPToolExportProfile profile,
            bool coreToolsConfigured,
            IEnumerable<string> coreTools,
            IEnumerable<string> fullToolsExcluded)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            if (profile == MCPToolExportProfile.Full)
                return !ContainsTool(fullToolsExcluded, toolName);

            return coreToolsConfigured
                ? ContainsTool(coreTools, toolName)
                : CoreTools.Contains(toolName);
        }

        /// <summary>Exposure check against the current settings (profile, custom Core list, Full exclusions).</summary>
        public static bool IsToolAllowed(string toolName, ISettingsController settings)
        {
            var profile = Parse(settings?.MCPToolExportProfile);
            return IsToolAllowed(
                toolName,
                profile,
                true,
                profile == MCPToolExportProfile.Core ? GetEffectiveCoreTools(settings) : null,
                settings?.MCPFullToolsExcluded);
        }

        /// <summary>The Core list in effect: the user's custom list, or the default for the project's UI pack and optional packs.</summary>
        public static IReadOnlyCollection<string> GetEffectiveCoreTools(ISettingsController settings)
        {
            if (settings != null && settings.MCPCoreToolsConfigured)
                return new HashSet<string>(settings.MCPCoreTools ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            return GetDefaultCoreTools(settings);
        }

        /// <summary>The default Core for these settings (UI pack + optional packs), ignoring a custom list.</summary>
        public static IReadOnlyCollection<string> GetDefaultCoreTools(ISettingsController settings)
        {
            return settings == null ? CoreTools : BuildDefaultCore(GetEffectiveUiPack(settings), settings.MCPOptionalCorePacks);
        }

        /// <summary>True when <paramref name="selection"/> equals <paramref name="defaultTools"/>, so it can be stored as "default".</summary>
        public static bool IsDefaultCoreSelection(IEnumerable<string> selection, IReadOnlyCollection<string> defaultTools = null)
        {
            return selection != null && new HashSet<string>(selection, StringComparer.OrdinalIgnoreCase).SetEquals(defaultTools ?? CoreTools);
        }

        private static bool ContainsTool(IEnumerable<string> tools, string toolName)
        {
            if (tools == null)
                return false;

            foreach (var tool in tools)
            {
                if (string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static string BuildDescriptionPrefix(MCPToolExportProfile profile)
        {
            return profile == MCPToolExportProfile.Core ? "[core] " : string.Empty;
        }

        public static int GetSortRank(string toolName, MCPToolExportProfile profile)
        {
            return GetSortRank(toolName, profile, CoreTools);
        }

        public static int GetSortRank(string toolName, MCPToolExportProfile profile, IReadOnlyCollection<string> coreTools)
        {
            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase))
                return 10000;

            if (profile == MCPToolExportProfile.Core && ContainsTool(coreTools ?? CoreTools, toolName))
                return 100;

            return 1000;
        }

        public static string BuildDescriptionPrefix(string toolName, MCPToolExportProfile profile)
        {
            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase))
                return "[fallback] " + BuildDescriptionPrefix(profile);

            return BuildDescriptionPrefix(profile);
        }
    }
}
