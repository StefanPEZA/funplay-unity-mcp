// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Funplay.Editor.State
{
    [InitializeOnLoad]
    internal static class EditorOperationService
    {
        private const string SessionKey = "Funplay.EditorOperation.Session";
        private const int JournalIoAttempts = 5;
        internal static readonly string JournalPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library/FunplayMcp/Operations/editor-operations.json");
        private static readonly List<EditorOperation> Operations = new List<EditorOperation>();
        private static EditorOperationCoordinator Coordinator;
        private static string session;
        // True while the journal could not be read (e.g. locked); every entry point retries the read.
        private static bool journalReadPending;
        private static Task<EditorRefreshResult> refreshTask;
        private static string refreshError;
        private static bool recoveredRefresh;
        private static bool journalErrorFromSave;
        private const double RecoveredCheckIntervalSeconds = .5, RecoveredPendingGraceSeconds = 3;
        private static double recoveredCheckedAt, recoveredPendingSince;
        internal static string JournalError { get; private set; }
        internal static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        internal static bool HasActive => Coordinator.Active != null;
        internal static string ActiveId => Coordinator.Active?.operation_id;

        static EditorOperationService()
        {
            session = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(session)) { session = Guid.NewGuid().ToString("N"); SessionState.SetString(SessionKey, session); }
            LoadJournal();
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => Coordinator.Reload(Now);
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
                Coordinator.CompilerErrors(messages.Where(x => x.type == CompilerMessageType.Error)
                    .Select(x => $"{x.file}({x.line},{x.column}): {x.message}"), Now);
        }

        /// <summary>
        /// Reads the journal into <see cref="Operations"/> and rebuilds the coordinator (which resumes an
        /// active operation). A locked file leaves the read pending, retried lazily by the next call,
        /// instead of disabling operations for the whole domain; a corrupt journal is moved aside.
        /// </summary>
        private static void LoadJournal()
        {
            List<EditorOperation> loaded = null;
            journalReadPending = false;
            try
            {
                if (File.Exists(JournalPath))
                {
                    var json = RetryJournalIo(_ => File.ReadAllText(JournalPath));
                    loaded = ParseJournal(json, out var corruption);
                    if (corruption != null)
                    {
                        var aside = MoveCorruptJournalAside(JournalPath);
                        Debug.LogWarning($"[Funplay] The editor operation journal was unreadable ({corruption}) and was moved to '{aside}'. Earlier operation IDs are no longer available; nothing is replayed.");
                        loaded = null;
                    }
                }
                JournalError = null;
                journalErrorFromSave = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                journalReadPending = true;
                JournalError = "Cannot read operation journal yet (it will be retried on the next call): " + ex.Message;
            }

            Operations.Clear();
            if (loaded != null) Operations.AddRange(loaded);
            try { Coordinator = new EditorOperationCoordinator(Operations, session, Save, Now); }
            catch (Exception ex)
            {
                // Keep diagnostics callable even when a restarted operation cannot be persisted.
                Operations.Clear(); JournalError = "Cannot recover operation journal: " + ex.Message;
                Coordinator = new EditorOperationCoordinator(Operations, session, Save, Now);
            }
            recoveredRefresh = Coordinator.Active?.step == 2 && Coordinator.Active.refresh_assets;
        }

        /// <summary>Parses journal JSON; <paramref name="corruption"/> describes why it is unusable, or is null.</summary>
        internal static List<EditorOperation> ParseJournal(string json, out string corruption)
        {
            corruption = null;
            try
            {
                var operations = JsonConvert.DeserializeObject<List<EditorOperation>>(json ?? string.Empty) ?? new List<EditorOperation>();
                if (!EditorOperationCoordinator.ValidJournal(operations)) { corruption = "invalid operation journal"; return null; }
                return operations;
            }
            catch (JsonException ex) { corruption = ex.Message; return null; }
        }

        internal static string MoveCorruptJournalAside(string path)
        {
            var aside = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".json");
            try { RetryJournalIo(_ => { File.Move(path, aside); return true; }); }
            catch (Exception) { return path; }
            return aside;
        }

        private static void EnsureJournalLoaded()
        {
            if (journalReadPending && Coordinator.Active == null) LoadJournal();
        }

        internal static object CurrentEditor()
        {
            var state = Snapshot();
            return new {
            is_compiling = state.is_compiling, is_updating = state.is_updating,
            is_playing = state.is_playing, is_changing_play_mode = state.is_changing_play_mode,
            compilation_failed = state.compilation_failed, refresh_pending = state.refresh_pending,
            refresh_error = state.refresh_error, ready = state.ready,
            readiness_scope = "editor_import_compile_mode_only", player = PlayerLoopWitness.Snapshot(),
            context = Funplay.Editor.MCP.Server.MCPProjectContext.Current
            };
        }
        internal static EditorOperationSnapshot Snapshot() => new EditorOperationSnapshot
        {
            is_compiling = EditorApplication.isCompiling, is_updating = EditorApplication.isUpdating,
            is_playing = EditorApplication.isPlaying,
            is_changing_play_mode = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode,
            compilation_failed = EditorUtility.scriptCompilationFailed,
            // A recovered refresh is still pending until its post-reload script check settles.
            refresh_pending = (refreshTask != null && !refreshTask.IsCompleted) || (recoveredRefresh && Coordinator.Active != null),
            refresh_error = refreshError
        };
        internal static object Start(string target, bool refresh, int timeout, string key)
        {
            // An unread journal is retried on every call; a failed write is retried by the next save and
            // reported as journal_error on the receipt (the operation itself runs from memory).
            EnsureJournalLoaded();
            if (journalReadPending) return Response.Error("OPERATION_JOURNAL_UNAVAILABLE", new { detail = JournalError, journal_path = JournalPath, retryable = true });
            try
            {
                // Idempotent lookup must work even while the original operation imports assets.
                var previous = !string.IsNullOrWhiteSpace(key) ? Operations.LastOrDefault(x => x.request_key == key) : null;
                if (previous == null && refreshTask != null && !refreshTask.IsCompleted && Coordinator.Active == null)
                    return Response.Error("REFRESH_STILL_RUNNING", new { current_editor = CurrentEditor() });
                var op = Coordinator.Start(target, refresh, timeout, key, Now);
                if (previous == null) { refreshTask = null; refreshError = null; recoveredRefresh = false; recoveredPendingSince = 0; }
                return Receipt(op);
            }
            catch (ArgumentException ex) { return Response.Error("INVALID_ARGUMENT", new { detail = ex.Message }); }
            catch (InvalidOperationException ex) { return Response.Error(ex.Message, new { active_operation_id = Coordinator.Active?.operation_id }); }
            catch (Exception ex) { return Response.Error("OPERATION_JOURNAL_UNAVAILABLE", new { detail = ex.Message, journal_path = JournalPath }); }
        }
        internal static object Get(string id, string key)
        {
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(key)) return Response.Error("OPERATION_ID_OR_REQUEST_KEY_REQUIRED");
            EnsureJournalLoaded();
            if (journalReadPending) return Response.Error("OPERATION_JOURNAL_UNAVAILABLE", new { detail = JournalError, journal_path = JournalPath, retryable = true });
            var op = Operations.LastOrDefault(x => (string.IsNullOrEmpty(id) || x.operation_id == id) && (string.IsNullOrEmpty(key) || x.request_key == key));
            if (op != null) return Receipt(op);
            return Response.Error("OPERATION_NOT_FOUND", string.IsNullOrEmpty(id)
                ? (object)new { retained = 32, request_key = key, hint = "No operation was started with this request_key: the request never started (e.g. it was still queued when a reload began). Repeating prepare_editor with the same request_key is safe." }
                : new { retained = 32 });
        }
        internal static object List() { EnsureJournalLoaded(); return ListCore(); }
        private static object ListCore() => Response.Success("Recent editor operations (newest first).", new
        { operations = Operations.AsEnumerable().Reverse().ToArray(), current_editor = CurrentEditor(), journal_path = JournalPath, journal_error = JournalError });
        internal static object Cancel(string id)
        {
            EnsureJournalLoaded();
            var op = Operations.LastOrDefault(x => x.operation_id == id);
            if (op == null) return Response.Error("OPERATION_NOT_FOUND");
            Coordinator.Cancel(op, Now);
            return Receipt(op);
        }
        private static object Receipt(EditorOperation op) => Response.Success("Inspect operation.status; this envelope only acknowledges the query.", new
        { operation = op, current_editor = CurrentEditor(), journal_path = JournalPath, poll_after_ms = 1000, journal_error = JournalError });
        private static void Tick()
        {
            if (Coordinator.Active == null) return;
            try
            {
                if (refreshTask != null && refreshTask.IsCompleted)
                {
                    if (refreshTask.IsFaulted) refreshError = refreshTask.Exception?.GetBaseException().Message;
                    else if (refreshTask.IsCanceled) refreshError = "Refresh was interrupted.";
                    else if (refreshTask.Result.ScriptChangesStillPending) refreshError = "Script changes are still pending; a refresh interceptor may be active.";
                    refreshTask = null;
                }
                if (recoveredRefresh && !EditorApplication.isCompiling && !EditorApplication.isUpdating && Now - recoveredCheckedAt >= RecoveredCheckIntervalSeconds)
                {
                    // Reload destroys Tasks. Reconcile imported code, without repeating the refresh side effect.
                    // Unity often starts a further compile shortly after a reload, so pending scripts get a
                    // short grace period (the coordinator waits while compiling) before the refresh fails.
                    recoveredCheckedAt = Now;
                    if (!EditorRefreshHelper.CaptureScriptChangeState(true).HasPendingScriptChanges) recoveredRefresh = false;
                    else if (recoveredPendingSince == 0) recoveredPendingSince = Now;
                    else if (Now - recoveredPendingSince >= RecoveredPendingGraceSeconds)
                    {
                        recoveredRefresh = false;
                        refreshError = "Scripts are still pending after reload. Start a new preparation after inspecting compilation errors.";
                    }
                }
                else if (EditorApplication.isCompiling || EditorApplication.isUpdating) recoveredPendingSince = 0;
                switch (Coordinator.Tick(Snapshot(), Now))
                {
                    case EditorOperationAction.ExitPlay: EditorApplication.isPlaying = false; break;
                    case EditorOperationAction.Refresh:
                        refreshError = null;
                        refreshTask = EditorRefreshHelper.RefreshAndRequestCompilationAsync();
                        break;
                    case EditorOperationAction.EnterPlay: EditorApplication.isPlaying = true; break;
                }
            }
            catch (Exception ex)
            {
                try { Coordinator.Fail("EDITOR_OPERATION_FAILED", ex.Message, Now); }
                catch { JournalError = ex.Message; }
            }
        }
        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
                var temp = JournalPath + ".tmp";
                var json = JsonConvert.SerializeObject(Operations, Formatting.Indented);
                // Always temp + replace: an in-place overwrite can leave a truncated journal behind
                // that the next domain then fails to parse. A reader holding the file open blocks
                // File.Replace ("Unable to remove the file to be replaced"); that is retried.
                RetryJournalIo(attempt =>
                {
                    File.WriteAllText(temp, json);
                    if (!File.Exists(JournalPath)) File.Move(temp, JournalPath);
                    else File.Replace(temp, JournalPath, null);
                    return true;
                });
                // A transient write failure must not disable operations for the rest of the session.
                if (journalErrorFromSave) { JournalError = null; journalErrorFromSave = false; }
            }
            catch (Exception ex)
            {
                // The operation keeps running from memory; the receipt carries journal_error and the
                // next save retries. Only a reload before that would lose it.
                if (JournalError == null || journalErrorFromSave) { JournalError = "Cannot write operation journal: " + ex.Message; journalErrorFromSave = true; }
            }
        }

        // Antivirus, indexers, git or a second Editor briefly lock the journal on Windows and the
        // write or read fails with a sharing violation. Retry a few times with a short backoff.
        internal static T RetryJournalIo<T>(Func<int, T> io, int attempts = JournalIoAttempts, int delayMilliseconds = 25)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return io(attempt); }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < attempts)
                {
                    Thread.Sleep(delayMilliseconds * attempt);
                }
            }
        }
    }
}
