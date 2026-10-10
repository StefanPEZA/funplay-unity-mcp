// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using Funplay.Editor.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class JsonParseTests
    {
        private const string Utc = "2026-10-10T17:49:36.9490000+00:00";

        [Test]
        public void ParsedTimestampsReserializeUnchanged()
        {
            var receipt = JsonParse.ParseObject("{\"created_at\":\"" + Utc + "\",\"n\":1}");

            Assert.AreEqual(JTokenType.String, receipt["created_at"].Type);
            StringAssert.Contains("\"created_at\":\"" + Utc + "\"", receipt.ToString(Formatting.None));
        }

        [Test]
        public void ToolArgumentsKeepDateLikeStrings()
        {
            var arguments = JsonParse.ParseJsonObject("{\"text\":\"" + Utc + "\"}", 0, out _);

            Assert.AreEqual(Utc, arguments["text"]);
        }

        [Test]
        public void ParseRejectsWhatJTokenParseRejects()
        {
            Assert.Throws<JsonReaderException>(() => JsonParse.ParseObject("[1]"));
            Assert.Throws<JsonReaderException>(() => JsonParse.ParseArray("{}"));
            Assert.Throws<JsonReaderException>(() => JsonParse.ParseToken("{} trailing"));
            Assert.Throws<JsonReaderException>(() => JsonParse.ParseToken(""));
            Assert.AreEqual(2, JsonParse.ParseArray("[1, 2] ").Count);
        }
    }
}
