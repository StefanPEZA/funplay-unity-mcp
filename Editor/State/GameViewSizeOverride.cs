// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Journaled Game View size changes. Two independent slots: "size" (set_game_view_size, kept until
    /// restore=true or the next Editor start) and "preview" (preview_ui_toolkit, restored when the call
    /// ends, before a reload, and at the next start). The preview slot nests over the size slot: it
    /// snapshots whatever the size slot selected and puts it back. Custom sizes live in the user's
    /// GameViewSizes preferences; each one is labelled "Funplay MCP …" so leftovers can be removed.
    /// </summary>
    [InitializeOnLoad]
    internal static class GameViewSizeOverride
    {
        internal const string LabelBase = "Funplay MCP ";

        /// <summary>
        /// GameViewSizes is a per-user preference shared by every project; labels carry a short hash of
        /// this project's path so startup cleanup never removes another project's size.
        /// </summary>
        internal static readonly string LabelPrefix = LabelBase + ProjectTag(Path.GetDirectoryName(Application.dataPath) ?? "") + " ";

        internal static string ProjectTag(string projectPath)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectPath.Replace('\\', '/').TrimEnd('/').ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 3).Replace("-", "").ToLowerInvariant();
            }
        }
        internal const string SizeSlot = "size", PreviewSlot = "preview";
        private const string SessionKey = "Funplay.GameViewSize.EditorSession";

        [Serializable]
        internal sealed class Journal
        {
            public string slot, label, editor_session;
            public int width, height;
            public PreviewViewState original, applied;
        }

        internal sealed class Preset
        {
            public string Name; public int Width, Height; public float DpScale;
        }

        /// <summary>Named sizes; dp_scale is the UI Toolkit scale preview_ui_toolkit applies with them.</summary>
        internal static readonly Preset[] Presets =
        {
            new Preset { Name = "desktop", Width = 1920, Height = 1080, DpScale = 1f },
            new Preset { Name = "phone-320", Width = 640, Height = 1136, DpScale = 2f },
            new Preset { Name = "phone-360", Width = 720, Height = 1280, DpScale = 2f },
            new Preset { Name = "phone-landscape", Width = 1280, Height = 720, DpScale = 2f },
        };

        internal static Preset FindPreset(string name)
        {
            foreach (var preset in Presets)
                if (string.Equals(preset.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return preset;
            return null;
        }

        internal static string JournalPath(string slot) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "FunplayMcp", "game-view-" + slot + ".json");

        static GameViewSizeOverride()
        {
            // Restores never run inside InitializeOnLoad: the Game View and its sizes are not ready yet.
            var freshEditor = SessionState.GetString(SessionKey, "").Length == 0;
            if (freshEditor) SessionState.SetString(SessionKey, Guid.NewGuid().ToString("N"));
            EditorApplication.delayCall += () => RecoverAtStartup(freshEditor);
            AssemblyReloadEvents.beforeAssemblyReload += () => TryRestore(PreviewSlot, new List<string>());
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode) TryRestore(PreviewSlot, new List<string>());
            };
        }

        private static void RecoverAtStartup(bool freshEditor)
        {
            var warnings = new List<string>();
            try
            {
                TryRestore(PreviewSlot, warnings);
                if (freshEditor) TryRestore(SizeSlot, warnings);
                var keep = new List<string>();
                var size = Load(SizeSlot);
                if (size != null) keep.Add(size.label);
                PreviewGameView.RemoveUnusedOwnedSizes(LabelPrefix, keep);
            }
            catch (Exception ex) { warnings.Add(ex.Message); }
            foreach (var warning in warnings)
                Debug.LogWarning("[Funplay] Game View size restore: " + warning);
        }

        internal static string EditorSession => SessionState.GetString(SessionKey, "");

        /// <summary>True while a preview_ui_toolkit call owns the Game View size.</summary>
        internal static bool PreviewActive => File.Exists(JournalPath(PreviewSlot));

        internal static Journal Load(string slot)
        {
            var path = JournalPath(slot);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<Journal>(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }

        private static void Save(Journal journal)
        {
            var path = JournalPath(journal.slot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(journal, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        /// <summary>Selects a fixed width x height in the main Game View, journaling the original first.</summary>
        internal static Journal Apply(string slot, int width, int height)
        {
            PreviewGameView.ValidateDimensions(width, height);
            var existing = Load(slot);
            var warnings = new List<string>();
            if (existing != null) TryRestore(slot, warnings);

            var original = PreviewGameView.Capture() ?? throw new InvalidOperationException("GAME_VIEW_SETTINGS_UNAVAILABLE: open a Game tab first.");
            var journal = new Journal
            {
                slot = slot, width = width, height = height, original = original, editor_session = EditorSession,
                label = LabelPrefix + slot + " " + width + "x" + height + " " + Guid.NewGuid().ToString("N").Substring(0, 6)
            };
            // Journal before touching the view: a crash in between leaves a restorable record.
            Save(journal);
            PreviewGameView.Apply(original, width, height, journal.label);
            journal.applied = PreviewGameView.Capture();
            Save(journal);
            return journal;
        }

        /// <summary>Restores the slot's original Game View size; true when there was something to restore.</summary>
        internal static bool TryRestore(string slot, List<string> warnings)
        {
            var journal = Load(slot);
            if (journal == null) return false;
            try
            {
                // Window IDs are only meaningful in the session that wrote them.
                PreviewGameView.Rebind(journal.original, journal.applied, journal.label, journal.editor_session != EditorSession);
                PreviewGameView.Restore(journal.original, journal.applied, journal.label, warnings);
            }
            catch (Exception ex) { warnings.Add("Game View restore failed: " + ex.Message); }
            try { File.Delete(JournalPath(slot)); }
            catch (Exception ex) { warnings.Add("Could not delete the Game View journal: " + ex.Message); }
            return true;
        }
    }
}
