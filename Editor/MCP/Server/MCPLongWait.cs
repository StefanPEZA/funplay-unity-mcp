// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Waits for a task longer than one tool call may last (55 s), on the direct HTTP transport only. A
    /// <c>get_task</c> with <c>wait_seconds</c> 56..120 from a client that sent a progress token is answered as an
    /// SSE stream: the wait runs as read-only 55 s slices, and a <c>notifications/progress</c> goes out every
    /// <see cref="HeartbeatSeconds"/> so the client keeps waiting. Without a token the plain 55 s cap applies.
    /// </summary>
    internal static class MCPLongWait
    {
        internal const int MaxSeconds = 120;

        // Fields, not constants, so tests can use a short slice.
        internal static int SliceSeconds = 55;
        internal static double HeartbeatSeconds = 15;

        /// <summary>True when <paramref name="request"/> is a long get_task wait the stream can serve.</summary>
        internal static bool TryPlan(MCPRequest request, bool acceptsEventStream, out object progressToken, out int seconds)
        {
            progressToken = null; seconds = 0;
            if (!acceptsEventStream || request?.Params == null || request.Method != "tools/call") return false;
            if (!request.Params.TryGetValue("name", out var name) || !string.Equals(name as string, "get_task", StringComparison.Ordinal)) return false;
            if (!request.Params.TryGetValue("arguments", out var args) || !(args is Dictionary<string, object> arguments)) return false;
            if (!arguments.TryGetValue("wait_seconds", out var wait) ||
                !int.TryParse(Convert.ToString(wait, CultureInfo.InvariantCulture), out seconds) || seconds <= SliceSeconds) return false;
            seconds = Math.Min(seconds, MaxSeconds);
            if (!request.Params.TryGetValue("_meta", out var meta)) return false;
            progressToken = meta is Dictionary<string, object> dictionary ? (dictionary.TryGetValue("progressToken", out var t) ? t : null)
                : meta is JObject json ? json["progressToken"]?.ToObject<object>() : null;
            return progressToken != null;
        }

        /// <summary>The same request with a shorter wait, so each slice passes the ordinary 55 s validation.</summary>
        internal static MCPRequest Slice(MCPRequest original, int waitSeconds)
        {
            var arguments = new Dictionary<string, object>((Dictionary<string, object>)original.Params["arguments"]) { ["wait_seconds"] = waitSeconds };
            var parameters = new Dictionary<string, object>(original.Params) { ["arguments"] = arguments };
            return new MCPRequest { JsonRpc = original.JsonRpc, Id = original.Id, Method = original.Method, Params = parameters };
        }

        /// <summary>
        /// Whether a slice's reply ends the wait: anything but a still-running timeout. A reply that cannot be read
        /// ends it too, so a malformed answer is returned to the caller instead of being retried.
        /// </summary>
        internal static bool IsFinal(MCPResponse response)
        {
            if (response == null || response.Error != null) return true;
            try
            {
                var content = (response.Result as Dictionary<string, object>)?["content"] as List<Dictionary<string, object>>;
                var text = content != null && content.Count > 0 ? content[0]["text"] as string : null;
                var task = text == null ? null : JObject.Parse(text)["data"]?["task"];
                return task == null || (string)task["wait_reason"] != "timeout";
            }
            catch (Exception) { return true; }
        }

        internal static string ProgressEvent(object token, int elapsedSeconds, int totalSeconds)
            => "event: message\ndata: " + JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new Dictionary<string, object> { ["progressToken"] = token, ["progress"] = elapsedSeconds, ["total"] = totalSeconds, ["message"] = "waiting for the task" }
            }) + "\n\n";

        internal static string FinalEvent(string responseJson) => "event: message\ndata: " + responseJson + "\n\n";

        internal const string StreamHeaders =
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n" +
            "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\n\r\n";
    }
}
