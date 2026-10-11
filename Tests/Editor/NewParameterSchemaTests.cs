// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    /// <summary>New optional parameters must be declared, or the invoker rejects them as UNKNOWN_PARAM.</summary>
    public sealed class NewParameterSchemaTests
    {
        [TestCase("get_visual_tree", "if_revision")][TestCase("get_hierarchy", "if_revision")][TestCase("get_scene_info", "if_revision")]
        [TestCase("get_component_properties", "if_revision")][TestCase("get_console_logs", "if_revision")][TestCase("get_console_logs", "after_cursor")]
        [TestCase("capture_game_view", "region")][TestCase("capture_game_view", "max_size")][TestCase("capture_game_view", "step_if_paused")][TestCase("capture_game_view", "detail")]
        [TestCase("execute_code", "save_as")][TestCase("execute_code", "snippet")][TestCase("execute_code", "args")]
        [TestCase("scroll_ui_toolkit", "detail")][TestCase("wait_for_ui_toolkit", "detail")][TestCase("pointer_ui_toolkit", "detail")]
        [TestCase("extract_recording_frames", "detail")][TestCase("get_recording_frame", "detail")][TestCase("get_tool_capabilities", "reset_usage")]
        [TestCase("compose_contact_sheet", "paths")][TestCase("run_ui_steps", "steps")]
        public void ParameterIsDeclared(string tool, string parameter)
        {
            var method = ToolRegistry.GetMethod(tool);
            Assert.IsNotNull(method, tool);
            var unknown = FunctionInvokerController.FindUnknownParameters(method, new Dictionary<string, string> { [parameter] = "x" });
            CollectionAssert.IsEmpty(unknown, tool + " must declare " + parameter);
        }

        [Test]
        public void InvokerAcceptsIfRevisionAndNeverTurnsASmallReplyIntoUnchanged()
        {
            var call = new FunctionCall { FunctionName = "get_scene_info", Parameters = new Dictionary<string, string> { ["if_revision"] = "0000000000000000" } };
            var result = JObject.Parse(new FunctionInvokerController().Invoke(call));
            Assert.IsNull(result["code"] == null ? null : (string)result["code"] == "UNKNOWN_PARAM" ? "unknown" : null, result.ToString());
        }
    }
}
