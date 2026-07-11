using System;
using System.IO;
using System.Text;

namespace GraphicSetter;

internal static class DDSColorSpaceUtility
{
    private const uint DxgiBc1Unorm = 71;
    private const uint DxgiBc1UnormSrgb = 72;
    private const uint DxgiBc3Unorm = 77;
    private const uint DxgiBc3UnormSrgb = 78;
    private const uint DxgiBc4Unorm = 80;
    private const uint DxgiBc5Unorm = 83;
    private const uint DxgiBc7Unorm = 98;
    private const uint DxgiBc7UnormSrgb = 99;

    public static bool IsLinear(string path, bool forceLinear)
    {
        if (forceLinear)
            return true;

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 128)
                return false;

            using BinaryReader reader = new(stream, Encoding.ASCII, false);
            stream.Position = 84;
            string fourCc = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (fourCc is "ATI1" or "ATI2" or "BC4U" or "BC5U")
                return true;
            if (fourCc != "DX10" || stream.Length < 148)
                return false;

            stream.Position = 128;
            uint dxgiFormat = reader.ReadUInt32();
            return dxgiFormat switch
            {
                DxgiBc1Unorm => true,
                DxgiBc1UnormSrgb => false,
                DxgiBc3Unorm => true,
                DxgiBc3UnormSrgb => false,
                DxgiBc4Unorm => true,
                DxgiBc5Unorm => true,
                DxgiBc7Unorm => true,
                DxgiBc7UnormSrgb => false,
                _ => false
            };
        }
        catch
        {
            return forceLinear;
        }
    }
}
