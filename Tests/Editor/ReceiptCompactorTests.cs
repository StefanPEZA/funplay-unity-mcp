// Copyright (C) Funplay. Licensed under MIT.
using System.Linq;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ReceiptCompactorTests
    {
        private const string OpId = "0123456789abcdef0123456789abcdef";

        private static JObject EditorReceipt(string status, string errorCode = null)
        {
            var op = new JObject
            {
                ["operation_id"] = OpId, ["request_key"] = "k1", ["editor_session"] = "s", ["target"] = "play", ["status"] = status, ["phase"] = status == "ready" ? "ready" : "refreshing",
                ["created_at"] = "2026-10-11T10:00:00Z", ["updated_at"] = "2026-10-11T10:00:05Z", ["finished_at"] = "2026-10-11T10:00:05Z",
                ["error_code"] = errorCode, ["message"] = status == "ready" ? "Editor is ready." : "Refresh failed.", ["refresh_assets"] = true, ["timeout_seconds"] = 120, ["step"] = 3,
                ["created_seconds"] = 12.5, ["deadline_seconds"] = 132.5, ["settle_since"] = 0.0, ["compilation_errors"] = new JArray(),
                ["history"] = new JArray(new JObject { ["timestamp"] = "t", ["phase"] = "queued", ["message"] = "Queued." }, new JObject { ["timestamp"] = "t", ["phase"] = "ready", ["message"] = "Done." })
            };
            return JObject.FromObject(Response.Success("Inspect operation.status; this envelope only acknowledges the query.", new
            {
                operation = op,
                current_editor = new { is_compiling = false, is_updating = false, is_playing = true, is_changing_play_mode = false, compilation_failed = false, refresh_pending = false, refresh_error = (string)null, ready = true },
                journal_path = "C:/p/Library/FunplayMcp/journal.json", poll_after_ms = 1000, journal_error = (string)null
            }));
        }

        private static JObject Observe(string status, string errorCode = null, string after = null)
            => TaskStatusService.Observe("editor", EditorReceipt(status, errorCode), after);

        [Test]
        public void ReadyEditorReceiptIsSmallAndKeepsTheDecisionFields()
        {
            var json = Observe("ready");
            var text = ResponsePruner.Prune("get_task", json.ToString(Newtonsoft.Json.Formatting.None), "compact");
            Assert.Less(text.Length, 360, text);
            var data = JObject.Parse(text)["data"];
            Assert.AreEqual("editor:" + OpId, (string)data["task"]["task_id"]);
            Assert.AreEqual("ready", (string)data["task"]["status"]);
            Assert.AreEqual("k1", (string)data["operation"]["request_key"]);
            Assert.AreEqual("ready", (string)data["operation"]["status"]);
            Assert.IsTrue((bool)data["current_editor"]["ready"]);
            Assert.AreEqual(ReceiptCompactor.RevisionLength, ((string)data["task"]["revision"]).Length);
            Assert.IsNull(data["operation"]["history"]);
            Assert.IsNull(data["journal_path"]);
        }

        [Test]
        public void FailedEditorReceiptKeepsHistoryAndTheErrorCode()
        {
            var data = Observe("failed", "COMPILATION_FAILED")["data"];
            Assert.AreEqual("COMPILATION_FAILED", (string)data["operation"]["error_code"]);
            Assert.AreEqual(2, ((JArray)data["operation"]["history"]).Count);
            Assert.IsFalse((string)data["operation"]["message"] == null);
        }

        [Test]
        public void ARevisionOfEitherLengthMatchesAndAnUnchangedPollReturnsOnlyTheTaskBlock()
        {
            var first = Observe("ready");
            var short16 = (string)first["data"]["task"]["revision"];
            Assert.IsTrue(ReceiptCompactor.SameRevision(short16 + "ffffffffffffffffffffffffffffffffffffffffffffffff", short16));
            var again = Observe("ready", after: short16);
            Assert.AreEqual(new[] { "task" }, ((JObject)again["data"]).Properties().Select(p => p.Name).ToArray());
            Assert.IsFalse((bool)again["data"]["task"]["changed"], "unchanged is reported as changed=false");
        }

        [Test]
        public void FullDetailLeavesTheReceiptAsBefore()
        {
            var full = TaskStatusService.Describe("editor", EditorReceipt("ready"));
            Assert.IsNotNull(full["data"]["operation"]["history"]);
            Assert.AreEqual(64, ((string)full["data"]["task"]["revision"]).Length);
        }
    }
}
