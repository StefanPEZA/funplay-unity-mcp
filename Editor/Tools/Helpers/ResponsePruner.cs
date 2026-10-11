// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using Funplay.Editor.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Compacts the JSON text of receipt and echo tools by dropping fields that carry no information:
    /// null, empty strings/arrays/objects, and a fixed list of flags whose absence means false.
    /// Data readers (execute_code, get_visual_tree, get_component_properties, ...) are never touched,
    /// because there an empty value is the answer. Everything else, including <c>error</c>, stays.
    /// </summary>
    internal static class ResponsePruner
    {
        internal static readonly HashSet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_task", "prepare_editor", "run_tests", "enter_play_mode", "exit_play_mode", "request_recompile",
            "click_ui_toolkit_element", "wait_for_ui_toolkit", "scroll_ui_toolkit", "pointer_ui_toolkit", "navigate_ui_toolkit",
            "capture_game_view", "extract_recording_frames", "record_game_view", "set_game_view_size", "open_scene",
            "simulate_key_press"
        };

        /// <summary>Keys whose <c>false</c> is the default and is dropped; absent means false.</summary>
        internal static readonly HashSet<string> DefaultFalseFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "fell_back_to_file", "is_changing_play_mode", "compilation_failed", "refresh_pending", "is_compiling",
            "is_updating", "domain_reload_expected", "scrolled_into_view", "receiver_detached", "focusable",
            "hasFilters", "read_only", "covered", "exposed", "click_event_received", "receiver_disabled"
        };

        /// <summary>Returns <paramref name="json"/> unchanged unless the tool is on the allowlist and compact mode is on.</summary>
        internal static string Prune(string toolName, string json, string detail = null)
        {
            if (string.IsNullOrEmpty(json) || toolName == null || !Tools.Contains(toolName) || !ResponseDetail.IsCompact(detail))
                return json;
            if (json[0] != '{')
                return json;

            try
            {
                var root = JsonParse.ParseObject(json);
                if (root["success"] == null || !(root["data"] is JObject data))
                    return json;
                PruneObject(data);
                return root.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                return json;
            }
        }

        internal static void PruneObject(JObject obj)
        {
            foreach (var property in new List<JProperty>(obj.Properties()))
            {
                var value = property.Value;
                if (value is JObject child) PruneObject(child);
                else if (value is JArray array) PruneArray(array);

                if (IsDroppable(property.Name, property.Value))
                    property.Remove();
            }
        }

        private static void PruneArray(JArray array)
        {
            foreach (var item in array)
            {
                if (item is JObject child) PruneObject(child);
                else if (item is JArray nested) PruneArray(nested);
            }
        }

        private static bool IsDroppable(string name, JToken value)
        {
            switch (value.Type)
            {
                case JTokenType.Null: return true;
                case JTokenType.String: return value.Value<string>().Length == 0;
                case JTokenType.Array: return !((JArray)value).HasValues;
                case JTokenType.Object: return !((JObject)value).HasValues;
                case JTokenType.Boolean: return !value.Value<bool>() && DefaultFalseFlags.Contains(name);
                default: return false;
            }
        }
    }
}
