// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ImageComposeTests
    {
        // 8x4: left half red, right half blue; top row green.
        private static byte[] TestPng()
        {
            var texture = new Texture2D(8, 4, TextureFormat.RGBA32, false);
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 8; x++)
                    texture.SetPixel(x, y, y == 3 ? Color.green : x < 4 ? Color.red : Color.blue);
            texture.Apply();
            var png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);
            return png;
        }

        private static Color32 PixelAt(byte[] png, int x, int yFromTop)
        {
            var texture = new Texture2D(2, 2);
            texture.LoadImage(png);
            var pixel = texture.GetPixel(x, texture.height - 1 - yFromTop);
            Object.DestroyImmediate(texture);
            return pixel;
        }

        [TestCase("1,2,3,4", true)][TestCase(" 0, 0, 5, 5 ", true)]
        [TestCase("1,2,3", false)][TestCase("a,b,c,d", false)][TestCase("-1,0,5,5", false)][TestCase("0,0,0,5", false)]
        public void RegionParsing(string text, bool valid) => Assert.AreEqual(valid, ImageCompose.TryParseRegion(text, out _));

        [Test]
        public void FitWithinNeverUpscalesAndKeepsAspect()
        {
            Assert.AreEqual(new Vector2Int(960, 540), ImageCompose.FitWithin(1920, 1080, 960));
            Assert.AreEqual(new Vector2Int(100, 50), ImageCompose.FitWithin(100, 50, 960));
            Assert.AreEqual(new Vector2Int(100, 50), ImageCompose.FitWithin(100, 50, 0));
        }

        [Test]
        public void RegionIsTakenFromTheTopLeftAndClippedToTheImage()
        {
            var cropped = ImageCompose.CropAndFit(TestPng(), new RectInt(4, 0, 100, 100), 0, out var size);
            Assert.AreEqual(new Vector2Int(4, 4), size);
            Assert.AreEqual(Color.green, (Color)PixelAt(cropped, 0, 0), "top row is green");
            Assert.AreEqual(Color.blue, (Color)PixelAt(cropped, 0, 2), "right half is blue");
        }

        [Test]
        public void MaxSizeDownscalesTheLongEdge()
        {
            ImageCompose.CropAndFit(TestPng(), null, 4, out var size);
            Assert.AreEqual(new Vector2Int(4, 2), size);
        }

        [Test]
        public void RegionOutsideTheImageIsRejected()
            => Assert.Throws<System.ArgumentException>(() => ImageCompose.CropAndFit(TestPng(), new RectInt(50, 0, 5, 5), 0, out _));

        [Test]
        public void ContactSheetPlacesCellsRowByRow()
        {
            var sheet = ImageCompose.ContactSheet(new List<byte[]> { TestPng(), TestPng(), TestPng() }, 2, 8, out var size);
            Assert.AreEqual(new Vector2Int(16, 16), size);
            // Cell 0 sits top-left: its 8x4 image is centred in the 8x8 cell (rows 2..5 from the top).
            Assert.AreEqual(Color.green, (Color)PixelAt(sheet, 0, 2));
            Assert.AreEqual(Color.green, (Color)PixelAt(sheet, 8, 2), "cell 1 is to the right of cell 0");
            Assert.AreEqual(Color.green, (Color)PixelAt(sheet, 0, 10), "cell 2 starts the second row");
        }

        [Test]
        public void ContactSheetToolRejectsPathsOutsideTheProject()
        {
            StringAssert.Contains("PATH_NOT_ALLOWED", ScreenshotFunctions.ComposeContactSheet("[\"C:/Windows/win.png\"]"));
            StringAssert.Contains("INVALID_PATHS", ScreenshotFunctions.ComposeContactSheet("[]"));
        }
    }
}
