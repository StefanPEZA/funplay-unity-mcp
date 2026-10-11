// Copyright (C) Funplay. Licensed under MIT.
using System.Collections;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Scripting;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class ExecuteCodeBodyFormTests
    {
        [TestCase("public class A { }", true, "A")]
        [TestCase("using System;\nnamespace N { }\npublic struct B { }", true, "B")]
        [TestCase("var s = \"a class Foo here\"; // class Bar\n/* class Baz */ return s;", false, null)]
        [TestCase("T Pick<T>(T x) where T : class { return x; }\nreturn 1;", false, null)]
        [TestCase("foreach (var x in list) { var y = x; }", false, null)]
        public void DetectsTypeDeclarationsOnlyOutsideStringsCommentsAndBodies(string code, bool declared, string name)
        {
            Assert.AreEqual(declared, SnippetSyntax.TryFindTypeDeclaration(code, out var found));
            Assert.AreEqual(name, found);
        }

        [Test]
        public void BodyIsWrappedAsACommandWithQualifiedContextAndHoistedUsings()
        {
            var code = ScriptExecutionFunctions.BuildCodeForCompilation(
                "using System.Threading;\nvar go = new GameObject(\"x\");\nreturn go.name;", "T1", false, out var name);
            Assert.AreEqual("T1", name);
            StringAssert.Contains("using System.Threading;", code);
            StringAssert.Contains("Execute(Funplay.Editor.Tools.Scripting.ExecutionContext ctx)", code);
            StringAssert.Contains("using UnityEngine.UIElements;", code);
            Assert.Less(code.IndexOf("using System.Threading;"), code.IndexOf("public sealed class"));
        }

        [Test]
        public void UguiSnippetsSkipUIElementsSoButtonStaysUnambiguous()
        {
            var code = ScriptExecutionFunctions.BuildCodeForCompilation("using UnityEngine.UI;\nvar b = new GameObject().AddComponent<Button>();", "T2", false, out _);
            StringAssert.DoesNotContain("using UnityEngine.UIElements;", code);
        }

        [UnityTest]
        public IEnumerator BodyWithCtxReturnsItsValueAndSurvivesAThreadingUsing()
        {
            var task = ScriptExecutionFunctions.ExecuteCode(
                "using System.Threading;\nvar n = new GameObject(\"BodyFormProbe\");\nctx.Log(n.name);\nUnityEngine.Object.DestroyImmediate(n);\nreturn 42;", false, true);
            while (!task.IsCompleted) yield return null;
            var result = JObject.FromObject(task.Result);
            Assert.IsTrue((bool)result["success"], result.ToString());
            Assert.AreEqual(42, (int)result["data"]["returnValue"]);
            StringAssert.Contains("BodyFormProbe", result["data"]["logs"].ToString());
        }

        [UnityTest]
        public IEnumerator BodyUsesUIElementsAndReflectionWithoutUsings()
        {
            var task = ScriptExecutionFunctions.ExecuteCode(
                "var l = new Label(\"hi\"); var f = typeof(Label).GetMethod(\"ToString\", BindingFlags.Public | BindingFlags.Instance);\nreturn l.text + (f != null);", false, true);
            while (!task.IsCompleted) yield return null;
            var result = JObject.FromObject(task.Result);
            Assert.IsTrue((bool)result["success"], result.ToString());
            Assert.AreEqual("hiTrue", (string)result["data"]["returnValue"]);
        }

        [UnityTest]
        public IEnumerator MissingInterfaceMemberGetsAOneLineFix()
        {
            var task = ScriptExecutionFunctions.ExecuteCode("public class Bad : IFunplayCommand { }", false, true);
            while (!task.IsCompleted) yield return null;
            var result = JObject.FromObject(task.Result);
            Assert.AreEqual("COMPILATION_FAILED", (string)result["code"], result.ToString());
            StringAssert.Contains("Execute(ExecutionContext ctx)", (string)result["data"]["fix"]);
        }
    }
}
