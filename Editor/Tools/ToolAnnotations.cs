// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Funplay.Editor.Api.Models;

namespace Funplay.Editor.Tools
{
    /// <summary>
    /// MCP tool annotations (protocol 2025-03-26+): hints clients use to decide which calls need
    /// approval. Read-only comes from <see cref="ReadOnlyToolAttribute"/>; the remaining hints are
    /// listed here so every write tool is classified deliberately (a test enumerates the registry).
    /// The spec defaults destructive and open-world to true, so false is always emitted explicitly.
    /// </summary>
    internal static class ToolAnnotations
    {
        /// <summary>Write tools that may delete, overwrite or discard data, or run arbitrary code.</summary>
        internal static readonly HashSet<string> Destructive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "delete_asset", "delete_game_object", "remove_tag", "remove_package", "unpack_prefab", "bake_lightmaps",
            "install_package", "undo", "redo", "execute_code", "replay_execute_code", "write_file", "edit_script",
            "patch_script", "rename_asset", "set_asset_import_settings", "save_scene", "save_all_scenes",
            "save_prefab_stage", "create_new_scene", "unload_scene", "open_scene", "clear_execute_code_history",
            "extract_recording_frames"
        };

        /// <summary>Write tools that only add or change state in a recoverable way (Undo, re-run, or new files).</summary>
        internal static readonly HashSet<string> NonDestructive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add_component", "add_component_to_many", "add_layer", "add_tag", "assign_animator", "assign_material",
            "cancel_editor_operation", "cancel_task", "cancel_test_run", "cancel_ui_audit", "capture_editor_window", "capture_game_view",
            "capture_multiview", "capture_scene_view", "capture_simulator_view", "capture_ui_toolkit_element", "click_ui_toolkit_element",
            "close_prefab_stage", "configure_ui_defaults", "configure_ui_toolkit_panel", "copy_asset", "copy_component",
            "create_animation_clip", "create_animator_controller", "create_button", "create_canvas", "create_game_object",
            "create_image", "create_material", "create_panel_settings", "create_prefab", "create_primitive",
            "create_project_ui", "create_script", "create_scriptable_object", "create_text", "create_ui_toolkit_panel",
            "director_evaluate", "duplicate_game_object", "end_ui_preview_session", "enter_play_mode", "execute_menu_item",
            "exit_play_mode", "focus_on_object", "frame_debugger_disable", "frame_debugger_enable", "get_console_logs",
            "instantiate_prefab", "load_scene_additive", "log_message", "mark_recording", "memory_open_snapshot_in_profiler",
            "memory_take_full_snapshot", "memory_take_snapshot", "open_prefab_stage", "particle_control",
            "paste_component_values", "play_animator_state", "prepare_editor", "preview_ui_toolkit", "profiler_start", "profiler_stop",
            "record_game_view", "rename_game_object", "request_recompile", "run_tests", "scroll_ui_toolkit",
            "select_object", "set_active", "set_active_tool", "set_animator_parameter", "set_camera_culling_mask",
            "set_camera_projection", "set_camera_settings", "set_component_properties", "set_component_property",
            "set_game_view_size", "set_lighting_settings", "set_material_property", "set_parent", "set_prefab_properties", "set_prefab_property",
            "set_scriptable_object_properties", "set_selection", "set_tag_and_layer", "set_time_scale", "set_transform",
            "show_dialog", "simulate_key_combo", "simulate_key_press", "simulate_mouse_click", "simulate_mouse_drag",
            "simulate_ui_scroll", "start_ui_preview_session", "wait_for_compilation"
        };

        /// <summary>Write tools where repeating the same call has no additional effect.</summary>
        internal static readonly HashSet<string> Idempotent = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "assign_animator", "assign_material", "cancel_editor_operation", "cancel_task", "cancel_test_run", "cancel_ui_audit",
            "close_prefab_stage", "configure_ui_defaults", "configure_ui_toolkit_panel", "delete_asset",
            "delete_game_object", "enter_play_mode", "exit_play_mode", "focus_on_object", "frame_debugger_disable",
            "frame_debugger_enable", "open_prefab_stage", "open_scene", "prepare_editor", "remove_tag", "save_all_scenes",
            "save_prefab_stage", "save_scene", "select_object", "set_active", "set_active_tool", "set_animator_parameter",
            "set_camera_culling_mask", "set_camera_projection", "set_camera_settings", "set_component_properties",
            "set_component_property", "set_game_view_size", "set_lighting_settings", "set_material_property", "set_parent",
            "set_prefab_properties", "set_prefab_property", "set_scriptable_object_properties", "set_selection",
            "set_tag_and_layer", "set_time_scale", "set_transform", "set_asset_import_settings", "write_file"
        };

        /// <summary>Tools that reach outside the Editor and project (package registries, arbitrary code).</summary>
        internal static readonly HashSet<string> OpenWorld = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "install_package", "remove_package", "execute_code", "replay_execute_code"
        };

        public static ToolAnnotationsDef ForMethod(string toolName, MethodInfo method)
        {
            var readOnly = method != null && ToolRegistry.IsReadOnly(method);
            return new ToolAnnotationsDef
            {
                title = BuildTitle(toolName),
                readOnlyHint = readOnly,
                destructiveHint = !readOnly && Destructive.Contains(toolName),
                idempotentHint = readOnly || Idempotent.Contains(toolName),
                openWorldHint = OpenWorld.Contains(toolName)
            };
        }

        /// <summary>True when the tool has a deliberate classification (read-only, or listed as destructive or not).</summary>
        public static bool IsClassified(string toolName, MethodInfo method)
        {
            if (method != null && ToolRegistry.IsReadOnly(method))
                return !Destructive.Contains(toolName) && !NonDestructive.Contains(toolName);

            return Destructive.Contains(toolName) ^ NonDestructive.Contains(toolName);
        }

        private static readonly Dictionary<string, string> TitleWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ui"] = "UI", ["uitk"] = "UITK", ["id"] = "ID", ["2d"] = "2D"
        };

        public static string BuildTitle(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
                return toolName;

            var builder = new StringBuilder();
            foreach (var part in toolName.Split('_'))
            {
                if (part.Length == 0)
                    continue;
                if (builder.Length > 0)
                    builder.Append(' ');
                if (TitleWords.TryGetValue(part, out var word))
                    builder.Append(word);
                else
                    builder.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
            }
            return builder.ToString();
        }
    }
}
