using System;
using System.IO;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace GraphicSetter;

public class DDSHelper
{
    public static bool TryLoadDDS(VirtualFile file, ref bool rawMipDataFinalized, ref bool linearTexture,
        ref Texture2D texture2D)
    {
        string ddsExtensionPath = Path.ChangeExtension(file.FullPath, ".dds");
        if (!File.Exists(ddsExtensionPath))
            return false;

        bool forceLinear = TexturePolicy.IsDataTexture(file);
        string logWarning = null;
        try
        {
            linearTexture = DDSColorSpaceUtility.IsLinear(ddsExtensionPath, forceLinear);
            texture2D = DDSLoader.LoadDDS(ddsExtensionPath, out bool hasMipMaps, true,
                (width, height) => TexturePolicy.ShouldGenerateMipMaps(file, width, height),
                linearTexture);

            rawMipDataFinalized = texture2D != null;
        }
        catch (Exception exception)
        {
            logWarning = $"Caught exception while loading '{ddsExtensionPath}': {exception}";
        }

        if (!DDSLoader.error.NullOrEmpty())
        {
            string errorText = $"DDS loading failed for '{ddsExtensionPath}': {DDSLoader.error}";
            logWarning = logWarning.NullOrEmpty() ? errorText : logWarning + "\n" + errorText;
            DDSLoader.error = null;
        }

        if (!texture2D)
        {
            rawMipDataFinalized = false;
            linearTexture = false;
            if (GraphicsSettings.mainSettings.verboseLogging || Prefs.LogVerbose)
            {
                Log.Warning(logWarning.NullOrEmpty()
                    ? $"Couldn't load .dds from '{ddsExtensionPath}'. Loading from source image instead."
                    : $"{logWarning}\nLoading from source image instead.");
            }
            return false;
        }

        return true;
    }
}
