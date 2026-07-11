using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using Verse;

namespace GraphicSetter;

internal class CachedModData(ModContentPack mod)
{
    internal ModContentPack ModReference = mod;
    internal readonly Dictionary<string, int> TextureCountByAtlasType = new();
    internal long MemoryUsage;
    internal int TotalTextureCount;
    internal int TexturesInAtlasCount;
    internal int TexturesWithoutAtlasCount;

    public void RegisterTexture(Texture2D texture)
    {
        if (!texture)
            return;

        try
        {
            var textureSize = new Vector2(texture.width, texture.height);

            TotalTextureCount++;
            MemoryUsage += EstimateTextureMemorySize(texture);

            if (textureSize.x >= 512 || textureSize.y >= 512)
                TexturesWithoutAtlasCount++;
            else
                TexturesInAtlasCount++;
        }
        catch (Exception ex)
        {
            Log.Error($"Exception registering texture for memory calculation:{ex}");
        }
    }

    private static long EstimateTextureMemorySize(Texture2D texture)
    {
        if (!texture)
            return 0;

        var format = texture.format;
        var mipCount = Math.Max(1, texture.mipmapCount);

        switch (format)
        {
            case TextureFormat.DXT1:
            case TextureFormat.BC4:
                return EstimateBlockCompressed(texture.width, texture.height, mipCount, 8);

            case TextureFormat.DXT5:
            case TextureFormat.BC5:
            case TextureFormat.BC7:
                return EstimateBlockCompressed(texture.width, texture.height, mipCount, 16);

            case TextureFormat.Alpha8:
                return EstimateUncompressed(texture.width, texture.height, mipCount, 1);

            case TextureFormat.RGB565:
            case TextureFormat.ARGB4444:
            case TextureFormat.RGBA4444:
                return EstimateUncompressed(texture.width, texture.height, mipCount, 2);

            case TextureFormat.RGB24:
                return EstimateUncompressed(texture.width, texture.height, mipCount, 3);

            case TextureFormat.RGBA32:
            case TextureFormat.ARGB32:
            case TextureFormat.BGRA32:
                return EstimateUncompressed(texture.width, texture.height, mipCount, 4);

            default:
                if (Prefs.LogVerbose)
                    Log.Warning($"[Graphics Settings] Falling back to Unity's runtime size for unsupported texture format {format}.");
                return Profiler.GetRuntimeMemorySizeLong(texture);
        }
    }

    private static long EstimateBlockCompressed(int width, int height, int mipCount, int bytesPerBlock)
    {
        long total = 0;
        var mipWidth = Math.Max(1, width);
        var mipHeight = Math.Max(1, height);

        for (var level = 0; level < mipCount; level++)
        {
            var blocksWide = Math.Max(1, (mipWidth + 3) / 4);
            var blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
            total += (long)blocksWide * blocksHigh * bytesPerBlock;

            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return total;
    }

    private static long EstimateUncompressed(int width, int height, int mipCount, int bytesPerPixel)
    {
        long total = 0;
        var mipWidth = Math.Max(1, width);
        var mipHeight = Math.Max(1, height);

        for (var level = 0; level < mipCount; level++)
        {
            total += (long)mipWidth * mipHeight * bytesPerPixel;
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return total;
    }
}
