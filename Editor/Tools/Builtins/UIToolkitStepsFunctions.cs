// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Api;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        internal const int MaxSteps = 30, MaxStepsSeconds = 50, MaxSleepMs = 5000;

        [Description("Run a short sequence of UI Toolkit actions in one call, stopping at the first failure (one round trip instead of one per step). " +
                     "steps is a JSON array of objects with op and the same match fields as the single tools (target, selector, text, element_path): " +
                     "click {button, allow_covered, scroll_into_view}, wait {state, timeout_seconds, settle_frames}, scroll {delta_x, delta_y}, " +
                     "set_value {value | type_text, submit}, sleep {ms, up to 5000}. Max 30 steps and 50 s in total. " +
                     "Returns {completed, total} and, on failure, failed {index, op, code, detail} plus the failing step's own data (e.g. details with near misses): later steps did not run and nothing is retried. Needs Play Mode for clicks.")]
        public static async Task<object> RunUiSteps(
            [ToolParam("JSON array, e.g. [{\"op\":\"click\",\"selector\":\"#play\"},{\"op\":\"wait\",\"selector\":\"#menu\",\"state\":\"displayed\"}]")] string steps)
        {
            JArray list;
            try { list = JsonParse.ParseArray(steps); }
            catch (Exception ex) { return Response.Error("INVALID_STEPS", new { detail = ex.Message }); }
            if (list.Count < 1 || list.Count > MaxSteps) return Response.Error("INVALID_STEPS", new { detail = "Pass 1.." + MaxSteps + " steps." });

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < list.Count; i++)
            {
                var step = list[i] as JObject;
                var op = (string)step?["op"];
                JObject reply;
                if (step == null || op == null) return StepFailed(list.Count, i, op, "INVALID_STEP", "Each step is an object with an op.");
                if (clock.Elapsed.TotalSeconds > MaxStepsSeconds) return StepFailed(list.Count, i, op, "STEPS_TIME_BUDGET", "The 50 s budget ran out before this step.");
                try { reply = JObject.FromObject(await RunStep(op, step)); }
                catch (Exception ex) { return StepFailed(list.Count, i, op, "STEP_FAILED", ex.Message); }
                if (reply["success"] != null && !(bool)reply["success"])
                {
                    // The step's own data (detail, near-miss details, last observed state) stays an object, not an escaped string.
                    var rest = (reply["data"] as JObject)?.DeepClone() as JObject;
                    var detail = (string)rest?["detail"] ?? (string)reply["error"];
                    rest?.Remove("detail");
                    foreach (var empty in rest?.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList() ?? new List<JProperty>())
                        empty.Remove();
                    return StepFailed(list.Count, i, op, (string)reply["code"] ?? "STEP_FAILED", detail, rest != null && rest.HasValues ? rest : null);
                }
            }
            return Response.Success($"Ran {list.Count} steps.", new { completed = list.Count, total = list.Count });
        }

        private static object StepFailed(int total, int index, string op, string code, string detail, JObject data = null)
        {
            var failed = new JObject { ["index"] = index, ["op"] = op, ["code"] = code, ["detail"] = detail };
            if (data != null) failed.Merge(data);
            return Response.Error("STEP_FAILED", new { completed = index, total, failed });
        }

        private static string Str(JObject step, string name) => (string)step[name];
        private static T Val<T>(JObject step, string name, T fallback) => step[name] == null || step[name].Type == JTokenType.Null ? fallback : step[name].ToObject<T>();

        private static async Task<object> RunStep(string op, JObject s)
        {
            string target = Str(s, "target"), selector = Str(s, "selector"), text = Str(s, "text"), path = Str(s, "element_path");
            switch (op)
            {
                case "click":
                    return ClickUiToolkitElement(target, selector, text, path, Val(s, "button", "left"), Val(s, "allow_covered", false), Val(s, "scroll_into_view", false));
                case "wait":
                    return await WaitForUiToolkit(target, selector, text, path, Val(s, "state", "displayed"), Val(s, "timeout_seconds", 5f), Val(s, "settle_frames", 3));
                case "scroll":
                    return ScrollUiToolkit(target, selector, text, path, Val(s, "delta_x", 0f), Val(s, "delta_y", 0f));
                case "set_value":
                    return SetUiToolkitValue(target, selector, text, path, Str(s, "value"), Str(s, "type_text"), Val(s, "submit", false));
                case "sleep":
                    await SleepAsync(Math.Max(0, Math.Min(MaxSleepMs, Val(s, "ms", 100))));
                    return Response.Success("slept");
                default:
                    return Response.Error("INVALID_STEP", new { detail = "Unknown op '" + op + "'. Use click, wait, scroll, set_value or sleep." });
            }
        }

        /// <summary>Waits on Editor updates, not a thread timer, so the continuation stays on the Editor thread.</summary>
        private static Task SleepAsync(int ms)
        {
            var done = new TaskCompletionSource<bool>();
            var clock = Stopwatch.StartNew();
            void Tick()
            {
                if (clock.ElapsedMilliseconds < ms) return;
                EditorApplication.update -= Tick;
                done.TrySetResult(true);
            }
            EditorApplication.update += Tick;
            return done.Task;
        }
    }
}
