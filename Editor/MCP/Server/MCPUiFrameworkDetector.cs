// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// One-time, per-project detection of the UI frameworks in use, so Core exposes only the matching
    /// UI pack. Deliberately not per scene: a result that changed with the open scene, Play Mode or a
    /// Prefab Stage would flip the exposed tools without a tools/list_changed. The result is stored in
    /// settings; it only re-runs when nothing is stored or the user presses "Re-detect".
    /// UI Toolkit: any PanelSettings asset, .uxml or .uss under Assets/. uGUI: any text-serialized
    /// .unity/.prefab under Assets/ containing a Canvas (class ID 223), scanned off the main thread.
    /// </summary>
    internal static class MCPUiFrameworkDetector
    {
        private const string CanvasHeader = "--- !u!223 ";
        private static bool _running;

        public static bool IsRunning => _running;

        /// <summary>Starts detection when no result is stored. Main thread only.</summary>
        public static void EnsureDetected(ISettingsController settings)
        {
            if (settings == null || !string.IsNullOrEmpty(settings.MCPDetectedUiPack))
                return;
            Detect(settings);
        }

        /// <summary>Runs detection now and stores the result when done. Main thread only.</summary>
        public static void Detect(ISettingsController settings)
        {
            if (settings == null || _running)
                return;

            string assetsPath;
            bool usesUIToolkit;
            try
            {
                assetsPath = Application.dataPath;
                usesUIToolkit = AssetDatabase.FindAssets("t:PanelSettings", new[] { "Assets" }).Length > 0 ||
                                HasFileWithExtension(assetsPath, "*.uxml") ||
                                HasFileWithExtension(assetsPath, "*.uss");
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] UI framework detection failed: " + ex.Message);
                return;
            }

            _running = true;
            Task.Run(() => ContainsCanvas(assetsPath)).ContinueWith(task =>
            {
                var usesUgui = !task.IsFaulted && task.Result;
                EditorApplication.delayCall += () =>
                {
                    _running = false;
                    var result = Classify(usesUgui, usesUIToolkit);
                    PluginDebugLogger.Log($"[Funplay MCP Server] Detected UI framework pack: {result} (uGUI={usesUgui}, UI Toolkit={usesUIToolkit}).");
                    settings.MCPDetectedUiPack = result;
                };
            });
        }

        /// <summary>Both or neither → "both", so a project without UI yet still sees both packs.</summary>
        internal static string Classify(bool usesUgui, bool usesUIToolkit)
        {
            if (usesUgui == usesUIToolkit)
                return MCPToolExportPolicy.UiPackBoth;
            return usesUgui ? MCPToolExportPolicy.UiPackUgui : MCPToolExportPolicy.UiPackUIToolkit;
        }

        internal static bool ContainsCanvas(string assetsPath)
        {
            foreach (var pattern in new[] { "*.prefab", "*.unity" })
            {
                foreach (var file in SafeEnumerate(assetsPath, pattern))
                {
                    if (FileContainsCanvas(file))
                        return true;
                }
            }
            return false;
        }

        internal static bool FileContainsCanvas(string file)
        {
            try
            {
                using (var reader = new StreamReader(file))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith(CanvasHeader, StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return false;
        }

        private static bool HasFileWithExtension(string root, string pattern)
        {
            return SafeEnumerate(root, pattern).Any();
        }

        private static System.Collections.Generic.IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return Enumerable.Empty<string>();
            }
        }
    }
}
