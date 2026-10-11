// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class MCPLongWaitTests
    {
        private static MCPRequest GetTask(int wait, object token = null, string tool = "get_task")
        {
            var request = new MCPRequest
            {
                Id = 7, Method = "tools/call",
                Params = new Dictionary<string, object>
                {
                    ["name"] = tool,
                    ["arguments"] = new Dictionary<string, object> { ["task_id"] = "editor:x", ["wait_seconds"] = wait }
                }
            };
            if (token != null) request.Params["_meta"] = new Dictionary<string, object> { ["progressToken"] = token };
            return request;
        }

        private static MCPResponse Reply(string reason)
            => new MCPResponse
            {
                Id = 7,
                Result = new Dictionary<string, object>
                {
                    ["content"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = "{\"success\":true,\"data\":{\"task\":{\"wait_reason\":\"" + reason + "\"}}}" }
                    }
                }
            };

        [Test]
        public void OnlyLongGetTaskWaitsWithAProgressTokenAndSseAreServedAsAStream()
        {
            Assert.IsTrue(MCPLongWait.TryPlan(GetTask(100, "tok"), true, out var token, out var seconds));
            Assert.AreEqual("tok", token);
            Assert.AreEqual(100, seconds);
            Assert.IsTrue(MCPLongWait.TryPlan(GetTask(500, 3), true, out _, out seconds));
            Assert.AreEqual(MCPLongWait.MaxSeconds, seconds, "capped at 120");
            Assert.IsFalse(MCPLongWait.TryPlan(GetTask(55, "tok"), true, out _, out _), "55 s fits one call");
            Assert.IsFalse(MCPLongWait.TryPlan(GetTask(100), true, out _, out _), "no progress token");
            Assert.IsFalse(MCPLongWait.TryPlan(GetTask(100, "tok"), false, out _, out _), "client did not accept an event stream");
            Assert.IsFalse(MCPLongWait.TryPlan(GetTask(100, "tok", "prepare_editor"), true, out _, out _), "only read-only get_task is re-issued");
        }

        [Test]
        public void SlicesShortenOnlyTheWaitAndLeaveTheOriginalAlone()
        {
            var original = GetTask(100, "tok");
            var slice = MCPLongWait.Slice(original, 55);
            Assert.AreEqual(55, ((Dictionary<string, object>)slice.Params["arguments"])["wait_seconds"]);
            Assert.AreEqual("editor:x", ((Dictionary<string, object>)slice.Params["arguments"])["task_id"]);
            Assert.AreEqual(100, ((Dictionary<string, object>)original.Params["arguments"])["wait_seconds"]);
        }

        [Test]
        public void OnlyARunningTimeoutKeepsWaiting()
        {
            Assert.IsFalse(MCPLongWait.IsFinal(Reply("timeout")));
            Assert.IsTrue(MCPLongWait.IsFinal(Reply("completed")));
            Assert.IsTrue(MCPLongWait.IsFinal(Reply("changed")));
            Assert.IsTrue(MCPLongWait.IsFinal(new MCPResponse { Error = new MCPError { Code = -1, Message = "x" } }));
            Assert.IsTrue(MCPLongWait.IsFinal(new MCPResponse { Result = new Dictionary<string, object> { ["content"] = "not a list" } }));
        }

        [Test]
        public void ProgressEventNamesTheTokenAndIsValidJson()
        {
            var text = MCPLongWait.ProgressEvent("tok", 15, 100);
            StringAssert.StartsWith("event: message\ndata: ", text);
            var json = JObject.Parse(text.Substring("event: message\ndata: ".Length));
            Assert.AreEqual("notifications/progress", (string)json["method"]);
            Assert.AreEqual("tok", (string)json["params"]["progressToken"]);
            Assert.AreEqual(15, (int)json["params"]["progress"]);
        }

        [UnityTest]
        public IEnumerator TransportStreamsHeartbeatsThenTheFinalReplyAcrossSlices()
        {
            var oldSlice = MCPLongWait.SliceSeconds;
            var oldBeat = MCPLongWait.HeartbeatSeconds;
            MCPLongWait.SliceSeconds = 1; MCPLongWait.HeartbeatSeconds = 0.3;
            var port = GetFreePort();
            var transport = new HttpMCPTransport(port, "Funplay MCP Server - Test", "long-wait");
            var slices = new List<int>();
            transport.OnRequestReceived += (request, send) =>
            {
                var wait = Convert.ToInt32(((Dictionary<string, object>)request.Params["arguments"])["wait_seconds"]);
                lock (slices) slices.Add(wait);
                var index = slices.Count;
                // Two slices time out after ~0.7 s each, the third completes.
                Task.Delay(700).ContinueWith(_ => send(Reply(index < 3 ? "timeout" : "completed")));
            };
            try
            {
                var start = transport.StartAsync();
                while (!start.IsCompleted) yield return null;
                Assert.IsTrue(start.Result);

                var body = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"get_task\",\"_meta\":{\"progressToken\":\"tok\"},\"arguments\":{\"task_id\":\"editor:x\",\"wait_seconds\":3}}}";
                var read = Task.Run(() => Post(port, body));
                while (!read.IsCompleted) yield return null;
                var response = read.Result;

                StringAssert.Contains("text/event-stream", response);
                var progress = response.Split(new[] { "notifications/progress" }, StringSplitOptions.None).Length - 1;
                Assert.GreaterOrEqual(progress, 2, "heartbeats arrive while a slice is pending: " + response);
                StringAssert.Contains("completed", response);
                Assert.IsTrue(response.IndexOf("notifications/progress", StringComparison.Ordinal) < response.IndexOf("completed", StringComparison.Ordinal), "progress comes before the final reply");
                Assert.AreEqual(3, slices.Count, "each slice is a fresh read-only get_task");
                Assert.IsTrue(slices.TrueForAll(w => w >= 1 && w <= 3));
            }
            finally
            {
                transport.Dispose();
                MCPLongWait.SliceSeconds = oldSlice; MCPLongWait.HeartbeatSeconds = oldBeat;
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [UnityTest]
        public IEnumerator OAuthDiscoveryPathsAnswer404InsteadOfAJsonRpcReply()
        {
            var port = GetFreePort();
            var transport = new HttpMCPTransport(port, "Funplay MCP Server - Test", "oauth-404");
            var called = false;
            transport.OnRequestReceived += (request, send) => { called = true; send(Reply("completed")); };
            try
            {
                var start = transport.StartAsync();
                while (!start.IsCompleted) yield return null;
                Assert.IsTrue(start.Result);

                var body = "{\"redirect_uris\":[\"http://localhost/cb\"]}";
                foreach (var path in new[] { "/register", "/token", "/.well-known/oauth-authorization-server" })
                {
                    var read = Task.Run(() => Post(port, body, path));
                    while (!read.IsCompleted) yield return null;
                    StringAssert.StartsWith("HTTP/1.1 404", read.Result, path);
                }
                Assert.IsFalse(called, "OAuth paths never reach the JSON-RPC handler");
            }
            finally
            {
                transport.Dispose();
            }
        }

        private static string Post(int port, string body, string path = "/")
        {
            using (var client = new TcpClient("127.0.0.1", port))
            {
                var stream = client.GetStream();
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = "POST " + path + " HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: " + bytes.Length + "\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.ReadTimeout = 15000;
                var sb = new StringBuilder();
                var buffer = new byte[4096];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                return sb.ToString();
            }
        }
    }
}
