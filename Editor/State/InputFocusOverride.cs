// Copyright (C) Funplay. Licensed under MIT.
#if ENABLE_INPUT_SYSTEM
using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Devices the Input System disabled when focus was lost before the override applied are re-enabled:
    /// with IgnoreFocus it never re-enables them itself, so all input, real input included, would stay
    /// dead for the rest of the Play session.
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

        /// <summary>"applied", "already" (both settings already route input to the game) or "failed: …", plus re-enabled devices.</summary>
        internal static string Apply()
        {
            var result = ApplySettings();
            if (result.StartsWith("failed", StringComparison.Ordinal)) return result;
            var reenabled = ReenableBackgroundDisabledDevices();
            return reenabled > 0 ? $"{result}; re-enabled {reenabled} device(s) the Input System had disabled while the Editor was in the background" : result;
        }

        private static string ApplySettings()
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

        // Internal Input System members: the per-device "disabled while in background" flag and the
        // manager call that undoes exactly that scope (a device a game disabled itself stays disabled).
        private static readonly PropertyInfo DisabledWhileInBackground =
            typeof(InputDevice).GetProperty("disabledWhileInBackground", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        private static readonly FieldInfo ManagerField = typeof(InputSystem).GetField("s_Manager", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>Re-enables devices disabled only because the Editor lost focus; returns how many.</summary>
        internal static int ReenableBackgroundDisabledDevices()
        {
            if (DisabledWhileInBackground == null) return 0;
            var manager = ManagerField?.GetValue(null);
            var method = manager?.GetType().GetMethod("EnableOrDisableDevice", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var parameters = method?.GetParameters();
            if (parameters == null || parameters.Length != 3 || !parameters[2].ParameterType.IsEnum) return 0;
            object scope;
            try { scope = Enum.Parse(parameters[2].ParameterType, "TemporaryWhilePlayerIsInBackground"); }
            catch (ArgumentException) { return 0; }

            var count = 0;
            foreach (var device in InputSystem.devices.ToArray())
            {
                if (!(DisabledWhileInBackground.GetValue(device, null) is bool disabled) || !disabled) continue;
                method.Invoke(manager, new[] { device, true, scope });
                count++;
            }
            return count;
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
