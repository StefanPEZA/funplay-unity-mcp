// Copyright (C) Funplay. Licensed under MIT.
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ResponseRevisionTests
    {
        private static string Big(string tail = "") => "{\"success\":true,\"data\":\"" + new string('x', 1500) + tail + "\"}";

        [Test]
        public void SmallRepliesAreLeftAlone() => Assert.AreEqual("{\"success\":true}", ResponseRevision.Apply("{\"success\":true}", null));

        [Test]
        public void LargeJsonRepliesGainARevisionAndStayValidJson()
        {
            var json = JObject.Parse(ResponseRevision.Apply(Big(), null));
            Assert.AreEqual(16, ((string)json["revision"]).Length);
            Assert.IsTrue((bool)json["success"]);
        }

        [Test]
        public void MatchingRevisionReturnsOnlyUnchanged()
        {
            var revision = (string)JObject.Parse(ResponseRevision.Apply(Big(), null))["revision"];
            var again = ResponseRevision.Apply(Big(), revision);
            Assert.Less(again.Length, 100);
            Assert.IsTrue((bool)JObject.Parse(again)["unchanged"]);
            Assert.IsNull(JObject.Parse(ResponseRevision.Apply(Big("y"), revision))["unchanged"], "different content is returned in full");
        }

        [Test]
        public void PlainTextGetsATrailingRevisionLine()
        {
            var text = new string('a', 1200);
            var withRevision = ResponseRevision.Apply(text, null);
            StringAssert.EndsWith("revision: " + ResponseRevision.Compute(text), withRevision);
        }
    }
}
