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
            if (width <= 0 || height <= 0 || mipCount <= 0 || rawLength <= 0 || HeaderSize + (long)rawLength > length)
                return false;

            linear = (flags & FlagLinear) != 0;
            var hasMipMaps = (flags & FlagMipMaps) != 0 && mipCount > 1;
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
}
