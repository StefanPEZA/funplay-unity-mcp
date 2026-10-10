// Copyright (C) Funplay. Licensed under MIT.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Asset")]
    internal static class AssetFunctions
    {
        [Description("Create a new material with a specified color")]
        [SceneEditingTool]
        public static string CreateMaterial(
            [ToolParam("Name of the material")] string name,
            [ToolParam("Color as 'r,g,b,a' or hex '#RRGGBB'", Required = false)] string color = "1,1,1,1",
            [ToolParam("Shader name (default: Standard)", Required = false)] string shader = "Standard",
            [ToolParam("Save path (e.g. 'Assets/Materials/')", Required = false)] string save_path = "Assets/Materials/")
        {
            string actualShader = shader;
            string colorProperty = "_Color";

            if (shader == "Standard")
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline != null)
                {
                    string pipelineName = pipeline.GetType().Name;
                    if (pipelineName.Contains("Universal") || pipelineName.Contains("URP"))
                    {
                        actualShader = "Universal Render Pipeline/Lit";
                        colorProperty = "_BaseColor";
                    }
                    else if (pipelineName.Contains("HD") || pipelineName.Contains("HDRP"))
                    {
                        actualShader = "HDRP/Lit";
                        colorProperty = "_BaseColor";
                    }
                }
            }
            else
            {
                if (shader.StartsWith("Universal Render Pipeline") || shader.StartsWith("HDRP"))
                    colorProperty = "_BaseColor";
            }

            var shaderObj = Shader.Find(actualShader);
            if (shaderObj == null)
                return ToolResultFormatter.Error("SHADER_NOT_FOUND", new { shader = actualShader });

            var material = new Material(shaderObj);
            material.name = name;

            var c = ParseColor(color);
            if (material.HasProperty(colorProperty))
                material.SetColor(colorProperty, c);
            else
                material.color = c;

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{name}.mat";
            AssetDatabase.CreateAsset(material, fullPath);
            AssetDatabase.Refresh();

            string pipelineInfo = actualShader != shader ? $" (auto-detected: {actualShader})" : "";
            return $"Created material '{name}' at {fullPath}{pipelineInfo}";
        }

        [Description("Assign a material to a GameObject's renderer")]
        [SceneEditingTool]
        public static string AssignMaterial(
            [ToolParam("GameObject name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name,
            [ToolParam("Path to the material asset")] string material_path)
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { game_object_name });

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return ToolResultFormatter.Error("RENDERER_NOT_FOUND", new { game_object_name });

            var mat = AssetDatabase.LoadAssetAtPath<Material>(material_path);
            if (mat == null)
                return ToolResultFormatter.Error("MATERIAL_NOT_FOUND", new { material_path });

            Undo.RecordObject(renderer, $"Assign material to {go.name}");
            renderer.sharedMaterial = mat;
            return $"Assigned material '{mat.name}' to '{go.name}'";
        }

        [Description("Search for assets by type and name")]
        [ReadOnlyTool]
        public static string FindAssets(
            [ToolParam("Search filter (e.g. 't:Material red', 't:Prefab Player', 't:Texture')")] string filter)
        {
            var guids = AssetDatabase.FindAssets(filter);
            if (guids.Length == 0)
                return $"No assets found for filter: {filter}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {guids.Length} assets:");
            int count = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                sb.AppendLine($"  - {path}");
                count++;
                if (count >= 50) { sb.AppendLine("  ... (truncated)"); break; }
            }
            return sb.ToString();
        }

        [Description("Move an asset under Assets/ to the OS trash (recoverable; not a permanent delete). " +
                     "Folders require recursive=true. Paths outside Assets/ (Packages, ProjectSettings) are refused.")]
        [SceneEditingTool]
        public static object DeleteAsset(
            [ToolParam("Project-relative path under Assets/ (e.g. 'Assets/Old/Icon.png')")] string path,
            [ToolParam("Allow moving a folder and everything in it to the trash", Required = false)] bool recursive = false)
        {
            if (!TryNormalizeAssetsPath(path, out var normalized))
                return Response.Error("INVALID_ASSET_PATH", new { path, expected = "A project-relative path under Assets/ (not Assets itself)" });

            var isFolder = AssetDatabase.IsValidFolder(normalized);
            if (!isFolder && !File.Exists(normalized))
                return Response.Error("ASSET_NOT_FOUND", new { path = normalized });
            if (isFolder && !recursive)
                return Response.Error("FOLDER_REQUIRES_RECURSIVE", new
                {
                    path = normalized,
                    asset_count = AssetDatabase.FindAssets(string.Empty, new[] { normalized }).Length,
                    hint = "Pass recursive=true to move the folder and its contents to the trash."
                });

            if (!AssetDatabase.MoveAssetToTrash(normalized))
                return Response.Error("ASSET_DELETE_FAILED", new { path = normalized });

            return Response.Success($"Moved to trash: {normalized}", new { path = normalized, folder = isFolder, recoverable = true });
        }

        internal static bool TryNormalizeAssetsPath(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var candidate = path.Trim().Replace('\\', '/').TrimEnd('/');
            if (Path.IsPathRooted(candidate) || !candidate.StartsWith("Assets/", StringComparison.Ordinal))
                return false;
            foreach (var segment in candidate.Split('/'))
                if (segment == ".." || segment == "." || segment.Length == 0)
                    return false;

            normalized = candidate;
            return true;
        }

        [Description("Rename an asset")]
        [SceneEditingTool]
        public static string RenameAsset(
            [ToolParam("Current path of the asset")] string path,
            [ToolParam("New name (without extension)")] string new_name)
        {
            var result = AssetDatabase.RenameAsset(path, new_name);
            return string.IsNullOrEmpty(result)
                ? $"Renamed to '{new_name}'"
                : ToolResultFormatter.Error("ASSET_RENAME_FAILED", new { path, new_name, message = result });
        }

        [Description("Copy an asset to a new location")]
        [SceneEditingTool]
        public static string CopyAsset(
            [ToolParam("Source asset path")] string source_path,
            [ToolParam("Destination asset path")] string destination_path)
        {
            var dir = Path.GetDirectoryName(destination_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool copied = AssetDatabase.CopyAsset(source_path, destination_path);
            return copied
                ? $"Copied '{source_path}' to '{destination_path}'"
                : ToolResultFormatter.Error("ASSET_COPY_FAILED", new { source_path, destination_path });
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return Color.white;
            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var c)) return c;
            }
            value = value.Trim('(', ')', ' ');
            var p = value.Split(',');
            if (p.Length >= 3)
            {
                float r = float.Parse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                float g = float.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                float b = float.Parse(p[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                float a = p.Length >= 4 ? float.Parse(p[3].Trim(), System.Globalization.CultureInfo.InvariantCulture) : 1f;
                return new Color(r, g, b, a);
            }
            return Color.white;
        }
    }
}
