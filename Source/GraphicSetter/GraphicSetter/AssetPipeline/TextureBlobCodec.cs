using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using UnityEngine;

namespace GraphicSetter;

internal static class TextureBlobCodec
{
    private const int Magic = 0x42545347;
    private const int Version = 2;
    private const int HeaderSize = 36;
    private const int FlagMipMaps = 1;
    private const int FlagLinear = 2;
    private const int KnownFlags = FlagMipMaps | FlagLinear;
    private const int MaximumDimension = 32768;

    public static byte[] Encode(Texture2D texture, bool linear)
    {
        if (!texture)
            throw new ArgumentNullException(nameof(texture));

        var raw = texture.GetRawTextureData();
        using var stream = new MemoryStream(HeaderSize + raw.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(texture.width);
        writer.Write(texture.height);
        writer.Write((int)texture.format);
        writer.Write(Math.Max(1, texture.mipmapCount));
        var flags = (texture.mipmapCount > 1 ? FlagMipMaps : 0) | (linear ? FlagLinear : 0);
        writer.Write(flags);
        writer.Write(raw.Length);
        writer.Write(0);
        writer.Write(raw);
        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryLoad(string path, bool readable, out Texture2D texture, out bool linear, out long payloadBytes)
    {
        texture = null;
        linear = false;
        payloadBytes = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        try
        {
            var length = new FileInfo(path).Length;
            if (length < HeaderSize)
                return false;

            using var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (accessor.ReadInt32(0) != Magic || accessor.ReadInt32(4) != Version)
                return false;

            var width = accessor.ReadInt32(8);
            var height = accessor.ReadInt32(12);
            var format = (TextureFormat)accessor.ReadInt32(16);
            var mipCount = accessor.ReadInt32(20);
            var flags = accessor.ReadInt32(24);
            var rawLength = accessor.ReadInt32(28);
            var reserved = accessor.ReadInt32(32);

            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension ||
                mipCount <= 0 || mipCount > GetMaximumMipCount(width, height) ||
                rawLength <= 0 || reserved != 0 || (flags & ~KnownFlags) != 0 ||
                ((flags & FlagMipMaps) != 0) != (mipCount > 1))
                return false;

            var expectedRawLength = GetRawLength(width, height, format, mipCount);
            if (expectedRawLength <= 0 || expectedRawLength != rawLength || HeaderSize + expectedRawLength != length)
                return false;

            linear = (flags & FlagLinear) != 0;
            var hasMipMaps = mipCount > 1;
            texture = new Texture2D(width, height, format, hasMipMaps, linear);

            unsafe
            {
                byte* pointer = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    var rawPointer = pointer + (int)accessor.PointerOffset + HeaderSize;
                    texture.LoadRawTextureData((IntPtr)rawPointer, rawLength);
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            texture.Apply(false, !readable);
            payloadBytes = rawLength;
            return true;
        }
        catch
        {
            if (texture)
                UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
            return false;
        }
    }

    private static long GetRawLength(int width, int height, TextureFormat format, int mipCount)
    {
        long total = 0;
        for (var level = 0; level < mipCount; level++)
        {
            var levelBytes = GetLevelBytes(width, height, format);
            if (levelBytes <= 0 || total > int.MaxValue - levelBytes)
                return -1;
            total += levelBytes;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return total;
    }

    private static long GetLevelBytes(int width, int height, TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.DXT1:
            case TextureFormat.BC4:
                return (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8L;
            case TextureFormat.DXT5:
            case TextureFormat.BC5:
            case TextureFormat.BC7:
                return (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16L;
            case TextureFormat.Alpha8:
                return (long)width * height;
            case TextureFormat.RGB565:
            case TextureFormat.ARGB4444:
            case TextureFormat.RGBA4444:
                return (long)width * height * 2L;
            case TextureFormat.RGB24:
                return (long)width * height * 3L;
            case TextureFormat.RGBA32:
                return (long)width * height * 4L;
            default:
                return -1;
        }
    }

    private static int GetMaximumMipCount(int width, int height)
    {
        var count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }
        return count;
    }
}
