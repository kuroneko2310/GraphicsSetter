using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GraphicSetter;

// DDS importing based on https://github.com/sarbian/DDSLoader/blob/master/DatabaseLoaderTexture_DDS.cs
public static class DDSLoader
{
    private const uint DDSD_MIPMAPCOUNT_BIT = 0x00020000;
    private const uint DDPF_ALPHAPIXELS = 0x00000001;
    private const uint DDPF_ALPHA = 0x00000002;
    private const uint DDPF_FOURCC = 0x00000004;
    private const uint DDPF_RGB = 0x00000040;
    private const uint DDPF_LUMINANCE = 0x00020000;
    private const uint DDSCAPS2_CUBEMAP = 0x00000200;
    private const uint DDSCAPS2_VOLUME = 0x00200000;

    private const uint D3D10ResourceDimensionTexture2D = 3;
    private const uint D3D11ResourceMiscTextureCube = 0x4;

    private const uint DxgiBc1Unorm = 71;
    private const uint DxgiBc1UnormSrgb = 72;
    private const uint DxgiBc3Unorm = 77;
    private const uint DxgiBc3UnormSrgb = 78;
    private const uint DxgiBc4Unorm = 80;
    private const uint DxgiBc5Unorm = 83;
    private const uint DxgiBc7Unorm = 98;
    private const uint DxgiBc7UnormSrgb = 99;

    [ThreadStatic]
    public static string error;

