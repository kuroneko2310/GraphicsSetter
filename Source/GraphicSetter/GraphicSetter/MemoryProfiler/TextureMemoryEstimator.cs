using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace GraphicSetter;

internal static class TextureMemoryEstimator
{
    public static long Estimate(Texture2D texture)
    {
        if (!texture)
            return 0;

        if (!TryGetFormatLayout(texture.format, out int bytesPerPixel, out int bytesPerBlock))
        {
            long runtimeSize = Profiler.GetRuntimeMemorySizeLong(texture);
            return Math.Max(0, runtimeSize);
        }

        long total = 0;
        int width = texture.width;
        int height = texture.height;
        int mipCount = Math.Max(1, texture.mipmapCount);

        for (int mip = 0; mip < mipCount; mip++)
        {
            if (bytesPerBlock > 0)
            {
                long blocksWide = Math.Max(1, (width + 3) / 4);
                long blocksHigh = Math.Max(1, (height + 3) / 4);
                total += blocksWide * blocksHigh * bytesPerBlock;
            }
            else
            {
                total += (long)width * height * bytesPerPixel;
            }

            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }

        return total;
    }

    private static bool TryGetFormatLayout(TextureFormat format, out int bytesPerPixel, out int bytesPerBlock)
    {
        bytesPerPixel = 0;
        bytesPerBlock = 0;

        switch (format)
        {
            case TextureFormat.Alpha8:
            case TextureFormat.R8:
                bytesPerPixel = 1;
                return true;
            case TextureFormat.R16:
            case TextureFormat.RG16:
            case TextureFormat.RGB565:
            case TextureFormat.ARGB4444:
            case TextureFormat.RGBA4444:
            case TextureFormat.RHalf:
                bytesPerPixel = 2;
                return true;
            case TextureFormat.RGB24:
                bytesPerPixel = 3;
                return true;
            case TextureFormat.RGBA32:
            case TextureFormat.ARGB32:
            case TextureFormat.BGRA32:
            case TextureFormat.RGHalf:
            case TextureFormat.RFloat:
                bytesPerPixel = 4;
                return true;
            case TextureFormat.RGBAHalf:
            case TextureFormat.RGFloat:
                bytesPerPixel = 8;
                return true;
            case TextureFormat.RGBAFloat:
                bytesPerPixel = 16;
                return true;
            case TextureFormat.DXT1:
            case TextureFormat.BC4:
                bytesPerBlock = 8;
                return true;
            case TextureFormat.DXT5:
            case TextureFormat.BC5:
            case TextureFormat.BC7:
                bytesPerBlock = 16;
                return true;
            default:
                return false;
        }
    }
}
