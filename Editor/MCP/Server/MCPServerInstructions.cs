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

- Non-image tool results are JSON envelopes. Success is `{ ""success"": true, ""message"": ""..."", ... }` with an optional payload under `data`; failure is `{ ""success"": false, ""code"": ""..."", ... }`. Branch on `code`, never on human-readable text. Inline screenshots are returned as MCP image content.
- Edit scenes, prefabs, and ScriptableObjects ONLY through these tools / Unity Editor APIs. Never hand-edit .unity/.prefab/.asset files as text while the Editor is open - it overwrites your changes from its in-memory copy.
- To change serialized fields on one prefab component, prefer `set_prefab_property` / `set_prefab_properties`. They avoid Prefab Mode, synchronously reimport the asset, and return persisted readback. Use `open_prefab_stage` only for structural edits; a stage save serializes the full in-memory prefab graph, so review its warning and verify the result afterward.
- Inspect an object before mutating a user-named target; treat user-supplied names as hints, not paths. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Prefer structured tools (`audit_ui`, filtered `find_game_objects`, `inspect_ui_sprites`, `find_project_types`) for common UI inspection. Those and `create_project_ui`/`create_canvas` are uGUI (Canvas) tools; for UI Toolkit (PanelRenderer on Unity 6.6+, UIDocument earlier) use `get_ui_toolkit_info`, `create_ui_toolkit_panel` and `get_visual_tree`; drive it with `click_ui_toolkit_element`, `set_ui_toolkit_value`, `navigate_ui_toolkit` and `pointer_ui_toolkit`, and check what an action changed with `get_visual_tree(snapshot=true)` then `diff_visual_tree`. Use `get_tool_capabilities` to distinguish implemented from exposed tools. `execute_code` is a fallback for project-specific gaps, not the default query mechanism.
- After external changes use `prepare_editor(target=edit|play, request_key=...)`. Short tasks may finish in that call; otherwise query `get_task` with data.task.task_id, wait_seconds and the last after_revision. On a lost response use kind=editor + request_key. Verify operation.status and current_editor.ready; never replay uncertain mutations. Honor poll_after_ms instead of model-driven one-second polling; `wait_seconds` may be up to 55 for editor and test tasks (30 for other kinds). Receipts are compact: `phase` absent = status, `changed` absent = true, an unchanged poll returns only `task`, and a test job id is the part of `task_id` after `tests:`. Historical reload events are not readiness. `prepare_editor(target=play, refresh_assets=true)` is the normal way to pick up edited USS/UXML/scripts and enter Play; `prepare_editor(target=edit)` leaves Play. `enter_play_mode`/`exit_play_mode`, legacy compile readers and low-frequency management remain in Full; custom exposure lists are preserved.
- `get_task` is read-only across editor, ui_audit, ui_preview, recording, recording_frames and tests. It cannot cancel, restore or clean anything, and cannot bypass per-kind exposure; `cancel_task` cancels by the same task_id. wait_complete means waiting has settled, not success or preview restoration; inspect native errors, complete/ready/restoration fields. Recording starts return immediately for interactions. HTTP wait timeout/disconnection does not cancel the underlying job.
- After a package update, some clients add new tools but keep the old input schemas of tools they already knew until they reconnect, and reject newly documented parameters before the call reaches Unity. When that happens, or `get_tool_capabilities` reports `schemas.schemas_may_be_stale`, ask the user to reconnect the Unity MCP server (Claude Code: `/mcp`, then Reconnect) instead of working around it. Arguments a tool does not declare fail with `UNKNOWN_PARAM`, which lists the valid names.
- Replies from receipt and echo tools are compact: null/empty fields and default-false flags (e.g. `is_compiling`, `covered`) are omitted, so absent means false/empty. Captures return `geometry: {capture_id, size}` (plus `render`/`viewport`/`surface` only when different); the full contract is `get_visual_coordinates(capture_id)`. Pass `detail=full` to a tool that declares it for the complete shape.
- `get_console_logs` accepts `group_duplicates=true` to collapse spammy repeated logs and `filter_text` to narrow them.
- Large screenshot results automatically fall back to a project-local file. Prefer `save_to_file` when the client can read local files, and use explicit width/height when only a small inline preview is needed.
- Save only the assets you intentionally changed, then read them back to confirm the exact values.";
    }
}