    public static Texture2D LoadDDS(string path, out bool hasMipMaps, bool skipFileCheck = false,
        Func<int, int, bool> shouldKeepMipMaps = null, bool forceLinear = false)
    {
        error = null;
        hasMipMaps = false;

        if (!skipFileCheck && !File.Exists(path))
        {
            error = "File does not exist";
            return null;
        }

        using MemoryMappedFileSpanWrapper memory =
            new(OpenExistingMmf(path), MemoryMappedFileAccess.Read);
        Span<byte> span = memory.GetSpan(0L);
        int index = 0;

        Span<byte> magicBytes = ReadBytes(span, 4, ref index);
        Span<char> fourCC = stackalloc char[4];
        Encoding.ASCII.GetChars(magicBytes, fourCC);

        if (!FourCcEquals(fourCC, "DDS "))
        {
            byte[] magicArray = magicBytes.ToArray();
            error = $"Invalid DDS file. Header starts with '{string.Join("", magicArray.Select(static b => (char)b))}' ({BitConverter.ToString(magicArray)}) instead of 'DDS '.";
            return null;
        }

        uint headerSize = ReadUInt32(span, ref index);
        if (headerSize != 124u)
        {
            error = $"Invalid header size. Expected 124, got {headerSize}";
            return null;
        }

        uint flags = ReadUInt32(span, ref index);
        uint height = ReadUInt32(span, ref index);
        uint width = ReadUInt32(span, ref index);
        ReadUInt32(span, ref index); // pitch or linear size
        uint depth = ReadUInt32(span, ref index);
        uint declaredMipMapCount = ReadUInt32(span, ref index);

        if (width == 0 || height == 0 || width > 32768 || height > 32768)
        {
            error = $"Invalid DDS dimensions {width}x{height}";
            return null;
        }
        if (depth > 1)
        {
            error = $"Volume DDS textures are unsupported (depth={depth})";
            return null;
        }

        uint mipMapCount = (flags & DDSD_MIPMAPCOUNT_BIT) == 0 || declaredMipMapCount == 0
            ? 1u
            : declaredMipMapCount;
        uint maximumMipCount = CalculateMaximumMipCount(width, height);
        if (mipMapCount > maximumMipCount)
        {
            error = $"Invalid DDS mip count {mipMapCount}; maximum for {width}x{height} is {maximumMipCount}";
            return null;
        }

        for (int i = 0; i < 11; i++)
            ReadUInt32(span, ref index);

        uint pixelFormatSize = ReadUInt32(span, ref index);
        if (pixelFormatSize != 32u)
        {
            error = $"Invalid DDS pixel format header size. Expected 32, got {pixelFormatSize}";
            return null;
        }

        uint pixelFlags = ReadUInt32(span, ref index);
        Span<byte> pixelFourCcBytes = ReadBytes(span, 4, ref index);
        Encoding.ASCII.GetChars(pixelFourCcBytes, fourCC);

        uint rgbBitCount = ReadUInt32(span, ref index);
        uint pixelSize = rgbBitCount / 8;
        uint redMask = ReadUInt32(span, ref index);
        uint greenMask = ReadUInt32(span, ref index);
        uint blueMask = ReadUInt32(span, ref index);
        uint alphaMask = ReadUInt32(span, ref index);

        ReadUInt32(span, ref index); // caps
        uint caps2 = ReadUInt32(span, ref index);
        ReadUInt32(span, ref index); // caps3
        ReadUInt32(span, ref index); // caps4
        ReadUInt32(span, ref index); // reserved2

        if ((caps2 & (DDSCAPS2_CUBEMAP | DDSCAPS2_VOLUME)) != 0)
        {
            error = $"Cubemap or volume DDS resources are unsupported (caps2=0x{caps2:X8})";
            return null;
        }

        bool fourcc = (pixelFlags & DDPF_FOURCC) != 0;
        bool compressed = false;
        bool linear = forceLinear;
        bool bgr888 = redMask == 0x00ff0000 && greenMask == 0x0000ff00 && blueMask == 0x000000ff;
        TextureFormat textureFormat;

        if (fourcc)
        {
            compressed = true;
            if (FourCcEquals(fourCC, "DXT1"))
                textureFormat = TextureFormat.DXT1;
            else if (FourCcEquals(fourCC, "DXT5"))
                textureFormat = TextureFormat.DXT5;
            else if (FourCcEquals(fourCC, "ATI1") || FourCcEquals(fourCC, "BC4U"))
            {
                textureFormat = TextureFormat.BC4;
                linear = true;
            }
            else if (FourCcEquals(fourCC, "ATI2") || FourCcEquals(fourCC, "BC5U"))
            {
                textureFormat = TextureFormat.BC5;
                linear = true;
            }
            else if (FourCcEquals(fourCC, "DX10"))
            {
                if (!TryReadDx10Header(span, ref index, out textureFormat, out bool dx10Linear))
                    return null;
                linear = forceLinear || dx10Linear;
            }
            else
            {
                error = $"Unsupported DDS FourCC '{new string(fourCC.ToArray())}'";
                return null;
            }
        }
        else
        {
            bool alpha = (pixelFlags & DDPF_ALPHA) != 0;
            bool rgb = (pixelFlags & DDPF_RGB) != 0;
            bool alphaPixel = (pixelFlags & DDPF_ALPHAPIXELS) != 0;
            bool luminance = (pixelFlags & DDPF_LUMINANCE) != 0;

            bool rgb888 = redMask == 0x000000ff && greenMask == 0x0000ff00 && blueMask == 0x00ff0000;
            bool rgb565 = redMask == 0x0000F800 && greenMask == 0x000007E0 && blueMask == 0x0000001F;
            bool argb4444 = alphaMask == 0x0000f000 && redMask == 0x00000f00 && greenMask == 0x000000f0 && blueMask == 0x0000000f;
            bool rgba4444 = alphaMask == 0x0000000f && redMask == 0x0000f000 && greenMask == 0x000000f0 && blueMask == 0x00000f00;

            if (rgb && (rgb888 || bgr888))
                textureFormat = alphaPixel ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            else if (rgb && rgb565)
                textureFormat = TextureFormat.RGB565;
            else if (rgb && alphaPixel && argb4444)
                textureFormat = TextureFormat.ARGB4444;
            else if (rgb && alphaPixel && rgba4444)
                textureFormat = TextureFormat.RGBA4444;
            else if (!rgb && alpha != luminance)
            {
                textureFormat = TextureFormat.Alpha8;
                linear = true;
            }
            else
            {
                error = "Unsupported uncompressed DDS pixel layout";
                return null;
            }
        }

        if (!SystemInfo.SupportsTextureFormat(textureFormat))
        {
            error = $"Texture format {textureFormat} is unsupported on this graphics device";
            return null;
        }

        if (compressed && ((width & 3) != 0 || (height & 3) != 0))
        {
            error = $"Cannot load compressed texture with non-multiple-of-4 dimensions {width}x{height} and format {textureFormat}";
            return null;
        }

        if (shouldKeepMipMaps != null && !shouldKeepMipMaps((int)width, (int)height))
            mipMapCount = 1;

        int payloadOffset = index;
        long requiredBytes = CalculateRequiredPayloadBytes(textureFormat, width, height, mipMapCount, pixelSize);
        if (requiredBytes <= 0 || requiredBytes > int.MaxValue)
        {
            error = $"Invalid DDS payload size {requiredBytes} bytes";
            return null;
        }

        if (payloadOffset < 0 || payloadOffset > span.Length || span.Length - payloadOffset < requiredBytes)
        {
            error = $"Truncated DDS payload. Expected at least {requiredBytes} bytes, found {Math.Max(0, span.Length - payloadOffset)}";
            return null;
        }

        Span<byte> textureBytes = span.Slice(payloadOffset, (int)requiredBytes);
        if (!compressed && bgr888)
        {
            if (pixelSize < 3)
            {
                error = $"Invalid BGR pixel size {pixelSize}";
                return null;
            }

            textureBytes = textureBytes.ToArray();
            for (int i = 0; i + 2 < textureBytes.Length; i += (int)pixelSize)
            {
                byte blue = textureBytes[i];
                textureBytes[i] = textureBytes[i + 2];
                textureBytes[i + 2] = blue;
            }
        }

        try
        {
            hasMipMaps = mipMapCount > 1;
            Texture2D texture = new((int)width, (int)height, textureFormat, hasMipMaps, linear);
            unsafe
            {
                fixed (byte* dataPointer = &textureBytes[0])
                    texture.LoadRawTextureData((IntPtr)dataPointer, textureBytes.Length);
            }
            return texture;
        }
        catch (Exception exception)
        {
            error = $"Exception loading texture with format '{textureFormat}', width '{width}', height '{height}', mipCount '{mipMapCount}', linear '{linear}':\n{exception}";
            return null;
        }
    }

