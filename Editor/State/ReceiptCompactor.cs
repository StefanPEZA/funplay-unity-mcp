// Copyright (C) Funplay. Licensed under MIT.
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Compact projection of a task receipt (<c>get_task</c>, <c>prepare_editor</c>, <c>run_tests</c> ...).
    /// A pure function of the 0.8.6 shape: identity in <c>detail=full</c>, and it never removes a follow-up id,
    /// status, error code, compilation error or failure list. Absent means: <c>phase</c> = status,
    /// <c>changed</c> = true, <c>wait_reason</c> = snapshot.
    /// </summary>
    internal static class ReceiptCompactor
    {
        internal const int RevisionLength = 16;

        private static readonly string[] OperationNoise =
        {
            "operation_id", "editor_session", "created_at", "updated_at", "finished_at", "created_seconds", "deadline_seconds",
            "settle_since", "step", "timeout_seconds", "refresh_assets"
        };

        /// <summary>True when a caller-supplied revision (16 or 64 hex chars) names the same observation as <paramref name="current"/>.</summary>
        internal static bool SameRevision(string current, string supplied)
            => !string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(supplied) &&
               (current.StartsWith(supplied, System.StringComparison.Ordinal) || supplied.StartsWith(current, System.StringComparison.Ordinal));

        internal static void Compact(string kind, JObject receipt)
        {
            var data = receipt["data"] as JObject;
            var task = data?["task"] as JObject;
            if (task == null) return;
            var status = (string)task["status"];

            task.Remove("kind"); task.Remove("id"); task.Remove("snapshot_at");
            if ((string)task["phase"] == status) task.Remove("phase");
            if (task["changed"] != null && (bool)task["changed"]) task.Remove("changed");
            if ((string)task["wait_reason"] == "snapshot") task.Remove("wait_reason");
            if ((int?)task["waited_ms"] == 0) task.Remove("waited_ms");
            var revision = (string)task["revision"];
            if (revision != null && revision.Length > RevisionLength) task["revision"] = revision.Substring(0, RevisionLength);

            if (data["operation"] is JObject operation)
            {
                foreach (var name in OperationNoise) operation.Remove(name);
                var failed = status == "failed" || status == "interrupted" || status == "cancelled";
                if (!failed) operation.Remove("history");
                if (!failed && (string)operation["message"] != null && status == "ready") operation.Remove("message");
                if ((string)operation["phase"] == (string)operation["status"]) operation.Remove("phase");
            }
            if (data["current_editor"] is JObject editor && editor["ready"] != null && (bool)editor["ready"])
                data["current_editor"] = new JObject { ["ready"] = true };
            if (data["journal_error"] == null || data["journal_error"].Type == JTokenType.Null) data.Remove("journal_path");
            if (kind == "tests") data.Remove("jobId");
            // The legacy top-level copy of the polling hint (kept equal by AlignPollingHint); task.poll_after_ms is the one to honour.
            if (data["poll_after_ms"] != null && JToken.DeepEquals(data["poll_after_ms"], task["poll_after_ms"])) data.Remove("poll_after_ms");

            var message = (string)receipt["message"];
            if (message != null && message.StartsWith("Inspect operation.status")) receipt["message"] = kind + " " + status;
            if (data["blockingHint"] != null) data["blockingHint"] = "main_thread_blocked";
        }
    }
}
