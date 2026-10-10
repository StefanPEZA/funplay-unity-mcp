// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Reflection;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ExecuteCodeSafetyPolicyTests
    {
        [Test]
        public void ReadinessWaitsStayBelowTheTransportTimeoutAndStaleRunsAreFlagged()
        {
            // A Play Mode recompile is deferred until Play ends, so waiting there must be short.
            Assert.Less(ScriptExecutionFunctions.EditModeReadyWait.TotalSeconds + 7, 60, "refresh detection (~6.5 s) + wait must beat the 60 s transport timeout");
            Assert.LessOrEqual(ScriptExecutionFunctions.PlayModeReadyWait.TotalSeconds, 5);

            var plain = new { success = true, message = "ok" };
            Assert.AreSame(plain, ScriptExecutionFunctions.WithWarning(plain, null));
            var flagged = Newtonsoft.Json.Linq.JObject.FromObject(ScriptExecutionFunctions.WithWarning(plain, ScriptExecutionFunctions.PlayModeStaleScriptsWarning));
            Assert.IsTrue((bool)flagged["success"]);
            StringAssert.Contains("after Play Mode ends", (string)flagged["warning"]);
        }

        [Test]
        public void BaseSafety_BlocksExistingDangerousPatterns()
        {
            AssertBlocked("File.Delete(\"Assets/test.txt\");", false, "File.Delete");
            AssertBlocked("while (true) { }", false, "while");
            AssertBlocked("AssetDatabase.DeleteAsset(\"Assets/a.png\");", false, "DeleteAsset");
            AssertBlocked("AssetDatabase.DeleteAssets(paths, failed);", false, "DeleteAsset");
            AssertBlocked("AssetDatabase.MoveAssetToTrash(\"Assets/a.png\");", false, "DeleteAsset");
            AssertBlocked("AssetDatabase.MoveAssetsToTrash(paths, failed);", false, "DeleteAsset");
        }

        [Test]
        public void StrictSafety_BlocksBroadFilesystemWritesAndPaths()
        {
            AssertBlocked("File.WriteAllText(\"Assets/test.txt\", \"x\");", true, "File write");
            AssertBlocked("Directory.CreateDirectory(\"Assets/generated\");", true, "Directory write");
            AssertBlocked("var path = \"/Users/xyz/.ssh/config\";", true, "Absolute");
            AssertBlocked("var path = \"../ProjectSettings/ProjectSettings.asset\";", true, "Path traversal");
            AssertBlocked("var path = @\"C:\\Users\\Public\\test.txt\";", true, "Absolute");
        }

        [Test]
        public void StrictSafety_AllowsWritesUnderProjectLibraryAndTemp()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "Funplay Project");

            AssertAllowedIn(projectRoot, "File.WriteAllBytes(\"Library/FunplayMcp/Screenshots/crop.png\", bytes);");
            AssertAllowedIn(projectRoot, "System.IO.File.WriteAllText(@\"Temp/out.txt\", \"x\");");
            AssertAllowedIn(projectRoot,
                "var dir = Path.Combine(Application.dataPath, \"..\", \"Library\", \"FunplayMcp\");\n" +
                "Directory.CreateDirectory(dir);\n" +
                "var path = Path.Combine(dir, \"crop.png\");\n" +
                "File.WriteAllBytes(path, bytes);");
            AssertAllowedIn(projectRoot, "using (var writer = new StreamWriter(\"Temp/log.txt\")) { }");
            AssertAllowedIn(projectRoot, "File.Copy(\"Assets/a.png\", \"Library/a.png\", true);");

            // Absolute literals are allowed when they canonicalize into the project's Library/;
            // the Windows check compares mixed separators and case-insensitively.
            var absolute = Path.Combine(projectRoot, "Library", "crop.png");
            AssertAllowedIn(projectRoot, "File.WriteAllBytes(@\"" + absolute + "\", bytes);");
            if (Path.DirectorySeparatorChar == '\\')
                AssertAllowedIn(projectRoot, "File.WriteAllBytes(@\"" + absolute.ToUpperInvariant().Replace('\\', '/') + "\", bytes);");
        }

        [Test]
        public void StrictSafety_StillBlocksWritesOutsideLibraryAndTemp()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "Funplay Project");

            AssertBlockedIn(projectRoot, "File.WriteAllBytes(\"Assets/crop.png\", bytes);", "File write");
            AssertBlockedIn(projectRoot, "File.WriteAllBytes(\"Library\", bytes);", "File write");
            AssertBlockedIn(projectRoot, "File.WriteAllBytes(\"LibraryOther/crop.png\", bytes);", "File write");
            AssertBlockedIn(projectRoot, "File.WriteAllBytes(\"Library/x/../y.png\", bytes);", "Path traversal");
            AssertBlockedIn(projectRoot, "File.WriteAllBytes(Path.Combine(\"Library\", \"..\", \"Assets\", \"x\"), bytes);", "File write");
            AssertBlockedIn(projectRoot, "File.WriteAllBytes(path, bytes);", "File write");
            AssertBlockedIn(projectRoot, "var path = \"Library/a.png\"; path = \"Assets/a.png\"; File.WriteAllBytes(path, bytes);", "File write");
            AssertBlockedIn(projectRoot, "var path = \"Library/x\"; path += \"y\"; File.WriteAllBytes(path, bytes);", "File write");
            AssertBlockedIn(projectRoot, "File.WriteAllBytes($\"Library/{name}.png\", bytes);", "File write");
            // A second binding of the name (other scope, lambda, deconstruction) is not resolvable.
            AssertBlockedIn(projectRoot, "{ var p = \"Library/a\"; } foreach (var p in targets) File.WriteAllText(p, \"x\");", "File write");
            AssertBlockedIn(projectRoot, "var p = \"Library/a\"; targets.ForEach(p => File.WriteAllText(p, \"x\"));", "File write");
            AssertBlockedIn(projectRoot, "string p = \"Library/a\"; (p, q) = (target, 1); File.WriteAllText(p, \"x\");", "File write");
            AssertBlockedIn(projectRoot, "Directory.SetCurrentDirectory(home); File.WriteAllText(\"Library/a\", \"x\");", "working directory");
            AssertBlockedIn(projectRoot, "Environment.CurrentDirectory = home;", "working directory");
            AssertBlockedIn(projectRoot, "File.Move(\"Assets/a.png\", \"Library/a.png\");", "File write");
            AssertBlockedIn(projectRoot, "Directory.Move(\"Library\", \"Temp/Library\");", "Directory write");
            AssertBlockedIn(projectRoot, "var path = \"/etc/passwd\";", "Absolute");
            AssertBlockedIn(projectRoot, "var path = @\"" + Path.Combine(projectRoot + "Other", "Library", "x.png") + "\";", "Absolute");
        }

        [Test]
        public void NonStrictSafety_DoesNotApplyStrictFilesystemRules()
        {
            AssertAllowed("File.WriteAllText(\"Assets/test.txt\", \"x\");", false);
            AssertAllowed("var path = \"/Users/xyz/.ssh/config\";", false);
        }

        [Test]
        public void ToolResultFormatter_DetectsStructuredErrorResponses()
        {
            var error = ToolResultFormatter.Error("TEST_ERROR");

            Assert.IsTrue(ToolResultFormatter.IsError(error));
            Assert.IsFalse(ToolResultFormatter.IsError("Error: legacy"));
            Assert.IsFalse(ToolResultFormatter.IsError("{\"success\":true,\"message\":\"ok\"}"));
        }

        [Test]
        public void BuildCodeForCompilation_ProjectNamespaceInjectionDisabled_DoesNotInjectProjectNamespaces()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "public class Smoke { public static string Run() { return \"ok\"; } }",
                "TempScript",
                false,
                out var actualClassName);

            Assert.AreEqual("Smoke", actualClassName);
            Assert.IsFalse(fullCode.Contains("using Funplay.Editor.Tools.Builtins;"), fullCode);
            Assert.IsFalse(fullCode.Contains("using Funplay.Editor.Tests;"), fullCode);
        }

        [Test]
        public void BuildCodeForCompilation_IFunplayCommandMissingUsing_AddsScriptingNamespace()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "public class CommandScript : IFunplayCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = \"ok\"; } }",
                "TempScript",
                false,
                out var actualClassName);

            Assert.AreEqual("CommandScript", actualClassName);
            StringAssert.StartsWith("using Funplay.Editor.Tools.Scripting;", fullCode);
        }

        [Test]
        public void BuildCodeForCompilation_IFunplayCommandExistingUsing_DoesNotDuplicateScriptingNamespace()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "using Funplay.Editor.Tools.Scripting;\npublic class CommandScript : IFunplayCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = \"ok\"; } }",
                "TempScript",
                false,
                out _);

            var first = fullCode.IndexOf("using Funplay.Editor.Tools.Scripting;", StringComparison.Ordinal);
            Assert.GreaterOrEqual(first, 0, fullCode);
            Assert.AreEqual(first, fullCode.LastIndexOf("using Funplay.Editor.Tools.Scripting;", StringComparison.Ordinal), fullCode);
        }

        [Test]
        public void ReachableProjectNamespaceUsings_AreDerivedFromLoadedScriptAssemblies()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var usings = ScriptExecutionFunctions.GetReachableProjectNamespaceUsings(
                new[] { typeof(string).Assembly, typeof(ScriptExecutionFunctions).Assembly },
                projectRoot);

            StringAssert.Contains("using Funplay.Editor.Tools.Builtins;", usings);
            Assert.IsFalse(usings.Contains("using System;"), usings);
            Assert.IsFalse(usings.Contains("Funplay.Repro.Unreachable"), usings);
        }

        [Test]
        public void ProjectScriptAssemblyPath_OnlyAllowsLibraryScriptAssembliesUnderProject()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "Funplay Project");

            Assert.IsTrue(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Library", "ScriptAssemblies", "Game.Editor.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Library", "PackageCache", "com.example", "Example.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Packages", "com.example", "Example.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot + "Other", "Library", "ScriptAssemblies", "Game.Editor.dll"),
                projectRoot));
        }

        [Test]
        public void ExecuteCodeDiagnostics_UnwrapsTargetInvocationException()
        {
            var inner = new InvalidOperationException("real compiler failure");
            var wrapped = new TargetInvocationException(
                "Exception has been thrown by the target of an invocation.",
                new TargetInvocationException(inner));

            Assert.AreSame(inner, ScriptExecutionFunctions.UnwrapTargetInvocationException(wrapped));
        }

        private static void AssertBlocked(string code, bool strict, string expectedReasonPart)
        {
            Assert.IsTrue(ExecuteCodeSafetyPolicy.TryFindViolation(code, strict, out _, out var reason));
            StringAssert.Contains(expectedReasonPart, reason);
        }

        private static void AssertAllowed(string code, bool strict)
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(code, strict, out _, out _));
        }

        private static void AssertAllowedIn(string projectRoot, string code)
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(code, true, projectRoot, out _, out var reason), reason + "\n" + code);
        }

        private static void AssertBlockedIn(string projectRoot, string code, string expectedReasonPart)
        {
            Assert.IsTrue(ExecuteCodeSafetyPolicy.TryFindViolation(code, true, projectRoot, out _, out var reason), code);
            StringAssert.Contains(expectedReasonPart, reason, code);
        }
    }
}
