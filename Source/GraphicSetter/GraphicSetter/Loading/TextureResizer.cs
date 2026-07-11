using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GraphicSetter;

internal static class TextureResizer
{
    public static bool TryResize(ref Texture2D texture, int maxDimension, bool generateMipMaps)
    {
        if (!texture || maxDimension <= 0 || (texture.width <= maxDimension && texture.height <= maxDimension))
            return false;
        if (!texture.isReadable)
            return false;

        int targetWidth;
        int targetHeight;
        if (texture.width >= texture.height)
        {
            targetWidth = maxDimension;
            targetHeight = Math.Max(1, Mathf.RoundToInt(texture.height * (maxDimension / (float)texture.width)));
        }
        else
        {
            targetHeight = maxDimension;
            targetWidth = Math.Max(1, Mathf.RoundToInt(texture.width * (maxDimension / (float)texture.height)));
        }

        targetWidth = NormalizeBlockDimension(targetWidth, maxDimension);
        targetHeight = NormalizeBlockDimension(targetHeight, maxDimension);

        RenderTexture temporary = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D replacement = null;

        try
        {
            // LoadRawTextureData does not upload DDS bytes until Apply is called. Blitting before
            // this point can silently produce an empty or stale resized texture.
            texture.Apply(false, false);

            temporary = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            temporary.filterMode = FilterMode.Bilinear;
            temporary.wrapMode = texture.wrapMode;
            Graphics.Blit(texture, temporary);
            RenderTexture.active = temporary;

            replacement = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, generateMipMaps);
            replacement.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0, false);
            replacement.Apply(generateMipMaps, false);
            replacement.name = texture.name;
            replacement.filterMode = texture.filterMode;
            replacement.wrapMode = texture.wrapMode;
            replacement.anisoLevel = texture.anisoLevel;
            replacement.mipMapBias = texture.mipMapBias;

            Object.DestroyImmediate(texture);
            texture = replacement;
            replacement = null;
            return true;
        }
        catch
        {
            if (replacement)
                Object.DestroyImmediate(replacement);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
            if (temporary)
                RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private static int NormalizeBlockDimension(int value, int maximum)
    {
        if (value < 4)
            return value;

        int normalized = value - value % 4;
        if (normalized < 4)
            normalized = 4;
        return Math.Min(maximum, normalized);
    }
}
