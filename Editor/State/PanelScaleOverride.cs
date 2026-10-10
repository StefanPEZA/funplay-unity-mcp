// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Temporary PanelSettings scale overrides for preview_ui_toolkit, applied to the instances the live
    /// hosts use (which may come from Addressables, so never looked up by asset path while applying).
    /// The original values are journaled in Library/FunplayMcp before the first change and restored when
    /// the preview ends, on Play Mode exit (asset changes made in Play Mode persist), before an assembly
    /// reload, and at the next Editor start. The asset is never marked dirty; the response compares the
    /// file hash so a save by another tool mid-preview is visible.
    /// </summary>
    [InitializeOnLoad]
    internal static class PanelScaleOverride
    {
        [Serializable]
        internal sealed class Entry
        {
            public string instance_id, asset_path, file_hash;
            public string scale_mode, screen_match_mode;
            public float scale, reference_dpi, fallback_dpi, match;
            public int reference_width, reference_height;
        }

        [Serializable]
        internal sealed class Journal
        {
            /// <summary>Editor session that wrote the journal; instance IDs are only meaningful within it.</summary>
            public string editor_session;
            /// <summary>The last override applied (ConstantPixelSize at this scale); repairs only touch files holding it.</summary>
            public float applied_scale;
            public List<Entry> entries = new List<Entry>();
        }

        private const string SessionKey = "Funplay.PanelScaleOverride.EditorSession";

        internal static string EditorSession
        {
            get
            {
                var session = SessionState.GetString(SessionKey, "");
                if (session.Length == 0) { session = Guid.NewGuid().ToString("N"); SessionState.SetString(SessionKey, session); }
                return session;
            }
        }

        internal static string JournalPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "FunplayMcp", "panel-scale-overrides.json");

        static PanelScaleOverride()
        {
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode) RestoreAll(new List<string>());
            };
            AssemblyReloadEvents.beforeAssemblyReload += () => RestoreAll(new List<string>());
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(JournalPath)) return;
                var warnings = new List<string>();
                RestoreAll(warnings);
                foreach (var warning in warnings) Debug.LogWarning("[Funplay] PanelSettings restore: " + warning);
            };
        }

        internal static Entry Snapshot(PanelSettings settings)
        {
            var path = AssetDatabase.GetAssetPath(settings);
            return new Entry
            {
                instance_id = ObjectIdHelper.GetSerializableId(settings),
                asset_path = string.IsNullOrEmpty(path) ? null : path,
                file_hash = string.IsNullOrEmpty(path) ? null : FileHash(path),
                scale_mode = settings.scaleMode.ToString(),
                screen_match_mode = settings.screenMatchMode.ToString(),
                scale = settings.scale,
                reference_dpi = settings.referenceDpi,
                fallback_dpi = settings.fallbackDpi,
                match = settings.match,
                reference_width = settings.referenceResolution.x,
                reference_height = settings.referenceResolution.y
            };
        }

        internal static bool SameValues(Entry entry, PanelSettings settings)
        {
            var now = Snapshot(settings);
            return now.scale_mode == entry.scale_mode && now.screen_match_mode == entry.screen_match_mode &&
                   Mathf.Approximately(now.scale, entry.scale) && Mathf.Approximately(now.reference_dpi, entry.reference_dpi) &&
                   Mathf.Approximately(now.fallback_dpi, entry.fallback_dpi) && Mathf.Approximately(now.match, entry.match) &&
                   now.reference_width == entry.reference_width && now.reference_height == entry.reference_height;
        }

        internal static void ApplyValues(Entry entry, PanelSettings settings)
        {
            settings.scaleMode = (PanelScaleMode)Enum.Parse(typeof(PanelScaleMode), entry.scale_mode);
            settings.screenMatchMode = (PanelScreenMatchMode)Enum.Parse(typeof(PanelScreenMatchMode), entry.screen_match_mode);
            settings.scale = entry.scale;
            settings.referenceDpi = entry.reference_dpi;
            settings.fallbackDpi = entry.fallback_dpi;
            settings.match = entry.match;
            settings.referenceResolution = new Vector2Int(entry.reference_width, entry.reference_height);
        }

        /// <summary>
        /// Sets ConstantPixelSize with <paramref name="scale"/> on each PanelSettings, journaling the
        /// original values of any not yet journaled. Returns the journal.
        /// </summary>
        internal static Journal Override(IEnumerable<PanelSettings> settings, float scale)
        {
            var journal = Load() ?? new Journal { editor_session = EditorSession };
            var list = settings.Where(s => s != null).Distinct().ToList();
            foreach (var panelSettings in list)
            {
                var id = ObjectIdHelper.GetSerializableId(panelSettings);
                if (journal.entries.Any(e => e.instance_id == id)) continue;
                journal.entries.Add(Snapshot(panelSettings));
            }
            // Journal before changing anything, including the scale a crash would leave behind.
            journal.applied_scale = scale;
            Save(journal);
            foreach (var panelSettings in list)
            {
                panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
                panelSettings.scale = scale;
            }
            return journal;
        }

        /// <summary>
        /// The journaled PanelSettings object: by asset path whenever it has one (instance IDs from another
        /// session may name a different object), by instance ID only for path-less instances of this session.
        /// </summary>
        internal static PanelSettings Find(Entry entry, bool sameSession)
        {
            if (entry.asset_path != null)
            {
                if (sameSession && ObjectIdHelper.ToObject(entry.instance_id) is PanelSettings live &&
                    AssetDatabase.GetAssetPath(live) == entry.asset_path)
                    return live;
                return AssetDatabase.LoadAssetAtPath<PanelSettings>(entry.asset_path);
            }
            return sameSession ? ObjectIdHelper.ToObject(entry.instance_id) as PanelSettings : null;
        }

        /// <summary>Whether <paramref name="settings"/> holds the preview override (ConstantPixelSize at the applied scale).</summary>
        internal static bool HoldsOverride(PanelSettings settings, float appliedScale) =>
            settings.scaleMode == PanelScaleMode.ConstantPixelSize && Mathf.Approximately(settings.scale, appliedScale);

        /// <summary>
        /// Puts every journaled PanelSettings back and deletes the journal. Within the session that applied
        /// the override, the live object is the truth for every other field, so when something saved the
        /// file mid-preview the restored object is saved again (a repair, not a temporary change). After a
        /// restart the asset was reloaded from disk: it is repaired only while the file still holds the
        /// override; anything else (a git pull, a manual edit) is left alone and reported.
        /// </summary>
        internal static List<object> RestoreAll(List<string> warnings)
        {
            var results = new List<object>();
            var journal = Load();
            if (journal == null) return results;
            var sameSession = journal.editor_session == EditorSession;
            foreach (var entry in journal.entries)
            {
                try
                {
                    var settings = Find(entry, sameSession);
                    if (settings == null)
                    {
                        warnings.Add($"PanelSettings {entry.asset_path ?? entry.instance_id} could not be found{(sameSession ? "" : " after the restart")}; nothing restored.");
                        continue;
                    }

                    var restored = false;
                    if (!SameValues(entry, settings))
                    {
                        if (sameSession || HoldsOverride(settings, journal.applied_scale)) { ApplyValues(entry, settings); restored = true; }
                        else warnings.Add($"{entry.asset_path} changed since the preview (not the preview scaling); left as it is.");
                    }

                    string hashAfter = entry.asset_path != null ? FileHash(entry.asset_path) : null;
                    var fileChanged = entry.file_hash != null && hashAfter != entry.file_hash;
                    if (fileChanged && restored)
                    {
                        // The file may hold the preview scaling: save the restored values back.
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssetIfDirty(settings);
                        hashAfter = FileHash(entry.asset_path);
                        fileChanged = hashAfter != entry.file_hash;
                        warnings.Add($"{entry.asset_path} was saved during the preview; its original scaling was saved back{(fileChanged ? " (other saved changes kept)" : "")}.");
                    }

                    results.Add(new
                    {
                        asset_path = entry.asset_path,
                        values_restored = restored || SameValues(entry, settings),
                        file_hash_before = entry.file_hash,
                        file_hash_after = hashAfter,
                        byte_identical = entry.file_hash == null ? (bool?)null : !fileChanged
                    });
                }
                catch (Exception ex) { warnings.Add($"Restoring {entry.asset_path ?? entry.instance_id} failed: {ex.Message}"); }
            }
            try { File.Delete(JournalPath); }
            catch (Exception ex) { warnings.Add("Could not delete the PanelSettings journal: " + ex.Message); }
            return results;
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

        internal static string FileHash(string assetPath)
        {
            try
            {
                var full = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", assetPath);
                if (!File.Exists(full)) return null;
                using (var sha = SHA1.Create())
                using (var stream = File.OpenRead(full))
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception) { return null; }
        }
    }
}
