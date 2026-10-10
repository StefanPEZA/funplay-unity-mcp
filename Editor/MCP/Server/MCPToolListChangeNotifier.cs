// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Funplay.Editor.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Tracks changes to the exposed MCP tool list across server restarts and domain
    /// reloads, so transports can piggyback a <c>notifications/tools/list_changed</c>
    /// message onto the next client request (as an SSE-formatted response). Connected
    /// clients then refresh their tool list without reconnecting -- previously the only
    /// way for an already-connected client to see newly added or re-exposed tools was
    /// a full client restart.
    /// Threading: transports consume the pending flag from background threads, so the
    /// hot path only touches volatile statics. SessionState (main-thread-only API) is
    /// read/written exclusively on the main thread -- at server start and via an
    /// EditorApplication.update sync -- and is what lets the flag survive domain
    /// reloads within an editor session.
    /// </summary>
    internal static class MCPToolListChangeNotifier
    {
        private const string HashKey = "Funplay.MCP.ExposedToolsHash";
        private const string PendingKey = "Funplay.MCP.ToolsChangedPending";
        private const string ChangedAtKey = "Funplay.MCP.ToolsChangedAt";
        private const string InitializedAtKey = "Funplay.MCP.ClientInitializedAt";
        private const string DeliveredAtKey = "Funplay.MCP.ToolsChangeDeliveredAt";

        internal const string NotificationJson =
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}";

        private static volatile bool _pending;
        private static volatile bool _persistedFlagDirty;
        private static bool _updateHooked;
        private static volatile string _currentHash;
        // UTC ticks; 0 = unknown. Written from transport threads, mirrored to SessionState on the main thread.
        private static long _changedAt, _initializedAt, _deliveredAt;

        /// <summary>
        /// Compare the currently exposed tool list (names, descriptions, input schemas and
        /// annotations) against the last observed one. Called on the main thread when the
        /// server (re)starts with a freshly built exporter. The last hash is also kept in
        /// Library/FunplayMcp/tools-hash.txt, so a package update across an Editor restart
        /// notifies too; only the very first observation in a project records a baseline.
        /// </summary>
        public static void CheckForChanges(MCPToolExporter toolExporter)
        {
            try
            {
                var hash = ComputeToolListHash(toolExporter);
                var previous = SessionState.GetString(HashKey, null);
                if (string.IsNullOrEmpty(previous))
                    previous = ReadPersistedHash();
                var pending = SessionState.GetBool(PendingKey, false);
                _currentHash = hash;
                RestoreTicks(ref _changedAt, ChangedAtKey);
                RestoreTicks(ref _initializedAt, InitializedAtKey);
                RestoreTicks(ref _deliveredAt, DeliveredAtKey);

                if (string.IsNullOrEmpty(previous))
                {
                    SessionState.SetString(HashKey, hash);
                    WritePersistedHash(hash);
                }
                else if (!string.Equals(previous, hash, StringComparison.Ordinal))
                {
                    SessionState.SetString(HashKey, hash);
                    WritePersistedHash(hash);
                    SessionState.SetBool(PendingKey, true);
                    pending = true;
                    Interlocked.Exchange(ref _changedAt, DateTime.UtcNow.Ticks);
                    PersistTicks(_changedAt, ChangedAtKey);
                    PluginDebugLogger.Log(
                        "[Funplay MCP Server] Exposed tool list changed; clients will be notified via tools/list_changed.");
                }

                _pending = pending;
                _persistedFlagDirty = false;

                if (!_updateHooked)
                {
                    _updateHooked = true;
                    EditorApplication.update += SyncPersistedFlag;
                }
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] Tool list change check failed: " + ex.Message);
            }
        }

        /// <summary>Consume the pending flag. Thread-safe; returns true at most once per change.</summary>
        public static bool TryConsumePending()
        {
            if (!_pending)
                return false;

            lock (typeof(MCPToolListChangeNotifier))
            {
                if (!_pending)
                    return false;
                _pending = false;
            }

            Interlocked.Exchange(ref _deliveredAt, DateTime.UtcNow.Ticks);
            _persistedFlagDirty = true;
            return true;
        }

        /// <summary>A client sent initialize; it fetches fresh schemas from here on. Thread-safe.</summary>
        public static void NoteClientInitialized()
        {
            Interlocked.Exchange(ref _initializedAt, DateTime.UtcNow.Ticks);
            _persistedFlagDirty = true;
        }

        /// <summary>
        /// Whether a client may still hold old input schemas. Some clients (Claude Code among them)
        /// add new tools on tools/list_changed but keep the schemas of tools they already knew until
        /// they reconnect, so a change after the last initialize is reported as possibly stale.
        /// </summary>
        public static object SchemaStatus() => SchemaStatus(_currentHash, Interlocked.Read(ref _changedAt),
            Interlocked.Read(ref _initializedAt), Interlocked.Read(ref _deliveredAt));

        internal static object SchemaStatus(string hash, long changedAt, long initializedAt, long deliveredAt)
        {
            var stale = changedAt > 0 && changedAt > initializedAt;
            return new
            {
                schema_hash = hash,
                changed_at = Iso(changedAt),
                client_initialized_at = Iso(initializedAt),
                list_changed_delivered_at = Iso(deliveredAt),
                schemas_may_be_stale = stale,
                hint = stale
                    ? "The exposed tool schemas changed after the last MCP initialize. Some clients keep the old input schemas of tools they already knew until they reconnect, and reject newly documented parameters. Ask the user to reconnect the Unity MCP server (Claude Code: /mcp, then Reconnect)."
                    : null
            };
        }

        private static string Iso(long ticks) =>
            ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : null;

        private static void RestoreTicks(ref long field, string key)
        {
            if (Interlocked.Read(ref field) == 0 &&
                long.TryParse(SessionState.GetString(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                Interlocked.CompareExchange(ref field, ticks, 0);
        }

        private static void PersistTicks(long ticks, string key)
        {
            if (ticks > 0) SessionState.SetString(key, ticks.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Flag a change that happened outside a server restart (a tool enabled/disabled at runtime,
        /// a manual registration after startup). Thread-safe.
        /// </summary>
        public static void MarkChanged()
        {
            Interlocked.Exchange(ref _changedAt, DateTime.UtcNow.Ticks);
            _pending = true;
            _persistedFlagDirty = true;
        }

        /// <summary>Re-arm the pending flag when a piggybacked send failed before reaching the client. Thread-safe.</summary>
        public static void RestorePending()
        {
            _pending = true;
            _persistedFlagDirty = true;
        }

        /// <summary>
        /// Wrap a JSON-RPC response as an SSE body that first delivers the
        /// tools/list_changed notification, then the response itself.
        /// </summary>
        public static string BuildSseBody(string responseJson)
        {
            return "data: " + NotificationJson + "\n\n" +
                   "data: " + (responseJson ?? string.Empty) + "\n\n";
        }

        /// <summary>Main-thread pump that mirrors the volatile flag into SessionState.</summary>
        private static void SyncPersistedFlag()
        {
            if (!_persistedFlagDirty)
                return;

            _persistedFlagDirty = false;
            SessionState.SetBool(PendingKey, _pending);
            PersistTicks(Interlocked.Read(ref _changedAt), ChangedAtKey);
            PersistTicks(Interlocked.Read(ref _initializedAt), InitializedAtKey);
            PersistTicks(Interlocked.Read(ref _deliveredAt), DeliveredAtKey);
        }

        internal static string ComputeToolListHash(MCPToolExporter toolExporter)
        {
            return ComputeToolListHash(toolExporter.ExportTools());
        }

        /// <summary>SHA1 of the canonical JSON (tools sorted by name, object keys sorted) of the exported list.</summary>
        internal static string ComputeToolListHash(IEnumerable<Dictionary<string, object>> tools)
        {
            var array = new JArray();
            foreach (var tool in tools.OrderBy(t => t.TryGetValue("name", out var n) ? n as string : null, StringComparer.Ordinal))
                array.Add(Canonicalize(JToken.FromObject(tool)));

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(array.ToString(Formatting.None)));
                return Convert.ToBase64String(bytes);
            }
        }

        private static JToken Canonicalize(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted[property.Name] = Canonicalize(property.Value);
                return sorted;
            }

            if (token is JArray arr)
            {
                var copy = new JArray();
                foreach (var item in arr)
                    copy.Add(Canonicalize(item));
                return copy;
            }

            return token;
        }

        private static string HashFilePath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "FunplayMcp", "tools-hash.txt");

        private static string ReadPersistedHash()
        {
            try
            {
                var path = HashFilePath;
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WritePersistedHash(string hash)
        {
            try
            {
                var path = HashFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, hash);
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] Could not persist the tool list hash: " + ex.Message);
            }
        }
    }
}
