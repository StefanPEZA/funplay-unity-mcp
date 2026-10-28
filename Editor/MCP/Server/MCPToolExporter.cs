// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using Funplay.Editor.Tools;
using Funplay.Editor.Settings;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Exports Funplay tool definitions to MCP tool schema format.
    /// </summary>
    internal class MCPToolExporter
    {
        private readonly ISettingsController _settings;

        public MCPToolExporter(ISettingsController settings)
        {
            _settings = settings;
        }

        public List<Dictionary<string, object>> ExportTools()
        {
            var mcpTools = new List<Dictionary<string, object>>();
            var tools = ToolSchemaBuilder.BuildAll();
            var profile = MCPToolExportPolicy.Parse(_settings?.MCPToolExportProfile);
            var coreTools = MCPToolExportPolicy.GetEffectiveCoreTools(_settings);

            tools.Sort((left, right) =>
            {
                var leftRank = MCPToolExportPolicy.GetSortRank(left.function.name, profile, coreTools);
                var rightRank = MCPToolExportPolicy.GetSortRank(right.function.name, profile, coreTools);
                var compareRank = leftRank.CompareTo(rightRank);
                return compareRank != 0
                    ? compareRank
                    : string.Compare(left.function.name, right.function.name, StringComparison.OrdinalIgnoreCase);
            });

            PluginDebugLogger.Log($"[Funplay MCP Server] Exporting tools with profile '{MCPToolExportPolicy.ToSettingValue(profile)}'");

            foreach (var tool in tools)
            {
                if (!MCPToolExportPolicy.IsToolAllowed(tool.function.name, _settings))
                    continue;

                var mcpTool = new Dictionary<string, object>
                {
                    ["name"] = tool.function.name,
                    ["description"] = MCPToolExportPolicy.BuildDescriptionPrefix(tool.function.name, profile) + tool.function.description,
                    ["inputSchema"] = ConvertParametersToJsonSchema(tool.function.parameters)
                };
                var annotations = ConvertAnnotations(tool.annotations);
                if (annotations != null)
                    mcpTool["annotations"] = annotations;
                var method = ToolRegistry.GetMethod(tool.function.name);
                if (method == null || !ToolRegistry.IsReadOnly(method))
                {
                    var schema = (Dictionary<string, object>)mcpTool["inputSchema"];
                    var properties = (Dictionary<string, object>)schema["properties"];
                    properties[MCPProjectContext.ExpectedArgument] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "Verified project:Editor identity guard; required for writes through legacy unscoped URLs. Read context from the intended Editor first."
                    };
                }
                mcpTools.Add(mcpTool);
            }

            return mcpTools;
        }

        internal static Dictionary<string, object> ConvertAnnotations(Funplay.Editor.Api.Models.ToolAnnotationsDef annotations)
        {
            if (annotations == null)
                return null;

            var result = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(annotations.title))
                result["title"] = annotations.title;
            if (annotations.readOnlyHint.HasValue)
                result["readOnlyHint"] = annotations.readOnlyHint.Value;
            if (annotations.destructiveHint.HasValue)
                result["destructiveHint"] = annotations.destructiveHint.Value;
            if (annotations.idempotentHint.HasValue)
                result["idempotentHint"] = annotations.idempotentHint.Value;
            if (annotations.openWorldHint.HasValue)
                result["openWorldHint"] = annotations.openWorldHint.Value;
            return result.Count > 0 ? result : null;
        }

        private Dictionary<string, object> ConvertParametersToJsonSchema(
            Funplay.Editor.Api.Models.ToolParametersDef parameters)
        {
            var properties = new Dictionary<string, object>();

            foreach (var prop in parameters.properties)
            {
                var propertySchema = new Dictionary<string, object>
                {
                    ["type"] = prop.Value.type,
                    ["description"] = prop.Value.description
                };

                if (prop.Value.@enum != null && prop.Value.@enum.Count > 0)
                    propertySchema["enum"] = prop.Value.@enum;

                if (prop.Value.defaultValue != null)
                    propertySchema["default"] = prop.Value.defaultValue;
                else if (!string.IsNullOrEmpty(prop.Value.@default))
                    propertySchema["default"] = prop.Value.@default;

                properties[prop.Key] = propertySchema;
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (parameters.required != null && parameters.required.Count > 0)
                schema["required"] = parameters.required;

            return schema;
        }
    }
}
