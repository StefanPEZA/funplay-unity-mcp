// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    internal static class ToolResultFormatter
    {
        public static string Error(string code, object data = null)
        {
            try
            {
                return JsonConvert.SerializeObject(Response.Error(code, data));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Funplay] Failed to serialize tool error response: {ex.Message}");
                return JsonConvert.SerializeObject(Response.Error(code, new { serialization_error = ex.Message }));
            }
        }

        public static string ErrorMessage(string code, string message)
        {
            return Error(code, new { message });
        }

        public static string Exception(Exception ex)
        {
            return Error("TOOL_EXCEPTION", new { message = ex?.Message ?? "Unknown exception" });
        }

        public static bool IsError(string result)
        {
            if (string.IsNullOrEmpty(result))
                return false;

            try
            {
                var obj = JsonParse.ParseObject(result);
                return obj.TryGetValue("success", out var successToken) &&
                    successToken.Type == JTokenType.Boolean &&
                    !successToken.Value<bool>();
            }
            catch
            {
                return false;
            }
        }
    }
}
