using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using Verse;

namespace GraphicSetter;

internal class CachedModData
{
    internal readonly ModContentPack ModReference;
    internal readonly Dictionary<string, int> TextureCountByAtlasType = new();
    internal readonly Dictionary<TextureFormat, long> BytesByFormat = new();
    internal readonly Dictionary<TextureFormat, int> CountByFormat = new();
    internal long MemoryUsage;
    internal long MemoryWithMipMaps;
    internal long MemoryWithoutMipMaps;
    internal long OneLevelDownMemory;
    internal int TotalTextureCount;
    internal int TexturesInAtlasCount;
    internal int TexturesWithoutAtlasCount;
    internal int TexturesWithMipMaps;
    internal int TexturesWithoutMipMaps;
    internal int ReadableTextures;

    internal CachedModData(ModContentPack mod)
    {
        ModReference = mod;
    }

    public long PotentialMipSavings => Math.Max(0, MemoryUsage - MemoryWithoutMipMaps);
    public long PotentialOneLevelDownSavings => Math.Max(0, MemoryUsage - OneLevelDownMemory);

    public void RegisterTexture(Texture2D texture)
    {
        if (!texture)
            return;

        try
        {
            var size = EstimateTextureMemorySize(texture);
            TotalTextureCount++;
            MemoryUsage += size;
            CountByFormat[texture.format] = CountByFormat.TryGetValue(texture.format, out var count) ? count + 1 : 1;
            BytesByFormat[texture.format] = BytesByFormat.TryGetValue(texture.format, out var formatBytes) ? formatBytes + size : size;

            var withoutMipMaps = EstimateTextureMemorySize(texture.width, texture.height, 1, texture.format, texture);
            MemoryWithoutMipMaps += withoutMipMaps;
            MemoryWithMipMaps += size;
            if (texture.mipmapCount > 1) TexturesWithMipMaps++;
            else TexturesWithoutMipMaps++;
            if (texture.isReadable) ReadableTextures++;

            var halfWidth = Math.Max(1, texture.width / 2);
            var halfHeight = Math.Max(1, texture.height / 2);
            var halfMips = Math.Max(1, texture.mipmapCount - 1);
            OneLevelDownMemory += EstimateTextureMemorySize(halfWidth, halfHeight, halfMips, texture.format, texture);

            if (texture.width >= 512 || texture.height >= 512) TexturesWithoutAtlasCount++;
            else TexturesInAtlasCount++;
        }
        catch (Exception exception)
        {
            Log.Error($"Exception registering texture for memory calculation: {exception}");
        }
    }

    internal IEnumerable<string> FormatSummary()
    {
        return BytesByFormat.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}: {CountByFormat[pair.Key]} / {MemoryData.MemoryString(pair.Value)}");
    }

    internal static long EstimateTextureMemorySize(Texture2D texture)
    {
        if (!texture)
            return 0;
        return EstimateTextureMemorySize(texture.width, texture.height, Math.Max(1, texture.mipmapCount), texture.format, texture);
    }

    private static long EstimateTextureMemorySize(int width, int height, int mipCount, TextureFormat format, Texture2D fallbackTexture)
    {
        switch (format)
        {
            case TextureFormat.DXT1:
            case TextureFormat.BC4:
                return EstimateBlockCompressed(width, height, mipCount, 8);
            case TextureFormat.DXT5:
            case TextureFormat.BC5:
            case TextureFormat.BC7:
                return EstimateBlockCompressed(width, height, mipCount, 16);
            case TextureFormat.Alpha8:
                return EstimateUncompressed(width, height, mipCount, 1);
            case TextureFormat.RGB565:
            case TextureFormat.ARGB4444:
            case TextureFormat.RGBA4444:
                return EstimateUncompressed(width, height, mipCount, 2);
            case TextureFormat.RGB24:
                return EstimateUncompressed(width, height, mipCount, 3);
            case TextureFormat.RGBA32:
            case TextureFormat.ARGB32:
            case TextureFormat.BGRA32:
                return EstimateUncompressed(width, height, mipCount, 4);
            default:
                if (fallbackTexture)
                    return Profiler.GetRuntimeMemorySizeLong(fallbackTexture);
                return EstimateUncompressed(width, height, mipCount, 4);
        }
    }

    private static long EstimateBlockCompressed(int width, int height, int mipCount, int bytesPerBlock)
    {
        long total = 0;
        var mipWidth = Math.Max(1, width);
        var mipHeight = Math.Max(1, height);
        for (var level = 0; level < mipCount; level++)
        {
            total += (long)Math.Max(1, (mipWidth + 3) / 4) * Math.Max(1, (mipHeight + 3) / 4) * bytesPerBlock;
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
