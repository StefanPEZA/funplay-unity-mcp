// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>PNG crop, downscale and grid composition, so an image can be reduced before it costs tokens.</summary>
    internal static class ImageCompose
    {
        /// <summary>Parses "x,y,width,height" (image pixels, top-left origin).</summary>
        internal static bool TryParseRegion(string text, out RectInt region)
        {
            region = default;
            var parts = (text ?? "").Split(',');
            if (parts.Length != 4) return false;
            var v = new int[4];
            for (var i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return false;
            if (v[0] < 0 || v[1] < 0 || v[2] < 1 || v[3] < 1) return false;
            region = new RectInt(v[0], v[1], v[2], v[3]);
            return true;
        }

        /// <summary>Size that fits <paramref name="width"/>x<paramref name="height"/> within maxSize on the long edge; never upscales.</summary>
        internal static Vector2Int FitWithin(int width, int height, int maxSize)
        {
            if (maxSize < 1 || Math.Max(width, height) <= maxSize) return new Vector2Int(width, height);
            var scale = (float)maxSize / Math.Max(width, height);
            return new Vector2Int(Math.Max(1, Mathf.RoundToInt(width * scale)), Math.Max(1, Mathf.RoundToInt(height * scale)));
        }

        /// <summary>Crops (top-left region, clipped to the image) then fits within maxSize (0 = keep size). Returns PNG bytes.</summary>
        internal static byte[] CropAndFit(byte[] png, RectInt? region, int maxSize, out Vector2Int size)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!source.LoadImage(png)) throw new ArgumentException("Not a PNG image.");
                var crop = new RectInt(0, 0, source.width, source.height);
                if (region.HasValue)
                {
                    var r = region.Value;
                    if (r.x >= source.width || r.y >= source.height) throw new ArgumentException("region lies outside the image (" + source.width + "x" + source.height + ").");
                    crop = new RectInt(r.x, r.y, Math.Min(r.width, source.width - r.x), Math.Min(r.height, source.height - r.y));
                }
                size = FitWithin(crop.width, crop.height, maxSize);
                if (crop.width == source.width && crop.height == source.height && size.x == crop.width && size.y == crop.height) return png;
                return Resample(source, crop, size);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        /// <summary>Resamples a top-left rect of <paramref name="source"/> to <paramref name="size"/> (bilinear) and encodes PNG.</summary>
        private static byte[] Resample(Texture2D source, RectInt cropTopLeft, Vector2Int size)
        {
            var target = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var output = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            try
            {
                source.filterMode = FilterMode.Bilinear;
                var scale = new Vector2((float)cropTopLeft.width / source.width, (float)cropTopLeft.height / source.height);
                var offset = new Vector2((float)cropTopLeft.x / source.width, (float)(source.height - cropTopLeft.yMax) / source.height);
                Graphics.Blit(source, target, scale, offset);
                RenderTexture.active = target;
                output.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
                output.Apply();
                return output.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(output);
            }
        }

        internal const int ContactSheetGutter = 4;

        /// <summary>Arranges PNGs row by row in a grid. Each image fits cellMax on its long edge; cells take the largest fitted
        /// width and height (so same-shaped images leave no bars) and are separated by a small gutter. Returns PNG bytes and the sheet size.</summary>
        internal static byte[] ContactSheet(IList<byte[]> pngs, int columns, int cellMax, out Vector2Int sheetSize)
        {
            columns = Mathf.Clamp(columns, 1, Math.Max(1, pngs.Count));
            var rows = (pngs.Count + columns - 1) / columns;
            var cells = new List<Texture2D>();
            Texture2D sheet = null;
            try
            {
                var fits = new List<Vector2Int>();
                for (var i = 0; i < pngs.Count; i++)
                {
                    var cell = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    cells.Add(cell);
                    if (!cell.LoadImage(pngs[i])) throw new ArgumentException("Image " + i + " is not a PNG.");
                    fits.Add(FitWithin(cell.width, cell.height, cellMax));
                }
                var cellSize = new Vector2Int(fits.Max(f => f.x), fits.Max(f => f.y));
                var pitch = cellSize + new Vector2Int(ContactSheetGutter, ContactSheetGutter);
                sheetSize = new Vector2Int(columns * pitch.x - ContactSheetGutter, rows * pitch.y - ContactSheetGutter);
                sheet = new Texture2D(sheetSize.x, sheetSize.y, TextureFormat.RGBA32, false);
                var fill = new Color32[sheetSize.x * sheetSize.y];
                for (var i = 0; i < fill.Length; i++) fill[i] = new Color32(32, 32, 32, 255);
                sheet.SetPixels32(fill);
                for (var i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];
                    var fit = fits[i];
                    var scaled = fit.x == cell.width && fit.y == cell.height ? cell.GetPixels32() : ScaledPixels(cell, fit);
                    var x0 = (i % columns) * pitch.x + (cellSize.x - fit.x) / 2;
                    // Rows run top to bottom; texture rows run bottom to top.
                    var y0 = (rows - 1 - i / columns) * pitch.y + (cellSize.y - fit.y) / 2;
                    sheet.SetPixels32(x0, y0, fit.x, fit.y, scaled);
                }
                sheet.Apply();
                return sheet.EncodeToPNG();
            }
            finally
            {
                foreach (var cell in cells) UnityEngine.Object.DestroyImmediate(cell);
                if (sheet != null) UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        private static Color32[] ScaledPixels(Texture2D source, Vector2Int size)
        {
            var target = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var output = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            try
            {
                source.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                output.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
                return output.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(output);
            }
        }
    }
}