    private static bool TryReadDx10Header(Span<byte> bytes, ref int index, out TextureFormat textureFormat,
        out bool linear)
    {
        textureFormat = default;
        linear = false;
        uint dxgiFormat = ReadUInt32(bytes, ref index);
        uint resourceDimension = ReadUInt32(bytes, ref index);
        uint miscFlag = ReadUInt32(bytes, ref index);
        uint arraySize = ReadUInt32(bytes, ref index);
        ReadUInt32(bytes, ref index);

        if (resourceDimension != D3D10ResourceDimensionTexture2D || arraySize != 1 ||
            (miscFlag & D3D11ResourceMiscTextureCube) != 0)
        {
            error = $"Unsupported DX10 DDS resource: dimension={resourceDimension}, arraySize={arraySize}, miscFlag={miscFlag}";
            return false;
        }

        switch (dxgiFormat)
        {
            case DxgiBc1Unorm:
                textureFormat = TextureFormat.DXT1;
                linear = true;
                return true;
            case DxgiBc1UnormSrgb:
                textureFormat = TextureFormat.DXT1;
                linear = false;
                return true;
            case DxgiBc3Unorm:
                textureFormat = TextureFormat.DXT5;
                linear = true;
                return true;
            case DxgiBc3UnormSrgb:
                textureFormat = TextureFormat.DXT5;
                linear = false;
                return true;
            case DxgiBc4Unorm:
                textureFormat = TextureFormat.BC4;
                linear = true;
                return true;
            case DxgiBc5Unorm:
                textureFormat = TextureFormat.BC5;
                linear = true;
                return true;
            case DxgiBc7Unorm:
                textureFormat = TextureFormat.BC7;
                linear = true;
                return true;
            case DxgiBc7UnormSrgb:
                textureFormat = TextureFormat.BC7;
                linear = false;
                return true;
            default:
                error = $"Unsupported DX10 DDS format DXGI_FORMAT={dxgiFormat}. Supported: BC1, BC3, BC4, BC5 and BC7 UNORM variants.";
                return false;
        }
    }

    private static long CalculateRequiredPayloadBytes(TextureFormat format, uint width, uint height, uint mipCount,
        uint declaredPixelSize)
    {
        int blockBytes = format switch
        {
            TextureFormat.DXT1 or TextureFormat.BC4 => 8,
            TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC7 => 16,
            _ => 0
        };

        int bytesPerPixel = format switch
        {
            TextureFormat.Alpha8 => 1,
            TextureFormat.RGB24 => 3,
            TextureFormat.RGBA32 => 4,
            TextureFormat.RGB565 or TextureFormat.ARGB4444 or TextureFormat.RGBA4444 => 2,
            _ => (int)declaredPixelSize
        };

        long total = 0;
        uint currentWidth = width;
        uint currentHeight = height;
        uint effectiveMipCount = mipCount == 0 ? 1u : mipCount;
        for (uint mip = 0; mip < effectiveMipCount; mip++)
        {
            if (blockBytes > 0)
            {
                long blocksWide = Math.Max(1L, (long)(currentWidth + 3u) / 4L);
                long blocksHigh = Math.Max(1L, (long)(currentHeight + 3u) / 4L);
                total += blocksWide * blocksHigh * blockBytes;
            }
            else
            {
                if (bytesPerPixel <= 0)
                    return -1;
                total += (long)currentWidth * currentHeight * bytesPerPixel;
            }

            currentWidth = currentWidth > 1 ? currentWidth >> 1 : 1u;
            currentHeight = currentHeight > 1 ? currentHeight >> 1 : 1u;
        }

        return total;
    }

    private static uint CalculateMaximumMipCount(uint width, uint height)
    {
        uint levels = 1;
        uint dimension = Math.Max(width, height);
        while (dimension > 1)
        {
            dimension >>= 1;
            levels++;
        }
        return levels;
    }

    private static bool FourCcEquals(Span<char> bytes, string value) => bytes.SequenceEqual(value);

    private static uint ReadUInt32(Span<byte> bytes, ref int index)
    {
        if (index < 0 || index > bytes.Length - sizeof(uint))
            throw new EndOfStreamException("Unexpected end of DDS header");

        uint result = BitConverter.ToUInt32(bytes.Slice(index, sizeof(uint)));
        index += sizeof(uint);
        return result;
    }

    private static Span<byte> ReadBytes(Span<byte> bytes, int count, ref int index)
    {
        if (count < 0 || index < 0 || index > bytes.Length - count)
            throw new EndOfStreamException("Unexpected end of DDS header");

        Span<byte> result = bytes.Slice(index, count);
        index += count;
        return result;
    }

    private static (MemoryMappedFile file, long length) OpenExistingMmf(string path)
    {
        FileInfo info = new(path);
        long length = info.Length;
        return (MemoryMappedFile.CreateFromFile(info.FullName, FileMode.Open, null, length,
            MemoryMappedFileAccess.Read), length);
    }
}
