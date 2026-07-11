using System;
using System.IO;
using GraphicSetter.Patches;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace GraphicSetter;

public class DDSHelper
{
    public static bool TryLoadDDS(VirtualFile file, ref bool hasMipMapsSet, ref Texture2D texture2D)
    {
        string ddsExtensionPath = Path.ChangeExtension(file.FullPath, ".dds");
        if (!File.Exists(ddsExtensionPath))
            return false;

        bool loadedFromDds = false;
        string logWarning = null;

        try
        {
            texture2D = DDSLoader.LoadDDS(ddsExtensionPath, out hasMipMapsSet, true,
                (width, height) => TexturePolicy.ShouldGenerateMipMaps(file, width, height),
                TexturePolicy.IsDataTexture(file));
        }
        catch (Exception exception)
        {
            logWarning = $"Caught exception while loading '{ddsExtensionPath}': {exception}";
        }

        if (!DDSLoader.error.NullOrEmpty())
        {
            string errorText = $"DDS loading failed for '{ddsExtensionPath}': {DDSLoader.error}";
            if (logWarning.NullOrEmpty())
                logWarning = errorText;
            else
                logWarning += "\n" + errorText;
            DDSLoader.error = null;
        }

        if (!texture2D)
        {
            if (GraphicsSettings.mainSettings.verboseLogging || Prefs.LogVerbose)
            {
                Log.Warning(logWarning.NullOrEmpty()
                    ? $"Couldn't load .dds from '{ddsExtensionPath}'. Loading from source image instead."
                    : $"{logWarning}\nLoading from source image instead.");
            }
        }
        else
        {
            loadedFromDds = true;
            if (!hasMipMapsSet)
                hasMipMapsSet = TextureLoadingPatch.CheckMipMapFix(texture2D, file);
        }

        return loadedFromDds;
    }
}
