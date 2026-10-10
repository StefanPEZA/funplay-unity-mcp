// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;

namespace Funplay.Editor.Api.Models
{
    internal class ToolDefinition
    {
        public string type = "function";
        public ToolFunctionDef function;

        /// <summary>
        /// MCP tool annotations. Set for built-in tools by the schema builder; manually registered tools
        /// may set it too. Null means "unknown": clients then assume the spec defaults (not read-only,
        /// destructive, open world).
        /// </summary>
        public ToolAnnotationsDef annotations;
    }

    internal class ToolAnnotationsDef
    {
        public string title;
        public bool? readOnlyHint;
        public bool? destructiveHint;
        public bool? idempotentHint;
        public bool? openWorldHint;
    }

    internal class ToolFunctionDef
    {
        public string name;
        public string description;
        public ToolParametersDef parameters;
    }

    internal class ToolParametersDef
    {
        public string type = "object";
        public Dictionary<string, ToolPropertyDef> properties = new Dictionary<string, ToolPropertyDef>();
        public List<string> required = new List<string>();
    }

    internal class ToolPropertyDef
    {
        public string type;
        public string description;
        public string @default;
        /// <summary>Typed default exported as JSON (bool, number or string); takes precedence over the string default.</summary>
        public object defaultValue;
        public List<string> @enum;
    }
}
