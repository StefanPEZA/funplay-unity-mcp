// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class Mp4ColorTagTests
    {
        private static byte[] Box(string type, params byte[][] children)
        {
            var body = children.SelectMany(c => c).ToArray();
            var size = 8 + body.Length;
            return new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size }
                .Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
        }

        // ftyp, mdat, moov > trak > mdia > minf > stbl > stsd (8-byte header) > avc1 (78-byte visual entry) > avcC, like MediaEncoder's files.
        private static byte[] Mp4(bool moovFirst = false)
        {
            var avc1 = Box("avc1", new byte[78], Box("avcC", new byte[5]));
            var stsd = Box("stsd", new byte[8], avc1);
            var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", stsd, Box("stco", new byte[8]))))));
            var parts = new List<byte[]> { Box("ftyp", new byte[8]) };
            if (moovFirst) parts.Add(moov);
            parts.Add(Box("mdat", new byte[32]));
            if (!moovFirst) parts.Add(moov);
            return parts.SelectMany(p => p).ToArray();
        }

        private static int Find(byte[] data, string type) => Encoding.ASCII.GetString(data).IndexOf(type, System.StringComparison.Ordinal) - 4;
        private static int Size(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];

        [Test]
        public void AddsABt709ColrBoxToTheSampleEntryAndGrowsEveryParent()
        {
            var original = Mp4();
            var tagged = Mp4ColorTag.Tag(original);
            Assert.IsNotNull(tagged);
            Assert.AreEqual(original.Length + 19, tagged.Length);
            var colr = Find(tagged, "colr");
            Assert.Greater(colr, Find(tagged, "avcC"), "colr follows avcC inside avc1");
            CollectionAssert.AreEqual(new byte[] { (byte)'n', (byte)'c', (byte)'l', (byte)'x', 0, 1, 0, 1, 0, 1, 0 }, tagged.Skip(colr + 8).Take(11).ToArray());
            foreach (var parent in new[] { "moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1" })
                Assert.AreEqual(Size(original, Find(original, parent)) + 19, Size(tagged, Find(tagged, parent)), parent);
            CollectionAssert.AreEqual(original.Take(Find(original, "moov")).ToArray(), tagged.Take(Find(tagged, "moov")).ToArray(), "ftyp and mdat are untouched");
        }

        [Test]
        public void LeavesTaggedFilesAndMoovBeforeMdatAlone()
        {
            Assert.IsNull(Mp4ColorTag.Tag(Mp4ColorTag.Tag(Mp4())), "already tagged");
            Assert.IsNull(Mp4ColorTag.Tag(Mp4(moovFirst: true)), "chunk offsets would move");
            Assert.IsNull(Mp4ColorTag.Tag(new byte[] { 0, 0, 0, 3, 1, 2 }), "not an MP4");
        }
    }
}
