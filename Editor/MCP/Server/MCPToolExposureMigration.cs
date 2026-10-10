// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// One-shot migration of tool exposure lists written before 0.8.5. Older versions stored the Full
    /// profile as an explicit allow list, and pressing Save in the exposure window always wrote one, so
    /// every tool added by a later version stayed hidden (TOOL_NOT_EXPOSED). Full is now stored as an
    /// exclusion list. Old lists carry no version, so they are compared against the 0.8.3 tool set.
    /// </summary>
    internal static class MCPToolExposureMigration
    {
        /// <summary>Every tool name registered by 0.8.3 (188 tools).</summary>
        internal static readonly IReadOnlyCollection<string> ToolsV083 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add_component", "add_component_to_many", "add_layer", "add_tag", "analyze_scene_complexity",
            "assign_animator", "assign_material", "audit_ui", "bake_lightmaps", "cancel_editor_operation",
            "cancel_test_run", "cancel_ui_audit", "capture_editor_window", "capture_game_view", "capture_multiview",
            "capture_scene_view", "capture_simulator_view", "clear_execute_code_history", "click_ui_toolkit_element",
            "close_prefab_stage", "configure_ui_defaults", "configure_ui_toolkit_panel", "copy_asset",
            "copy_component", "create_animation_clip", "create_animator_controller", "create_button", "create_canvas",
            "create_game_object", "create_image", "create_material", "create_new_scene", "create_panel_settings",
            "create_prefab", "create_primitive", "create_project_ui", "create_script", "create_scriptable_object",
            "create_text", "create_ui_toolkit_panel", "delete_asset", "delete_game_object", "director_evaluate",
            "duplicate_game_object", "edit_script", "end_ui_preview_session", "enter_play_mode", "execute_code",
            "execute_menu_item", "exists", "exit_play_mode", "extract_recording_frames", "find_assets",
            "find_broken_references", "find_game_objects", "find_project_types", "find_references", "focus_on_object",
            "frame_debugger_disable", "frame_debugger_enable", "frame_debugger_get_events", "get_active_tool",
            "get_animator_state", "get_asset_import_settings", "get_build_settings", "get_camera_properties",
            "get_compilation_errors", "get_component_properties", "get_console_logs", "get_counters",
            "get_editor_operation", "get_editor_state", "get_execute_code_history", "get_frame_timing",
            "get_game_object_info", "get_hierarchy", "get_layers", "get_lighting_settings", "get_material_properties",
            "get_mesh_info", "get_object_memory", "get_object_screen_bounds", "get_performance_snapshot",
            "get_prefab_stage", "get_project_settings", "get_recording_frame", "get_reload_recovery_status",
            "get_scene_info", "get_scriptable_object", "get_selection", "get_tags", "get_task", "get_test_job",
            "get_time_scale", "get_tool_capabilities", "get_top_memory_objects", "get_ui_audit", "get_ui_defaults",
            "get_ui_preview_session", "get_ui_toolkit_info", "get_undo_state", "get_visual_coordinates",
            "get_visual_tree", "get_windows", "inspect_ui_sprites", "install_package", "instantiate_prefab",
            "list_components", "list_directory", "list_dirty_scenes", "list_editor_operations", "list_packages",
            "list_scenes", "load_scene_additive", "log_message", "mark_recording", "memory_compare_snapshots",
            "memory_list_full_snapshots", "memory_list_snapshots", "memory_open_snapshot_in_profiler",
            "memory_query_references", "memory_query_top_objects", "memory_take_full_snapshot", "memory_take_snapshot",
            "open_prefab_stage", "open_scene", "particle_control", "paste_component_values", "patch_script",
            "physics2d_overlap_point", "physics_overlap", "physics_raycast", "pick_ui_toolkit_element", "ping_asset",
            "play_animator_state", "prepare_editor", "profiler_start", "profiler_status", "profiler_stop",
            "raycast_at_point", "read_file", "record_game_view", "redo", "remove_package", "remove_tag",
            "rename_asset", "rename_game_object", "replay_execute_code", "request_recompile", "run_tests",
            "save_all_scenes", "save_prefab_stage", "save_scene", "scroll_ui_toolkit", "search_files", "select_object",
            "set_active", "set_active_tool", "set_animator_parameter", "set_asset_import_settings",
            "set_camera_culling_mask", "set_camera_projection", "set_camera_settings", "set_component_properties",
            "set_component_property", "set_lighting_settings", "set_material_property", "set_parent",
            "set_prefab_properties", "set_prefab_property", "set_scriptable_object_properties", "set_selection",
            "set_tag_and_layer", "set_time_scale", "set_transform", "show_dialog", "simulate_key_combo",
            "simulate_key_press", "simulate_mouse_click", "simulate_mouse_drag", "simulate_ui_scroll",
            "start_ui_preview_session", "undo", "unload_scene", "unpack_prefab", "validate_menu_item",
            "wait_for_compilation", "write_file"
        };

        /// <summary>The built-in Core list of 0.8.3 (45 tools).</summary>
        internal static readonly IReadOnlyCollection<string> CoreToolsV083 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "execute_code", "simulate_key_press", "simulate_key_combo", "simulate_mouse_click", "simulate_mouse_drag",
            "get_scene_info", "get_hierarchy", "get_console_logs", "capture_scene_view", "capture_game_view",
            "record_game_view", "extract_recording_frames", "get_recording_frame", "simulate_ui_scroll",
            "raycast_at_point", "get_compilation_errors", "prepare_editor", "get_task", "cancel_editor_operation",
            "audit_ui", "cancel_ui_audit", "find_project_types", "inspect_ui_sprites", "get_tool_capabilities",
            "get_visual_coordinates", "get_object_screen_bounds", "get_ui_defaults", "create_project_ui",
            "get_ui_toolkit_info", "create_ui_toolkit_panel", "get_visual_tree", "click_ui_toolkit_element",
            "scroll_ui_toolkit", "get_editor_state", "get_selection", "set_selection", "get_prefab_stage",
            "find_game_objects", "list_components", "get_component_properties", "set_component_property",
            "set_component_properties", "set_prefab_property", "set_prefab_properties", "execute_menu_item"
        };

        /// <summary>
        /// Converts a legacy Full allow list into exclusions: the 0.8.3 tools the list switched off. A list
        /// that contains every 0.8.3 tool returns an empty array (= default, everything exposed).
        /// </summary>
        public static string[] LegacyFullAllowListToExclusions(IEnumerable<string> allowList)
        {
            var allowed = new HashSet<string>(allowList ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return ToolsV083
                .Where(tool => !allowed.Contains(tool))
                .OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>True when a legacy custom Core list is exactly the 0.8.3 default, i.e. "default" in disguise.</summary>
        public static bool IsLegacyDefaultCoreList(IEnumerable<string> coreList)
        {
            if (coreList == null)
                return false;

            var set = new HashSet<string>(coreList, StringComparer.OrdinalIgnoreCase);
            return set.SetEquals(CoreToolsV083);
        }
    }
}
