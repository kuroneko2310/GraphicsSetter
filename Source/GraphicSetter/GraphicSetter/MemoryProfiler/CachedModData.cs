using System;
using System.Collections.Generic;
using UnityEngine;
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
    internal int TexturesWithMipMaps;

    public void RegisterTexture(Texture2D texture)
    {
        if (!texture)
            return;

        try
        {
            Vector2 textureSize = new(texture.width, texture.height);

            TotalTextureCount++;
            MemoryUsage += TextureMemoryEstimator.Estimate(texture);
            if (texture.mipmapCount > 1)
                TexturesWithMipMaps++;

            if (textureSize.x >= 512 || textureSize.y >= 512)
                TexturesWithoutAtlasCount++;
            else
                TexturesInAtlasCount++;
        }
        catch (Exception ex)
        {
            Log.Error($"Exception registering texture for memory calculation: {ex}");
        }
    }
}
