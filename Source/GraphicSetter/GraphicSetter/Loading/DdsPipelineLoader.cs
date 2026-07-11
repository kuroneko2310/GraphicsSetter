using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using UnityEngine;

namespace GraphicSetter;

internal static class DdsPipelineLoader
{
    private const uint FourCcFlag = 0x4;
    private const uint RgbFlag = 0x40;
    private const uint AlphaFlag = 0x2;
    private const uint AlphaPixelsFlag = 0x1;
    private const uint LuminanceFlag = 0x20000;
    private const uint NormalFlag = 0x80000000;

    public static Texture2D Load(string path, int maximumDimension, bool forceNoMipMaps, int disableMipMapsAtOrBelow, out int sourceWidth, out int sourceHeight, out string error)
    {
        sourceWidth = 0;
        sourceHeight = 0;
        error = null;
        try
        {
            using var memory = new MemoryMappedFileSpanWrapper(Open(path), MemoryMappedFileAccess.Read);
            var bytes = memory.GetSpan(0);
            if (bytes.Length < 128 || ReadU32(bytes, 0) != 0x20534444 || ReadU32(bytes, 4) != 124)
                return Fail("Invalid DDS header", out error);

            sourceHeight = checked((int)ReadU32(bytes, 12));
            sourceWidth = checked((int)ReadU32(bytes, 16));
            var mipCount = (int)ReadU32(bytes, 28);
            if (mipCount <= 0) mipCount = 1;
            if (sourceWidth <= 0 || sourceHeight <= 0) return Fail("Invalid DDS dimensions", out error);

            var pixelFlags = ReadU32(bytes, 80);
            var fourCc = Encoding.ASCII.GetString(bytes.Slice(84, 4).ToArray());
            var rgbBits = ReadU32(bytes, 88);
            var red = ReadU32(bytes, 92);
            var green = ReadU32(bytes, 96);
            var blue = ReadU32(bytes, 100);
            var alpha = ReadU32(bytes, 104);
            var dataOffset = 128;
            var linear = (pixelFlags & NormalFlag) != 0;
            var compressed = false;
            var bgr = red == 0x00ff0000 && green == 0x0000ff00 && blue == 0x000000ff;
            TextureFormat format;

            if ((pixelFlags & FourCcFlag) != 0)
            {
                compressed = true;
                if (fourCc == "DXT1") format = TextureFormat.DXT1;
                else if (fourCc == "DXT5") format = TextureFormat.DXT5;
                else if (fourCc == "DX10")
                {
                    if (bytes.Length < 148) return Fail("Missing DX10 header", out error);
                    var dxgi = ReadU32(bytes, 128);
                    var dimension = ReadU32(bytes, 132);
                    var misc = ReadU32(bytes, 136);
                    var arraySize = ReadU32(bytes, 140);
                    if (dimension != 3 || arraySize != 1 || (misc & 4) != 0)
                        return Fail("DDS arrays, cubemaps and non-2D resources are unsupported", out error);
                    dataOffset = 148;
                    switch (dxgi)
                    {
                        case 71: format = TextureFormat.DXT1; linear = true; break;
                        case 72: format = TextureFormat.DXT1; linear = false; break;
                        case 77: format = TextureFormat.DXT5; linear = true; break;
                        case 78: format = TextureFormat.DXT5; linear = false; break;
                        case 80: format = TextureFormat.BC4; linear = true; break;
                        case 83: format = TextureFormat.BC5; linear = true; break;
                        case 98: format = TextureFormat.BC7; linear = true; break;
                        case 99: format = TextureFormat.BC7; linear = false; break;
                        default: return Fail($"Unsupported DXGI format {dxgi}", out error);
                    }
                }
                else return Fail($"Unsupported DDS FourCC {fourCc}", out error);
            }
            else
            {
                var hasRgb = (pixelFlags & RgbFlag) != 0;
                var hasAlphaPixel = (pixelFlags & AlphaPixelsFlag) != 0;
                var rgb = red == 0x000000ff && green == 0x0000ff00 && blue == 0x00ff0000;
                var rgb565 = red == 0x0000f800 && green == 0x000007e0 && blue == 0x0000001f;
                var argb4444 = alpha == 0x0000f000 && red == 0x00000f00 && green == 0x000000f0 && blue == 0x0000000f;
                var rgba4444 = alpha == 0x0000000f && red == 0x0000f000 && green == 0x000000f0 && blue == 0x00000f00;
                if (hasRgb && (rgb || bgr)) format = hasAlphaPixel ? TextureFormat.RGBA32 : TextureFormat.RGB24;
                else if (hasRgb && rgb565) format = TextureFormat.RGB565;
                else if (hasRgb && hasAlphaPixel && argb4444) format = TextureFormat.ARGB4444;
                else if (hasRgb && hasAlphaPixel && rgba4444) format = TextureFormat.RGBA4444;
                else if (!hasRgb && ((pixelFlags & AlphaFlag) != 0) != ((pixelFlags & LuminanceFlag) != 0)) format = TextureFormat.Alpha8;
                else return Fail($"Unsupported uncompressed DDS format ({rgbBits} bits)", out error);
            }

            var fullLength = ChainBytes(sourceWidth, sourceHeight, mipCount, format);
            if (fullLength <= 0 || dataOffset + fullLength > bytes.Length) return Fail("Truncated DDS payload", out error);
            var width = sourceWidth;
            var height = sourceHeight;
            var selectedMips = mipCount;
            var selectedOffset = dataOffset;
            while (maximumDimension > 0 && Math.Max(width, height) > maximumDimension && selectedMips > 1)
            {
                selectedOffset += checked((int)LevelBytes(width, height, format));
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
                selectedMips--;
            }
            if (forceNoMipMaps || Math.Max(width, height) <= Math.Max(0, disableMipMapsAtOrBelow)) selectedMips = 1;
            if (compressed && ((width & 3) != 0 || (height & 3) != 0)) return Fail("Selected compressed DDS dimensions must be multiples of four", out error);
            var selectedLength = ChainBytes(width, height, selectedMips, format);
            if (selectedLength <= 0 || selectedOffset + selectedLength > bytes.Length) return Fail("Invalid selected DDS payload", out error);
            var raw = bytes.Slice(selectedOffset, checked((int)selectedLength));
            if (!compressed && bgr)
            {
                raw = raw.ToArray();
                var pixelSize = BytesPerPixel(format);
                for (var index = 0; index + 2 < raw.Length; index += pixelSize)
                {
                    var swap = raw[index]; raw[index] = raw[index + 2]; raw[index + 2] = swap;
                }
            }
            var texture = new Texture2D(width, height, format, selectedMips > 1, linear);
            unsafe
            {
                fixed (byte* pointer = &raw[0]) texture.LoadRawTextureData((IntPtr)pointer, raw.Length);
            }
            return texture;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            return null;
        }
    }

