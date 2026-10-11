// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.State;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Tasks")]
    internal static class TaskStatusFunctions
    {
        [Description("Read an async task without starting, cancelling or cleaning it. Use the returned task_id; for a lost editor/preview start response use kind + request_key. MCP waits up to wait_seconds for completion, or a phase/status change after after_revision. Return the latest revision next time and obey poll_after_ms. A preview ready state completes preparation, not session restoration. Original task payloads remain under data.")]
        [ReadOnlyTool]
        public static object GetTask(
            [ToolParam("Namespaced task ID returned in data.task.task_id", Required = false)] string task_id = null,
            [ToolParam("For request-key lookup: editor or ui_preview. Bare IDs additionally support ui_audit, recording, recording_frames, tests.", Required = false)] string kind = null,
            [ToolParam("Original preparation/preview request key", Required = false)] string request_key = null,
            [ToolParam("Last data.task.revision; waits for a meaningful state change, not each progress counter", Required = false)] string after_revision = null,
            [ToolParam("MCP bounded wait, 0..55 seconds for editor/tests tasks (55 is the longest that fits client timeouts), 0..30 otherwise; zero returns immediately. On the direct HTTP transport a client that sends a progress token may use up to 120 for editor/tests (progress is streamed while waiting)", Required = false)] int wait_seconds = 20,
            [ToolParam("UI audit finding offset", Required = false)] int offset = 0,
            [ToolParam("UI audit finding page size, 1..500", Required = false)] int limit = 100)
            => TaskStatusService.ValidateWait(wait_seconds, kind, task_id) ?? TaskStatusService.Query(task_id, kind, request_key, after_revision, offset, limit);

        [Description("Cancel an async task by the task_id get_task uses (editor preparation, UI audit, recording, recording frame extraction, tests). Repeating is safe. Already-started native work (imports, compilation, Play transitions) is not rolled back. UI preview sessions are ended with end_ui_preview_session instead.")]
        public static object CancelTask(
            [ToolParam("Namespaced task ID from data.task.task_id, e.g. 'editor:<id>'")] string task_id,
            [ToolParam("Task kind when task_id is a bare ID: editor, ui_audit, recording, recording_frames or tests", Required = false)] string kind = null)
            => TaskStatusService.Cancel(task_id, kind);
    }
}
