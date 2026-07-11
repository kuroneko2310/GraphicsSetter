using System;

namespace GraphicSetter;

internal static class ImageHeaderUtility
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool TryReadPngDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data == null || data.Length < 24)
            return false;

        for (int i = 0; i < PngSignature.Length; i++)
        {
            if (data[i] != PngSignature[i])
                return false;
        }

        width = ReadBigEndianInt32(data, 16);
        height = ReadBigEndianInt32(data, 20);
        return width > 0 && height > 0;
    }

    private static int ReadBigEndianInt32(byte[] data, int offset)
    {
        return (data[offset] << 24)
               | (data[offset + 1] << 16)
               | (data[offset + 2] << 8)
               | data[offset + 3];
    }
}
