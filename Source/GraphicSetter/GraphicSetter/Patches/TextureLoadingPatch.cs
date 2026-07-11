using System;
using System.IO;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld.IO;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace GraphicSetter.Patches;

internal static class TextureLoadingPatch
{
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture", MethodType.Normal)]
    public static class LoadTexture_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(VirtualFile file, ref Texture2D __result)
        {
            if (TryCustomLoad(file, out Texture2D texture))
            {
                __result = texture;
                return false;
            }

            // The custom loader is an optimization, not a hard dependency. If it cannot
            // safely produce a texture, let RimWorld's original loader handle the file.
            return true;
        }

        public static Texture2D CustomLoad(VirtualFile file, bool readable = false)
        {
            return TryCustomLoad(file, out Texture2D texture, readable) ? texture : null;
        }

        public static bool TryCustomLoad(VirtualFile file, out Texture2D texture2D, bool readable = false)
        {
            texture2D = null;
            SettingsGroup settings = GraphicsSettings.mainSettings;

            if (file == null)
                return false;

            try
            {
                bool hasMipMapsSet = false;
                bool loadedFromDds = settings.enableDDSLoading
                                     && DDSHelper.TryLoadDDS(file, ref hasMipMapsSet, ref texture2D);

                if (!texture2D && file.Exists)
                {
                    byte[] data = file.ReadAllBytes();
                    bool generateMipMaps = true;
                    if (ImageHeaderUtility.TryReadPngDimensions(data, out int width, out int height))
                        generateMipMaps = TexturePolicy.ShouldGenerateMipMaps(file, width, height);

                    texture2D = new Texture2D(2, 2, TextureFormat.Alpha8, generateMipMaps);
                    texture2D.LoadImage(data);
                    hasMipMapsSet = !generateMipMaps;

                    if (FixMipMapsIfNeeded(ref texture2D, data, file))
                        hasMipMapsSet = true;
                }

                if (!texture2D)
                    return false;

                texture2D.name = Path.GetFileNameWithoutExtension(file.Name);
                texture2D.anisoLevel = 1;

                int maxDimension = TexturePolicy.ResolveMaxTextureDimension(file);
                bool generateResizedMipMaps = TexturePolicy.ShouldGenerateMipMaps(file, texture2D.width, texture2D.height);
                if (TextureResizer.TryResize(ref texture2D, maxDimension, generateResizedMipMaps))
                    hasMipMapsSet = true;

                if (!loadedFromDds && Prefs.TextureCompression)
                    texture2D.Compress(true);

                texture2D.filterMode = texture2D.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                if (settings.overrideMipMapBias)
                    texture2D.mipMapBias = settings.mipMapBias;

                texture2D.Apply(!hasMipMapsSet, !readable);
                return true;
            }
            catch (Exception exception)
            {
                if (texture2D)
                    Object.DestroyImmediate(texture2D);
                texture2D = null;

                if (settings.verboseLogging || Prefs.LogVerbose)
                    Log.Warning($"[Graphics Settings][{file.Name}] Custom texture load failed; falling back to vanilla. {exception}");
                return false;
            }
        }
    }

    private static bool FixMipMapsIfNeeded(ref Texture2D texture2D, byte[] data, VirtualFile file)
    {
        if (!CheckMipMapFix(texture2D, file))
            return false;

        Object.DestroyImmediate(texture2D);
        texture2D = new Texture2D(2, 2, TextureFormat.Alpha8, false);
        texture2D.LoadImage(data);
        return true;
    }

    public static bool CheckMipMapFix(Texture2D texture2D, VirtualFile file)
    {
        bool needsFix = NeedsMipMapFix(texture2D);
        if (needsFix)
            LogMipMapWarning(texture2D, file);

        return needsFix;
    }

    private static void LogMipMapWarning(Texture2D texture2D, VirtualFile file)
    {
        if (!Prefs.LogVerbose && !GraphicsSettings.mainSettings.verboseLogging)
            return;

        Log.Warning($"Texture does not support mipmapping, dimensions must be divisible by 4 ({texture2D.width}x{texture2D.height}) for '{file.Name}'");
    }

    private static bool NeedsMipMapFix(Texture2D texture2D)
        => ((texture2D.width & 3) != 0) | ((texture2D.height & 3) != 0);
}
