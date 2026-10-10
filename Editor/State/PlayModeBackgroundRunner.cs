// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using Funplay.Editor.DI;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Keeps Play Mode running while the Editor is in the background. Unity stops the player loop of an
    /// unfocused Editor unless Run In Background is on, which stalls game code, UI Toolkit schedulers and
    /// input while an agent drives the Editor (EditorApplication.QueuePlayerLoopUpdate does not help:
    /// measured on 6000.6, frames stay stuck). The flag is switched on when leaving Edit Mode, so it
    /// covers enter_play_mode, prepare_editor, Play Mode tests and the Play button, and restored when
    /// Edit Mode returns. A marker in Library/FunplayMcp survives crashes; the next Editor start restores.
    /// The restore is guarded: nothing is changed when the flag was already on before Play.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeBackgroundRunner
    {
        internal static string MarkerPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "FunplayMcp", "run-in-background-applied.txt");

        static PlayModeBackgroundRunner()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Crash recovery: a marker left behind while not playing means the restore never ran.
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.delayCall += Restore;
        }

        private static bool Enabled
        {
            get
            {
                var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
                return settings?.KeepPlayModeRunningInBackground ?? true;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode && Enabled)
                Apply();
            else if (change == PlayModeStateChange.EnteredEditMode)
                Restore();
        }

        internal static void Apply()
        {
            if (PlayerSettings.runInBackground || File.Exists(MarkerPath))
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
                File.WriteAllText(MarkerPath, "runInBackground was false; set to true by Funplay MCP for Play Mode at " + DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                // Without a marker a crash could leave the setting on; don't touch it.
                Debug.LogWarning("[Funplay] Could not write the Run In Background marker, leaving the setting unchanged: " + ex.Message);
                return;
            }

            PlayerSettings.runInBackground = true;
        }

        internal static void Restore()
        {
            if (!File.Exists(MarkerPath))
                return;

            if (PlayerSettings.runInBackground)
                PlayerSettings.runInBackground = false;

            try { File.Delete(MarkerPath); }
            catch (Exception ex) { Debug.LogWarning("[Funplay] Could not delete the Run In Background marker: " + ex.Message); }
        }
    }
}
