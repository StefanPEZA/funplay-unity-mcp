// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Adds a BT.709 'colr' (nclx) box to the H.264 sample entry of an MP4 written by Unity's MediaEncoder.
    /// The encoder has no colour settings, so the file declares unknown primaries and Windows Media Foundation
    /// warns ("Color primaries 0 is unknown") whenever it is decoded, e.g. by extract_recording_frames.
    /// Only the moov box grows, so the file is left alone unless moov follows mdat (no chunk offsets move).
    /// </summary>
    internal static class Mp4ColorTag
    {
        // size, 'colr', 'nclx', primaries 1 (BT.709), transfer 1, matrix 1, limited range
        private static readonly byte[] ColrBt709 = { 0, 0, 0, 19, (byte)'c', (byte)'o', (byte)'l', (byte)'r', (byte)'n', (byte)'c', (byte)'l', (byte)'x', 0, 1, 0, 1, 0, 1, 0 };
        private static readonly string[] SampleEntryPath = { "moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1" };

        /// <summary>Returns true when the box was added; false (file unchanged) when the layout is not the expected one.</summary>
        internal static bool TryTagBt709(string path)
        {
            var data = File.ReadAllBytes(path);
            var tagged = Tag(data);
            if (tagged == null) return false;
            var temp = path + ".colr.tmp";
            File.WriteAllBytes(temp, tagged);
            File.Copy(temp, path, true);
            File.Delete(temp);
            return true;
        }

        /// <summary>The tagged file, or null when it is already tagged, uses 64-bit box sizes, has moov before mdat or lacks an avc1 entry.</summary>
        internal static byte[] Tag(byte[] data)
        {
            var parents = new List<int>();
            int offset = 0, end = data.Length;
            long mdat = -1;
            foreach (var name in SampleEntryPath)
            {
                var found = -1;
                for (var o = offset; o + 8 <= end;)
                {
                    var size = ReadSize(data, o);
                    if (size < 8 || o + size > end) return null;
                    var type = Type(data, o);
                    if (name == "moov" && type == "mdat") mdat = o;
                    if (type == name) { found = o; break; }
                    o += (int)size;
                }
                if (found < 0) return null;
                if (name == "moov" && (mdat < 0 || mdat > found)) return null;
                parents.Add(found);
                var foundSize = (int)ReadSize(data, found);
                // stsd has version/flags and an entry count; avc1 is a visual sample entry (78 bytes before its child boxes).
                offset = found + (name == "stsd" ? 16 : 8);
                end = found + foundSize;
            }

            var entry = parents[parents.Count - 1];
            var entryEnd = entry + (int)ReadSize(data, entry);
            for (var o = entry + 8 + 78; o + 8 <= entryEnd;)
            {
                var size = ReadSize(data, o);
                if (size < 8) return null;
                if (Type(data, o) == "colr") return null;
                o += (int)size;
            }

            var result = new byte[data.Length + ColrBt709.Length];
            Buffer.BlockCopy(data, 0, result, 0, entryEnd);
            Buffer.BlockCopy(ColrBt709, 0, result, entryEnd, ColrBt709.Length);
            Buffer.BlockCopy(data, entryEnd, result, entryEnd + ColrBt709.Length, data.Length - entryEnd);
            foreach (var parent in parents)
                WriteSize(result, parent, ReadSize(result, parent) + (uint)ColrBt709.Length);
            return result;
        }

        private static uint ReadSize(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        private static void WriteSize(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
        private static string Type(byte[] d, int o) => System.Text.Encoding.ASCII.GetString(d, o + 4, 4);
    }
}
