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
        private static readonly List<EditorOperation> Operations;
        private static readonly EditorOperationCoordinator Coordinator;
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
            var session = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(session)) { session = Guid.NewGuid().ToString("N"); SessionState.SetString(SessionKey, session); }
            Operations = new List<EditorOperation>();
            try
            {
                if (File.Exists(JournalPath))
                    Operations = JsonConvert.DeserializeObject<List<EditorOperation>>(RetryJournalIo(_ => File.ReadAllText(JournalPath))) ?? Operations;
                if (!EditorOperationCoordinator.ValidJournal(Operations)) throw new InvalidDataException("Invalid operation journal; no actions will be replayed.");
            }
            catch (Exception ex) { Operations = new List<EditorOperation>(); JournalError = "Cannot read operation journal: " + ex.Message; }
            try { Coordinator = new EditorOperationCoordinator(Operations, session, Save, Now); }
            catch (Exception ex)
            {
                // Keep diagnostics callable even when a restarted operation cannot be persisted.
                Operations = new List<EditorOperation>(); JournalError = "Cannot recover operation journal: " + ex.Message;
                Coordinator = new EditorOperationCoordinator(Operations, session, Save, Now);
            }
            recoveredRefresh = Coordinator.Active?.step == 2 && Coordinator.Active.refresh_assets;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => Coordinator.Reload(Now);
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
                Coordinator.CompilerErrors(messages.Where(x => x.type == CompilerMessageType.Error)
                    .Select(x => $"{x.file}({x.line},{x.column}): {x.message}"), Now);
        }

        internal static object CurrentEditor() => Snapshot();
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
            // A failed read disables operations for the session; a failed write is retried by the next save.
            if (JournalError != null && !journalErrorFromSave) return Response.Error("OPERATION_JOURNAL_UNAVAILABLE", new { detail = JournalError, journal_path = JournalPath });
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
            var op = Operations.LastOrDefault(x => (string.IsNullOrEmpty(id) || x.operation_id == id) && (string.IsNullOrEmpty(key) || x.request_key == key));
            return op == null ? Response.Error("OPERATION_NOT_FOUND", new { retained = 32 }) : Receipt(op);
        }
        internal static object List() => Response.Success("Recent editor operations (newest first).", new
        { operations = Operations.AsEnumerable().Reverse().ToArray(), current_editor = CurrentEditor(), journal_path = JournalPath, journal_error = JournalError });
        internal static object Cancel(string id)
        {
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
                RetryJournalIo(attempt =>
                {
                    File.WriteAllText(temp, json);
                    if (!File.Exists(JournalPath)) File.Move(temp, JournalPath);
                    // File.Replace must delete the destination, which a reader holding it open blocks
                    // ("Unable to remove the file to be replaced"); the last attempt overwrites in place.
                    else if (attempt < JournalIoAttempts) File.Replace(temp, JournalPath, null);
                    else { File.Copy(temp, JournalPath, true); File.Delete(temp); }
                    return true;
                });
                // A transient write failure must not disable operations for the rest of the session.
                if (journalErrorFromSave) { JournalError = null; journalErrorFromSave = false; }
            }
            catch (Exception ex)
            {
                // A read failure stays sticky: a later save failure must not downgrade it to retryable.
                if (JournalError == null || journalErrorFromSave) { JournalError = ex.Message; journalErrorFromSave = true; }
                throw;
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
