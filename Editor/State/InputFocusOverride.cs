// Copyright (C) Funplay. Licensed under MIT.
#if ENABLE_INPUT_SYSTEM
using System;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Simulated keys must reach the game while the Game View (or the whole Editor) is unfocused. The
    /// Input System routes keyboard events to editor buffers when the Game View lacks focus
    /// (editorInputBehaviorInPlayMode) and resets/disables devices when the application loses focus
    /// (backgroundBehavior); both are switched for Play Mode on the first simulated key press and
    /// restored on Play exit, before a script reload and at the next Editor start. The original values
    /// are journaled in Library/FunplayMcp first; a restore only reverts a value that is still the one
    /// set here, so a change the user made meanwhile is kept. The settings object is never marked dirty.
    /// </summary>
    [InitializeOnLoad]
    internal static class InputFocusOverride
    {
        [Serializable]
        internal sealed class Journal
        {
            public string background_behavior, applied_background_behavior;
            public string editor_input_behavior, applied_editor_input_behavior;
            public string created;
        }

        internal static string JournalPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "FunplayMcp", "input-focus-override.json");

        static InputFocusOverride()
        {
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode) Restore();
            };
            AssemblyReloadEvents.beforeAssemblyReload += Restore;
            // Crash recovery: a journal left behind while not playing means the restore never ran.
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.delayCall += Restore;
        }

        internal static bool Active => File.Exists(JournalPath);

        /// <summary>"applied", "already" (both settings already route input to the game) or "failed: …".</summary>
        internal static string Apply()
        {
            var settings = InputSystem.settings;
            if (settings == null) return "failed: InputSystem.settings is not available.";
            var background = settings.backgroundBehavior;
            var editor = settings.editorInputBehaviorInPlayMode;
            const InputSettings.BackgroundBehavior wantedBackground = InputSettings.BackgroundBehavior.IgnoreFocus;
            const InputSettings.EditorInputBehaviorInPlayMode wantedEditor = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (background == wantedBackground && editor == wantedEditor) return Active ? "applied" : "already";

            var journal = Load() ?? new Journal { created = DateTime.UtcNow.ToString("O") };
            if (background != wantedBackground && journal.background_behavior == null)
            {
                journal.background_behavior = background.ToString();
                journal.applied_background_behavior = wantedBackground.ToString();
            }
            if (editor != wantedEditor && journal.editor_input_behavior == null)
            {
                journal.editor_input_behavior = editor.ToString();
                journal.applied_editor_input_behavior = wantedEditor.ToString();
            }
            try { Save(journal); }
            catch (Exception ex)
            {
                // Without a journal a crash could leave the settings changed; don't touch them.
                return "failed: could not write the restore journal: " + ex.Message;
            }
            // The setters apply the settings to the running Input System.
            if (background != wantedBackground) settings.backgroundBehavior = wantedBackground;
            if (editor != wantedEditor) settings.editorInputBehaviorInPlayMode = wantedEditor;
            return "applied";
        }

        internal static void Restore()
        {
            var journal = Load();
            if (journal == null)
            {
                if (File.Exists(JournalPath)) TryDelete();
                return;
            }
            try
            {
                var settings = InputSystem.settings;
                if (settings != null)
                {
                    if (journal.background_behavior != null && settings.backgroundBehavior.ToString() == journal.applied_background_behavior &&
                        Enum.TryParse(journal.background_behavior, out InputSettings.BackgroundBehavior background))
                        settings.backgroundBehavior = background;
                    if (journal.editor_input_behavior != null && settings.editorInputBehaviorInPlayMode.ToString() == journal.applied_editor_input_behavior &&
                        Enum.TryParse(journal.editor_input_behavior, out InputSettings.EditorInputBehaviorInPlayMode editor))
                        settings.editorInputBehaviorInPlayMode = editor;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Funplay] Input focus settings restore failed: " + ex.Message); }
            TryDelete();
        }

        internal static Journal Load()
        {
            if (!File.Exists(JournalPath)) return null;
            try { return JsonConvert.DeserializeObject<Journal>(File.ReadAllText(JournalPath)); }
            catch (Exception) { return null; }
        }

        private static void Save(Journal journal)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
            var temp = JournalPath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(journal, Formatting.Indented));
            if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, null); else File.Move(temp, JournalPath);
        }

        private static void TryDelete()
        {
            try { File.Delete(JournalPath); }
            catch (Exception ex) { Debug.LogWarning("[Funplay] Could not delete the input focus journal: " + ex.Message); }
        }
    }
}
#endif
