using System;
using RimWorld.IO;
using UnityEngine;

namespace GraphicSetter;

internal static class TextureImageProcessor
{
    public static Texture2D Decode(VirtualFile file, byte[] data, TexturePipelinePolicy policy, SettingsGroup settings, out bool resized, out bool mipless)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentException("Texture source data is empty", nameof(data));

        Texture2D temporary = null;
        try
        {
            temporary = new Texture2D(2, 2, TextureFormat.RGBA32, false, policy.Linear);
            if (!temporary.LoadImage(data, false))
                throw new InvalidOperationException($"Unity could not decode '{file.FullPath}'.");

            var budgetMaximum = VramBudgetManager.FitMaxDimension(temporary.width, temporary.height, policy.MaxDimension, policy.UseMipMaps, settings);
            var targetWidth = temporary.width;
            var targetHeight = temporary.height;
            while (Math.Max(targetWidth, targetHeight) > budgetMaximum)
            {
                targetWidth = Math.Max(1, targetWidth / 2);
                targetHeight = Math.Max(1, targetHeight / 2);
            }

            var useMipMaps = !policy.DisableMipMapsForSize(targetWidth, targetHeight, settings);
            resized = targetWidth != temporary.width || targetHeight != temporary.height;
            mipless = !useMipMaps;

            var pixels = temporary.GetPixels32();
            var width = temporary.width;
            var height = temporary.height;
            while (width != targetWidth || height != targetHeight)
                pixels = DownsampleHalf(pixels, width, height, out width, out height);

            var texture = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, useMipMaps, policy.Linear);
            texture.SetPixels32(pixels);
            texture.Apply(useMipMaps, false);

            if (policy.AllowCompression && Verse.Prefs.TextureCompression && (targetWidth & 3) == 0 && (targetHeight & 3) == 0)
                texture.Compress(policy.HighQualityCompression);

            return texture;
        }
        finally
        {
            if (temporary)
                UnityEngine.Object.DestroyImmediate(temporary);
        }
    }

    private static Color32[] DownsampleHalf(Color32[] source, int sourceWidth, int sourceHeight, out int width, out int height)
    {
        width = Math.Max(1, sourceWidth / 2);
        height = Math.Max(1, sourceHeight / 2);
        var destination = new Color32[width * height];

        for (var y = 0; y < height; y++)
        {
            var sourceY0 = Math.Min(sourceHeight - 1, y * 2);
            var sourceY1 = Math.Min(sourceHeight - 1, sourceY0 + 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX0 = Math.Min(sourceWidth - 1, x * 2);
                var sourceX1 = Math.Min(sourceWidth - 1, sourceX0 + 1);
                var a = source[sourceY0 * sourceWidth + sourceX0];
                var b = source[sourceY0 * sourceWidth + sourceX1];
                var c = source[sourceY1 * sourceWidth + sourceX0];
                var d = source[sourceY1 * sourceWidth + sourceX1];
                destination[y * width + x] = new Color32((byte)((a.r + b.r + c.r + d.r) / 4), (byte)((a.g + b.g + c.g + d.g) / 4), (byte)((a.b + b.b + c.b + d.b) / 4), (byte)((a.a + b.a + c.a + d.a) / 4));
            }
        }

        return destination;
    }
}
