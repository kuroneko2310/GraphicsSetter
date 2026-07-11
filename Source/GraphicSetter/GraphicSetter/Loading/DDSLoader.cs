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
    private const uint DDPF_NORMAL = 0x80000000;

    private const uint DDSCAPS2_CUBEMAP_MASK = 0x0000FE00;
    private const uint DDSCAPS2_VOLUME = 0x00200000;
    private const uint D3D10_RESOURCE_DIMENSION_TEXTURE2D = 3;
    private const uint DDS_RESOURCE_MISC_TEXTURECUBE = 0x4;

    private const uint DXGI_FORMAT_BC1_UNORM = 71;
    private const uint DXGI_FORMAT_BC1_UNORM_SRGB = 72;
    private const uint DXGI_FORMAT_BC3_UNORM = 77;
    private const uint DXGI_FORMAT_BC3_UNORM_SRGB = 78;
    private const uint DXGI_FORMAT_BC4_UNORM = 80;
    private const uint DXGI_FORMAT_BC5_UNORM = 83;
    private const uint DXGI_FORMAT_BC7_UNORM = 98;
    private const uint DXGI_FORMAT_BC7_UNORM_SRGB = 99;

    private const int LegacyHeaderSize = 128;
    private const int Dx10HeaderSize = 20;

    public static string error;

    public static Texture2D LoadDDS(string path, out bool hasMipMaps, bool skipFileCheck = false)
    {
        error = null;
        hasMipMaps = false;

        if (!skipFileCheck && !File.Exists(path))
        {
            error = "File does not exist";
            return null;
        }

        using var memory = new MemoryMappedFileSpanWrapper(OpenExistingMmf(path), MemoryMappedFileAccess.Read);
        var span = memory.GetSpan(0L);

        if (span.Length < LegacyHeaderSize)
        {
            error = $"Invalid DDS file. Expected at least {LegacyHeaderSize} bytes, got {span.Length}.";
            return null;
        }

        var index = 0;
        var dwMagic = ReadBytes(span, 4, ref index);

        Span<char> fourCC = stackalloc char[4];
        Encoding.ASCII.GetChars(dwMagic, fourCC);

        if (!FourCcEquals(fourCC, "DDS "))
        {
            var dwMagicArray = dwMagic.ToArray();
            error = $"Invalid DDS file. File header starts with '{
                string.Join("", dwMagicArray.Select(static b => (char)b))}' ({
                BitConverter.ToString(dwMagicArray)}) instead of 'DDS '.";
            return null;
        }

        var dwSize = ReadUInt32(span, ref index);
        if (dwSize != 124u)
        {
            error = $"Invalid header size. Expected 124, got {dwSize}.";
            return null;
        }

        var dwFlags = ReadUInt32(span, ref index);
        var dwHeight = ReadUInt32(span, ref index);
        var dwWidth = ReadUInt32(span, ref index);
        _ = ReadUInt32(span, ref index); // dwPitchOrLinearSize
        var dwDepth = ReadUInt32(span, ref index);
        var dwMipMapCount = ReadUInt32(span, ref index);

        if (dwWidth == 0 || dwHeight == 0 || dwWidth > int.MaxValue || dwHeight > int.MaxValue)
        {
            error = $"Invalid DDS dimensions: {dwWidth}x{dwHeight}.";
            return null;
        }

        if ((dwFlags & DDSD_MIPMAPCOUNT_BIT) == 0 || dwMipMapCount == 0)
            dwMipMapCount = 1;

        var maximumMipCount = GetMaximumMipCount((int)dwWidth, (int)dwHeight);
        if (dwMipMapCount > maximumMipCount)
        {
            error = $"Invalid mip count {dwMipMapCount} for {dwWidth}x{dwHeight}; maximum is {maximumMipCount}.";
            return null;
        }

        index += 11 * sizeof(uint); // dwReserved1

        var pixelFormatSize = ReadUInt32(span, ref index);
        if (pixelFormatSize != 32u)
        {
            error = $"Invalid DDS pixel format header size. Expected 32, got {pixelFormatSize}.";
            return null;
        }

        var pixelFormatFlags = ReadUInt32(span, ref index);
        var pixelFormatFourCC = ReadBytes(span, 4, ref index);
        Encoding.ASCII.GetChars(pixelFormatFourCC, fourCC);

        var rgbBitCount = ReadUInt32(span, ref index);
        var redMask = ReadUInt32(span, ref index);
        var greenMask = ReadUInt32(span, ref index);
        var blueMask = ReadUInt32(span, ref index);
        var alphaMask = ReadUInt32(span, ref index);

        _ = ReadUInt32(span, ref index); // dwCaps
        var caps2 = ReadUInt32(span, ref index);
        _ = ReadUInt32(span, ref index); // dwCaps3
        _ = ReadUInt32(span, ref index); // dwCaps4
        _ = ReadUInt32(span, ref index); // dwReserved2

        if (dwDepth > 1 || (caps2 & DDSCAPS2_VOLUME) != 0)
        {
            error = "3D/volume DDS textures are not supported.";
            return null;
        }

        if ((caps2 & DDSCAPS2_CUBEMAP_MASK) != 0)
        {
            error = "Cubemap DDS textures are not supported.";
            return null;
        }

        TextureFormat? textureFormat = null;
        var isCompressed = false;
        var linear = (pixelFormatFlags & DDPF_NORMAL) != 0;
        var hasDx10Header = false;

        var bgr888 = redMask == 0x00ff0000
            && greenMask == 0x0000ff00
            && blueMask == 0x000000ff;

        if ((pixelFormatFlags & DDPF_FOURCC) != 0)
        {
            isCompressed = true;

            if (FourCcEquals(fourCC, "DXT1"))
                textureFormat = TextureFormat.DXT1;
            else if (FourCcEquals(fourCC, "DXT5"))
                textureFormat = TextureFormat.DXT5;
            else if (FourCcEquals(fourCC, "DX10"))
            {
                hasDx10Header = true;
                if (span.Length < LegacyHeaderSize + Dx10HeaderSize)
                {
                    error = "DDS declares a DX10 header, but the 20-byte extension is missing.";
                    return null;
                }

                var dxgiFormat = ReadUInt32(span, ref index);
                var resourceDimension = ReadUInt32(span, ref index);
                var miscFlag = ReadUInt32(span, ref index);
                var arraySize = ReadUInt32(span, ref index);
                _ = ReadUInt32(span, ref index); // miscFlags2

                if (resourceDimension != D3D10_RESOURCE_DIMENSION_TEXTURE2D)
                {
                    error = $"Unsupported DX10 resource dimension {resourceDimension}; only Texture2D is supported.";
                    return null;
                }

                if (arraySize != 1)
                {
                    error = $"Unsupported DX10 texture array size {arraySize}; only a single texture is supported.";
                    return null;
                }

                if ((miscFlag & DDS_RESOURCE_MISC_TEXTURECUBE) != 0)
                {
                    error = "DX10 cubemap DDS textures are not supported.";
                    return null;
                }

                switch (dxgiFormat)
                {
                    case DXGI_FORMAT_BC1_UNORM:
                        textureFormat = TextureFormat.DXT1;
                        linear = true;
                        break;
                    case DXGI_FORMAT_BC1_UNORM_SRGB:
                        textureFormat = TextureFormat.DXT1;
                        linear = false;
                        break;
                    case DXGI_FORMAT_BC3_UNORM:
                        textureFormat = TextureFormat.DXT5;
                        linear = true;
                        break;
                    case DXGI_FORMAT_BC3_UNORM_SRGB:
                        textureFormat = TextureFormat.DXT5;
                        linear = false;
                        break;
                    case DXGI_FORMAT_BC4_UNORM:
                        textureFormat = TextureFormat.BC4;
                        linear = true;
                        break;
                    case DXGI_FORMAT_BC5_UNORM:
                        textureFormat = TextureFormat.BC5;
                        linear = true;
                        break;
                    case DXGI_FORMAT_BC7_UNORM:
                        textureFormat = TextureFormat.BC7;
                        linear = true;
                        break;
                    case DXGI_FORMAT_BC7_UNORM_SRGB:
                        textureFormat = TextureFormat.BC7;
                        linear = false;
                        break;
                    default:
                        error = $"Unsupported DX10 DXGI format {dxgiFormat}. Supported formats are BC1, BC3, BC4, BC5 and BC7 (UNorm/sRGB where applicable).";
                        return null;
                }
            }
        }
        else
        {
            var alpha = (pixelFormatFlags & DDPF_ALPHA) != 0;
            var rgb = (pixelFormatFlags & DDPF_RGB) != 0;
            var alphaPixel = (pixelFormatFlags & DDPF_ALPHAPIXELS) != 0;
            var luminance = (pixelFormatFlags & DDPF_LUMINANCE) != 0;

            var rgb888 = redMask == 0x000000ff
                && greenMask == 0x0000ff00
                && blueMask == 0x00ff0000;

            var rgb565 = redMask == 0x0000F800
                && greenMask == 0x000007E0
                && blueMask == 0x0000001F;

            var argb4444 = alphaMask == 0x0000f000
                && redMask == 0x00000f00
                && greenMask == 0x000000f0
                && blueMask == 0x0000000f;

            var rgba4444 = alphaMask == 0x0000000f
                && redMask == 0x0000f000
                && greenMask == 0x000000f0
                && blueMask == 0x00000f00;

            if (rgb && (rgb888 || bgr888))
                textureFormat = alphaPixel ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            else if (rgb && rgb565)
                textureFormat = TextureFormat.RGB565;
            else if (rgb && alphaPixel && argb4444)
                textureFormat = TextureFormat.ARGB4444;
            else if (rgb && alphaPixel && rgba4444)
                textureFormat = TextureFormat.RGBA4444;
            else if (!rgb && alpha != luminance)
                textureFormat = TextureFormat.Alpha8;
        }

        if (!textureFormat.HasValue)
        {
            error = hasDx10Header
                ? "Unsupported DX10 DDS texture format."
                : "Only BC1/DXT1, BC3/DXT5, BC4, BC5, BC7, A8, RGB24, RGBA32, RGB565, ARGB4444 and RGBA4444 are supported.";
            return null;
        }

        if (!isCompressed && rgbBitCount == 0)
        {
            error = "Uncompressed DDS texture has a zero pixel bit count.";
            return null;
        }

        if (isCompressed && ((dwWidth & 3) != 0 || (dwHeight & 3) != 0))
        {
            error = $"Cannot load compressed texture with non-multiple-of-4 dimensions {dwWidth}x{dwHeight} and format {textureFormat.Value}.";
            return null;
        }

        var expectedDataLength = CalculateDataLength(
            (int)dwWidth,
            (int)dwHeight,
            (int)dwMipMapCount,
            textureFormat.Value);

        if (expectedDataLength <= 0 || expectedDataLength > int.MaxValue)
        {
            error = $"Invalid DDS payload size {expectedDataLength} bytes.";
            return null;
        }

        if (span.Length - index < expectedDataLength)
        {
            error = $"Truncated DDS payload. Expected {expectedDataLength} bytes, found {span.Length - index}.";
            return null;
        }

        var textureBytes = span.Slice(index, (int)expectedDataLength);

        if (!isCompressed && bgr888)
        {
            textureBytes = textureBytes.ToArray();
            var pixelSize = GetBytesPerPixel(textureFormat.Value);

            for (var i = 0; i + 2 < textureBytes.Length; i += pixelSize)
            {
                var blue = textureBytes[i];
                textureBytes[i] = textureBytes[i + 2];
                textureBytes[i + 2] = blue;
            }
        }

        try
        {
            hasMipMaps = dwMipMapCount > 1;
            var texture = new Texture2D(
                (int)dwWidth,
                (int)dwHeight,
                textureFormat.Value,
                hasMipMaps,
                linear);

            unsafe
            {
                fixed (byte* data = &textureBytes[0])
                    texture.LoadRawTextureData((IntPtr)data, textureBytes.Length);
            }

            return texture;
        }
        catch (Exception exception)
        {
            error = $"Exception loading texture with format '{textureFormat.Value}', width '{
                dwWidth}', height '{dwHeight}', mipCount '{dwMipMapCount}':\n{exception}";
            return null;
        }
    }

    private static long CalculateDataLength(int width, int height, int mipCount, TextureFormat format)
    {
        long total = 0;
        var mipWidth = Math.Max(1, width);
        var mipHeight = Math.Max(1, height);

        for (var level = 0; level < mipCount; level++)
        {
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.BC4:
                    total += (long)Math.Max(1, (mipWidth + 3) / 4)
                        * Math.Max(1, (mipHeight + 3) / 4) * 8;
                    break;

                case TextureFormat.DXT5:
                case TextureFormat.BC5:
                case TextureFormat.BC7:
                    total += (long)Math.Max(1, (mipWidth + 3) / 4)
                        * Math.Max(1, (mipHeight + 3) / 4) * 16;
                    break;

                default:
                    total += (long)mipWidth * mipHeight * GetBytesPerPixel(format);
                    break;
            }

            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return total;
    }

    private static int GetBytesPerPixel(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Alpha8 => 1,
            TextureFormat.RGB565 or TextureFormat.ARGB4444 or TextureFormat.RGBA4444 => 2,
            TextureFormat.RGB24 => 3,
            TextureFormat.RGBA32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported uncompressed DDS texture format")
        };
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

    private static bool FourCcEquals(ReadOnlySpan<char> bytes, string value)
        => bytes.SequenceEqual(value.AsSpan());

    private static uint ReadUInt32(Span<byte> bytes, ref int index)
    {
        var result = BitConverter.ToUInt32(bytes.Slice(index, sizeof(uint)));
        index += sizeof(uint);
        return result;
    }

    private static Span<byte> ReadBytes(Span<byte> bytes, int count, ref int index)
    {
        var result = bytes.Slice(index, count);
        index += count;
        return result;
    }

    private static (MemoryMappedFile file, long length) OpenExistingMmf(string path)
    {
        var info = new FileInfo(path);
        var length = info.Length;

        return (MemoryMappedFile.CreateFromFile(info.FullName, FileMode.Open, null, length,
            MemoryMappedFileAccess.Read), length);
    }
}