    private static Texture2D Fail(string message, out string error) { error = message; return null; }

    private static long ChainBytes(int width, int height, int mipCount, TextureFormat format)
    {
        long total = 0;
        for (var level = 0; level < mipCount; level++)
        {
            total += LevelBytes(width, height, format);
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
        return total;
    }

    private static long LevelBytes(int width, int height, TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.DXT1:
            case TextureFormat.BC4: return (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;
            case TextureFormat.DXT5:
            case TextureFormat.BC5:
            case TextureFormat.BC7: return (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
            default: return (long)Math.Max(1, width) * Math.Max(1, height) * BytesPerPixel(format);
        }
    }

    private static int BytesPerPixel(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.Alpha8: return 1;
            case TextureFormat.RGB565:
            case TextureFormat.ARGB4444:
            case TextureFormat.RGBA4444: return 2;
            case TextureFormat.RGB24: return 3;
            case TextureFormat.RGBA32: return 4;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static uint ReadU32(Span<byte> bytes, int offset) => BitConverter.ToUInt32(bytes.Slice(offset, 4));

    private static (MemoryMappedFile file, long length) Open(string path)
    {
        var info = new FileInfo(path);
        return (MemoryMappedFile.CreateFromFile(info.FullName, FileMode.Open, null, info.Length, MemoryMappedFileAccess.Read), info.Length);
    }
}
