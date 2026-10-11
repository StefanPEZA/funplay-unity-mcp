// Copyright (C) Funplay. Licensed under MIT.

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Server-level usage guidance returned in the MCP `initialize` result's `instructions`
    /// field. Unlike CLAUDE.md/AGENTS.md (client-specific), this reaches EVERY MCP client and
    /// pins the cross-cutting conventions a model needs to drive this Unity Editor server
    /// correctly. Keep it short, project-agnostic, and about disciplines a fresh client would
    /// otherwise get wrong - not a tool catalogue (that's what tools/list is for).
    /// </summary>
    internal static class MCPServerInstructions
    {
        // Shared with both built-in skills so native clients and generated guides agree.
        internal const string UiAutomationGuidance =
@"- Do not use computer use (desktop mouse/keyboard automation) to operate Unity unless necessary. When assembling, modifying, inspecting or validating UI, prefer Unity MCP whenever it can complete the step, including hierarchy/component/prefab reads and edits, compilation/Play state, clicks/scrolling, screenshots and recordings.
- Check the connected project's tools/list and, when available, `get_tool_capabilities`. A tool missing from exposure, compilation/domain reload or a temporary disconnection is not evidence of a missing capability: check exposure/readiness and recover status first. Respect custom allowlists; do not widen exposure or use another interaction method to bypass restrictions.
- Prefer specialized MCP tools; for project-specific gaps they do not cover, use a permitted, guarded `execute_code` call through Unity Editor APIs when it can perform the step reliably. Computer use is a fallback only for a confirmed MCP capability gap, or an explicit user request: explain the uncovered step before using it, limit it to that step, and return to MCP readback/validation when available. If recovery fails, report the connection blocker rather than silently switching methods or repeating uncertain mutations.
- This routing applies to operating Unity, not ordinary source-file editing or viewing supplied design references and already-captured images/videos with appropriate file or media tools.";

        public const string Text =
@"This server drives the Unity Editor. Core conventions:

" + UiAutomationGuidance + @"

- Non-image tool results are JSON envelopes: success is `{ ""success"": true, ""message"": ""..."", data? }`, failure is `{ ""success"": false, ""code"": ""..."" }`. Branch on `code`, never on message text. Inline screenshots are MCP image content; large ones automatically fall back to a project-local file (prefer `save_to_file` if the client can read files, or pass a small width/height).
- Replies of receipt and echo tools are compact: null/empty fields and default-false flags (e.g. `is_compiling`, `covered`) are omitted, so absent means false/empty. Captures return `geometry: {capture_id, size}` (plus `render`/`viewport`/`surface` only when different); the full contract is `get_visual_coordinates(capture_id)`. Pass `detail=full` to a tool that declares it for the complete shape.
- Edit scenes, prefabs and ScriptableObjects ONLY through these tools / Editor APIs, never by hand-editing .unity/.prefab/.asset text while the Editor is open (it overwrites your edit from memory). For serialized fields on one prefab component use `set_prefab_property` / `set_prefab_properties`: no Prefab Mode, synchronous reimport, persisted readback. Use `open_prefab_stage` only for structural edits; a stage save serializes the whole prefab graph, so review its warning and verify afterwards.
- Inspect an object before mutating a user-named target; names are hints, not paths. Carry the returned `instanceId` into follow-ups (`find_method=by_id`).
- Prefer structured tools (`audit_ui`, filtered `find_game_objects`, `inspect_ui_sprites`, `find_project_types`). Those and `create_project_ui`/`create_canvas` are uGUI tools; for UI Toolkit use `get_ui_toolkit_info`, `create_ui_toolkit_panel`, `get_visual_tree`, and drive it with `click_ui_toolkit_element`, `set_ui_toolkit_value`, `navigate_ui_toolkit`, `pointer_ui_toolkit`; chain several actions in one call with `run_ui_steps` (stops at the first failure); check what an action changed with `get_visual_tree(snapshot=true)` then `diff_visual_tree`. `get_tool_capabilities` shows implemented vs exposed tools (and local call counts). `execute_code` is a fallback for project-specific gaps: write plain statements, `ctx` is in scope, `return expr;` sets the result.
- After external changes use `prepare_editor(target=edit|play, request_key=...)`; `prepare_editor(target=play, refresh_assets=true)` is the way to pick up edited USS/UXML/scripts and enter Play, `target=edit` leaves Play. Short tasks may finish in the call; otherwise `get_task` with data.task.task_id, `wait_seconds` (up to 55 for editor and test tasks, 30 for other kinds) and the last `after_revision`; honor poll_after_ms. On a lost response use kind=editor + request_key. Verify operation.status and current_editor.ready; never replay uncertain mutations; historical reload events are not readiness. Task receipts are compact: `phase` absent = status, `changed` absent = true, an unchanged poll returns only `task`, a test job id is the part of `task_id` after `tests:`. `enter_play_mode`/`exit_play_mode` skip the refresh (enter reports `refresh_needed` when scripts changed).
- `get_task` is read-only across editor, ui_audit, ui_preview, recording, recording_frames and tests; `cancel_task` cancels by the same task_id. wait_complete means waiting settled, not success: inspect status, errors and ready/complete/restoration fields. An HTTP wait timeout or disconnect does not cancel the job.
- After a package update some clients keep old tool schemas and reject new parameters before they reach Unity. If that happens, or `get_tool_capabilities` reports `schemas.schemas_may_be_stale`, ask the user to reconnect the MCP server (Claude Code: `/mcp`, Reconnect). Undeclared arguments fail with `UNKNOWN_PARAM`, which lists the valid names.
- Saving tokens: `capture_game_view(region='x,y,w,h', max_size=N)` crops or downscales (view-only); `compose_contact_sheet(paths)` joins up to 16 saved PNGs; `get_visual_tree`, `get_hierarchy`, `get_scene_info`, `get_component_properties`, `get_console_logs` accept `if_revision` and answer `unchanged` when nothing changed; `get_console_logs` also takes `group_duplicates`, `filter_text` and `after_cursor`.
- Save only the assets you intentionally changed, then read them back to confirm the exact values.";
    }
}
